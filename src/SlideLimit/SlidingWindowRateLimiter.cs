namespace SlideLimit;

/// <summary>
/// Sliding-window-log rate limiter: exact accounting — every admission is remembered and ages
/// out after the window, so the limiter admits at most <c>limit</c> requests in any contiguous
/// span of <c>window</c>, and a slot frees at the precise tick the oldest live entry ages out.
/// Memory per key is proportional to admitted request volume (bounded by <c>limit</c>).
/// </summary>
/// <remarks>
/// <para>
/// All time comes from the <see cref="TimeProvider"/> injected in the constructor; the limiter
/// itself reads no wall clock, which is what makes its tests deterministic to the tick.
/// </para>
/// <para>
/// <b>Clock discipline.</b> Entries are pruned by expiry against the current time, never by
/// position, so an NTP correction that moves the clock backwards cannot resurrect consumed
/// allowance. A backwards step may temporarily make the limiter stricter (fresh entries carry
/// earlier timestamps and therefore expire later), but never more permissive. It never throws.
/// </para>
/// </remarks>
public sealed class SlidingWindowRateLimiter : IRateLimiter
{
    private readonly int _limit;
    private readonly long _windowTicks;
    private readonly TimeProvider _clock;
    private readonly IRateLimitStore _store;

    /// <summary>Creates a limiter backed by the system clock and a private in-memory store.</summary>
    /// <param name="limit">Maximum admissions per key inside any window.</param>
    /// <param name="window">Length of the sliding window; an entry admitted at <c>t</c> releases its slot at <c>t + window</c>.</param>
    public SlidingWindowRateLimiter(int limit, TimeSpan window)
        : this(limit, window, TimeProvider.System, new InMemoryRateLimitStore())
    {
    }

    /// <summary>Creates a limiter on shared storage, for tests (fake clock) and multi-instance processes.</summary>
    /// <param name="limit">Maximum admissions per key inside any window.</param>
    /// <param name="window">Length of the sliding window.</param>
    /// <param name="clock">Time source; every read of "now" goes through it.</param>
    /// <param name="store">Backing state for per-key logs.</param>
    public SlidingWindowRateLimiter(int limit, TimeSpan window, TimeProvider clock, IRateLimitStore store)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(store);

        long windowTicks = window.Ticks;
        if (windowTicks > long.MaxValue / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Window must not exceed ~1460 years: expiry arithmetic would overflow ticks.");
        }

        _limit = limit;
        _windowTicks = windowTicks;
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
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _limit);

        return _store.Apply<SlidingWindowLog, RateLimitResult>(key, current => Admit(current, count));
    }

    // Pure per-attempt decision: prune by expiry, admit if capacity remains, and derive
    // Retry-After from the oldest live entry so the reported wait is exactly the true wait.
    private RateLimitWrite<SlidingWindowLog, RateLimitResult> Admit(SlidingWindowLog? current, int count)
    {
        long nowTicks = _clock.GetUtcNow().UtcTicks;
        long cutoff = nowTicks - _windowTicks; // an entry with tick <= cutoff is out of the window

        long[] entries = current?.EntryTicks ?? [];
        int live = 0;
        long oldest = long.MaxValue;
        for (int i = 0; i < entries.Length; i++)
        {
            long tick = entries[i];
            if (tick <= cutoff)
            {
                continue;
            }

            live++;
            if (tick < oldest)
            {
                oldest = tick;
            }
        }

        bool pruned = live != entries.Length;

        if (live + count <= _limit)
        {
            long[] admitted = new long[live + count];
            int j = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] > cutoff)
                {
                    admitted[j++] = entries[i];
                }
            }

            for (int i = live; i < admitted.Length; i++)
            {
                admitted[i] = nowTicks;
            }

            return new RateLimitWrite<SlidingWindowLog, RateLimitResult>(
                new SlidingWindowLog(admitted),
                RateLimitResult.Allowed(_limit, _limit - admitted.Length));
        }

        // Denied: the next slot frees when the oldest live entry ages out — strictly after
        // cutoff, so the wait is at least one tick. Persist the pruned log so expiry does
        // not have to be recomputed on every rejection.
        long waitTicks = oldest + _windowTicks - nowTicks;
        SlidingWindowLog? state = !pruned
            ? current ?? new SlidingWindowLog(entries)
            : Compact(entries, cutoff);

        return new RateLimitWrite<SlidingWindowLog, RateLimitResult>(
            state,
            RateLimitResult.Denied(_limit, _limit - live, TimeSpan.FromTicks(waitTicks)));
    }

    private static SlidingWindowLog Compact(long[] entries, long cutoff)
    {
        var kept = new long[Count(entries, cutoff)];
        int j = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i] > cutoff)
            {
                kept[j++] = entries[i];
            }
        }

        return new SlidingWindowLog(kept);
    }

    private static int Count(long[] entries, long cutoff)
    {
        int n = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i] > cutoff)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Per-key admission timestamps (UTC ticks). Immutable once constructed.</summary>
    internal sealed record SlidingWindowLog(long[] EntryTicks);
}
