# SlideLimit

Sliding-window-log and token-bucket rate limiting for .NET, behind one small interface — with
pluggable storage and every clock read routed through an injected `TimeProvider`, so the
semantics are testable to the tick.

No timers, no background threads, no wall-clock reads inside the algorithms. NuGet-ready
structure, `net10.0`, nullable reference types on, zero runtime dependencies.

## Which algorithm, and when

The deciding question is **should a quiet period buy a burst?**

| | Sliding-window log | Token bucket |
|---|---|---|
| Model | Remembers every admission; each ages out after the window | A bucket of tokens trickles back; each request spends tokens |
| Burst after quiet time | No — never more than `limit` in any window, evenly or otherwise | Yes — up to `capacity` at once |
| Steady-state rate | `limit` per `window` | `refillAmount` per `refillInterval` |
| Memory per key | O(admissions in window), bounded by `limit` | O(1); a full bucket is dropped from the store |
| Reach for it when | Hard caps: login attempts, API quotas, anti-abuse floors | Player actions, write-behind queues, anything that should absorb spikes but not surges forever |

A password-reset endpoint wants the window: five per hour means five, even after three days of
silence. A movement sync channel wants the bucket: let a reconnecting client drain its backload,
then hold the line at the refill rate.

## Before / after

**Before** — the hand-rolled limiter every codebase has:

```csharp
// Do not do this at home.
private static readonly Dictionary<string, Queue<DateTime>> Hits = new();
private static readonly object Gate = new();

public static bool Allow(string userId)
{
    lock (Gate)
    {
        if (!Hits.TryGetValue(userId, out var q)) q = Hits[userId] = new Queue<DateTime>();
        var now = DateTime.Now;                       // local time, of all things
        while (q.Count > 0 && (now - q.Peek()).TotalMinutes > 1) q.Dequeue();
        if (q.Count >= 5) return false;
        q.Enqueue(now);
        return true;                                  // caller: "try again later"? when, exactly?
    }
}
```

**After** — with SlideLimit:

```csharp
using SlideLimit;

var limiter = new SlidingWindowRateLimiter(limit: 5, window: TimeSpan.FromMinutes(1));

RateLimitResult result = limiter.TryAcquire(userId);
if (!result.IsAllowed)
{
    Response.Headers.RetryAfter = result.RetryAfterSecondsRoundedUp.ToString();
    return StatusCode(429);
}
```

Both algorithms return the same thing, so middleware that sets `Retry-After` is written once:
`IsAllowed`, `Limit`, `Remaining`, and `RetryAfter` — a wait that is exact to the tick, never
optimistic, never a stale guess.

Several limits in one app? Register named policies:

```csharp
var policies = new PolicyRateLimiter()
    .AddSlidingWindow("password-reset", limit: 5, window: TimeSpan.FromMinutes(1))
    .AddTokenBucket("movement", capacity: 60, refillAmount: 20, TimeSpan.FromSeconds(1));

var r = policies.TryAcquire("movement", playerId);   // policies.Get("movement") for a plain IRateLimiter
```

## Public API

That is the whole surface.

| Type | Role |
|---|---|
| `IRateLimiter` | `TryAcquire(key)` / `TryAcquire(key, count)` → `RateLimitResult` |
| `RateLimitResult` | Immutable struct: `IsAllowed`, `Limit`, `Remaining`, `RetryAfter`, `RetryAfterSecondsRoundedUp` |
| `SlidingWindowRateLimiter` | Precise window-log limiter |
| `TokenBucketRateLimiter` | Burst-with-refill limiter |
| `PolicyRateLimiter` | Named policies over one shared store and clock |
| `IRateLimitStore` | Storage abstraction: one atomic read-modify-write primitive, `Apply` |
| `RateLimitWrite<TState,TResult>` | Output of a store operation: state to commit (`null` deletes) + result |
| `InMemoryRateLimitStore` | Process-local store: `ConcurrentDictionary` + CAS retry loop |

Every limiter has a simple constructor (`TimeProvider.System` + private in-memory store) and a
full constructor taking your `TimeProvider` and `IRateLimitStore`.

## Why time is injected

Rate limiters are statements about time, and `DateTime.Now` is a statement you cannot re-run.
Every "now" in this library comes from an injected `TimeProvider`, which is what lets the test
suite assert things that are impossible against wall time:

- admission the exact tick the oldest entry leaves the window (`RetryAfter - 1 tick` still blocks, `+ 1 tick` admits);
- token refill boundaries at tick precision, including sub-second rates;
- 256 threads hammering one key against a frozen clock, with the admitted count asserted **equal** to the limit, replayed ten times;
- NTP corrections stepping the clock backwards.

The choice for a backwards clock: **freeze, never forgive**. The token bucket stops refilling
until real time catches up to its stored reference (no phantom tokens, reference point never
moved back); the window log prunes purely by expiry, so a rewind can make it temporarily
stricter but never more generous. Neither throws. Both behaviors are tested.

## What is implemented vs. what is designed for

**Implemented and tested: the in-memory store, only.** This machine has no Redis, so there is
no Redis adapter here — shipping an untested one would be worse than shipping none.

The store abstraction is deliberately the shape a Redis backend maps onto directly. That is the
obvious next contribution:

- `IRateLimitStore.Apply<TState, TResult>(key, operation)` becomes a `WATCH key` /
  `GET` / run `operation` client-side / `MULTI`+`SET`+`EXEC` optimistic-retry loop — exactly the
  CAS loop `InMemoryRateLimitStore` uses, with the server as the version check. A Lua script
  holding the same state is an equivalent alternative.
- Limiter state (`SlidingWindowLog` as a `long[]` of UTC ticks, `TokenBucketState` as
  `(double, long)`) is plain immutable data designed for JSON round-tripping; write with
  `KeyExpire` set to the policy horizon so idle keys self-evict.
- The retry contract is already documented on the interface: `operation` may run more than once
  and must be pure; only the committing attempt's `RateLimitResult` is returned. `PolicyRateLimiter`
  namespaces keys per policy, so several policies share one Redis connection safely.

Single-process by design: `InMemoryRateLimitStore` state dies with the process, and two web
servers with private stores are two independent limits.

## Concurrency, honestly

`InMemoryRateLimitStore.Apply` is a compare-and-swap loop over a
`ConcurrentDictionary<string, object>`: read state, compute a new immutable state, swap it in
only if nobody wrote in between, otherwise replay. Only the winning attempt's result is
returned. `TryAcquire` on all limiters is safe for concurrent use on the same key.

The proof is in `ConcurrencyTests`: storms of 256 barrier-gated threads on one key, with the
clock frozen so the admitted count is exactly knowable — asserted equal to the limit, never
"at most". Replay ×10 per test. These tests were verified to fail when the CAS loop is
deliberately replaced with a non-atomic get-then-set: admitted 12 vs. expected 10, 21 vs. 8.

## Benchmarks

BenchmarkDotNet 0.15.4, .NET 10.0.2, Apple M4 Pro (arm64, RyuJIT), Release — measured, not
claimed. Deny paths in steady state (window at capacity / bucket empty):

| Method | Mean | Error | Allocated |
|---|---:|---:|---:|
| SlidingWindow_DenyPath_WindowAtCapacity (limit 100) | 70.61 ns | 1.10 ns | 96 B |
| TokenBucket_DenyPath_BucketEmpty (capacity 100) | 40.31 ns | 0.25 ns | 128 B |
| Policy_Dispatch_AddsLookupAndKeyPrefix | 54.95 ns | 0.34 ns | 184 B |

The window cost grows with `limit` (it scans live entries) — another reason the bucket wins for
large allowances. Reproduce with `dotnet run -c Release --project benchmarks/SlideLimit.Benchmarks`.
On this repo's exFAT volume, macOS AppleDouble (`._*`) sidecars can break BenchmarkDotNet's
boilerplate build (`MSB1011`); run it from an APFS working copy if you hit it.

## Repo layout & tests

```
src/SlideLimit            the library
tests/SlideLimit.Tests    xUnit — deterministic clock, tick-precision boundaries, storms
benchmarks/...            BenchmarkDotNet
```

```bash
dotnet test SlideLimit.slnx
```

MIT licensed.
