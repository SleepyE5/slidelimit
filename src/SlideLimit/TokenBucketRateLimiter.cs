namespace SlideLimit;

/// <summary>
/// Token-bucket rate limiter: state starts full at <c>capacity</c>, each admission removes
/// tokens, and tokens trickle back at <c>refillAmount</c> per <c>refillInterval</c>, capped at
/// capacity. Lets a key burst up to capacity after quiet time while holding the long-run rate.
/// Memory per key is O(1) and a fully refilled bucket is dropped from the store entirely.
/// </summary>
/// <remarks>
/// <para>
/// All time comes from the injected <see cref="TimeProvider"/>; refills are computed lazily
/// from elapsed time, so no background timer exists and correctness does not depend on how
/// often the limiter is called.
/// </para>
/// <para>
/// <b>Clock discipline.</b> If the clock steps backwards (NTP correction), the elapsed time
/// used for refilling is clamped to zero and the stored reference timestamp is not moved back.
/// The bucket therefore pauses refilling until real time catches up: it grants no phantom
/// tokens and never throws.
/// </para>
/// </remarks>
public sealed class TokenBucketRateLimiter : IRateLimiter
{
    private readonly int _capacity;
    private readonly int _refillAmount;
    private readonly long _refillIntervalTicks;
    private readonly TimeProvider _clock;
    private readonly IRateLimitStore _store;

    /// <summary>Creates a limiter backed by the system clock and a private in-memory store.</summary>
    /// <param name="capacity">Maximum burst — the number of tokens a fully refilled bucket holds.</param>
    /// <param name="refillAmount">Tokens added each period.</param>
    /// <param name="refillInterval">Refill period; the steady-state rate is <c>refillAmount / refillInterval</c>.</param>
    public TokenBucketRateLimiter(int capacity, int refillAmount, TimeSpan refillInterval)
        : this(capacity, refillAmount, refillInterval, TimeProvider.System, new InMemoryRateLimitStore())
    {
    }

    /// <summary>Creates a limiter on shared storage, for tests (fake clock) and multi-instance processes.</summary>
    /// <param name="capacity">Maximum burst — the number of tokens a fully refilled bucket holds.</param>
    /// <param name="refillAmount">Tokens added each period.</param>
    /// <param name="refillInterval">Refill period.</param>
    /// <param name="clock">Time source; every read of "now" goes through it.</param>
    /// <param name="store">Backing state for per-key buckets.</param>
    public TokenBucketRateLimiter(int capacity, int refillAmount, TimeSpan refillInterval, TimeProvider clock, IRateLimitStore store)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(refillAmount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(refillInterval, TimeSpan.Zero);
        if (refillInterval.Ticks > long.MaxValue / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(refillInterval), "Refill interval must not exceed ~1460 years: refill arithmetic would overflow ticks.");
        }

        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(store);

        _capacity = capacity;
        _refillAmount = refillAmount;
        _refillIntervalTicks = refillInterval.Ticks;
        _clock = clock;
        _store = store;
    }

    /// <inheritdoc />
    public RateLimitResult TryAcquire(string key) => TryAcquire(key, 1);

    /// <inheritdoc />
    public RateLimitResult TryAcquire(string key, int count)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _capacity);

        return _store.Apply<TokenBucketState, RateLimitResult>(key, current => Admit(current, count));
    }

    // Pure per-attempt decision. Refill arithmetic multiplies before dividing so that whole
    // periods stay exact in double: (elapsed * amount) is an exact integer product for any
    // realistic tick span, and an exact division result is correctly rounded.
    private RateLimitWrite<TokenBucketState, RateLimitResult> Admit(TokenBucketState? current, int count)
    {
        long nowTicks = _clock.GetUtcNow().UtcTicks;

        double tokens;
        long lastRefill;
        if (current is null)
        {
            tokens = _capacity;
            lastRefill = nowTicks;
        }
        else
        {
            long elapsed = nowTicks - current.LastRefillTicks;
            if (elapsed <= 0)
            {
                // Same tick, or clock stepped backwards: freeze the bucket, do not move
                // the reference point into the past.
                tokens = current.Tokens;
                lastRefill = current.LastRefillTicks;
            }
            else
            {
                double refilled = (double)elapsed * _refillAmount / _refillIntervalTicks;
                tokens = Math.Min(_capacity, current.Tokens + refilled);
                lastRefill = nowTicks;
            }
        }

        if (tokens >= count)
        {
            tokens -= count;
            int remaining = (int)Math.Floor(tokens);

            // A full bucket is indistinguishable from no bucket; drop it so idle keys
            // do not consume memory.
            TokenBucketState? state = tokens >= _capacity ? null : new TokenBucketState(tokens, lastRefill);
            return new RateLimitWrite<TokenBucketState, RateLimitResult>(state, RateLimitResult.Allowed(_capacity, remaining));
        }

        double deficit = count - tokens;
        long waitTicks = (long)Math.Ceiling(deficit * _refillIntervalTicks / _refillAmount);
        if (waitTicks < 1)
        {
            waitTicks = 1;
        }

        return new RateLimitWrite<TokenBucketState, RateLimitResult>(
            new TokenBucketState(tokens, lastRefill),
            RateLimitResult.Denied(_capacity, (int)Math.Floor(tokens), TimeSpan.FromTicks(waitTicks)));
    }

    /// <summary>Per-key token count and refill reference (UTC ticks). Immutable once constructed.</summary>
    internal sealed record TokenBucketState(double Tokens, long LastRefillTicks);
}
