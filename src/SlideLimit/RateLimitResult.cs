namespace SlideLimit;

/// <summary>
/// The outcome of a single rate-limit check. Immutable value returned by
/// <see cref="IRateLimiter.TryAcquire(string)"/>.
/// </summary>
/// <remarks>
/// To emit a spec-compliant <c>Retry-After</c> header on a denial, use
/// <see cref="RetryAfterSecondsRoundedUp"/>. The rounded-up value is always safe:
/// waiting the reported duration guarantees the next check for the same request size
/// is allowed.
/// </remarks>
public readonly record struct RateLimitResult
{
    /// <summary>Creates a result. Prefer the <see cref="Allowed"/> and <see cref="Denied"/> factories.</summary>
    public RateLimitResult(bool isAllowed, int limit, int remaining, TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(remaining);
        if (remaining > limit)
        {
            throw new ArgumentOutOfRangeException(nameof(remaining), "Remaining allowance cannot exceed the limit.");
        }

        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter), "RetryAfter cannot be negative.");
        }

        IsAllowed = isAllowed;
        Limit = limit;
        Remaining = remaining;
        RetryAfter = retryAfter;
    }

    /// <summary>Whether this request was admitted.</summary>
    public bool IsAllowed { get; }

    /// <summary>The configured allowance this decision was taken against (window size or bucket capacity).</summary>
    public int Limit { get; }

    /// <summary>
    /// Whole units of allowance left right after this decision. On a denial this is the amount
    /// currently available but insufficient for the requested <c>count</c>.
    /// </summary>
    public int Remaining { get; }

    /// <summary>
    /// How long to wait before retrying; <see cref="TimeSpan.Zero"/> when allowed.
    /// A denial always reports at least one tick.
    /// </summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>
    /// <see cref="RetryAfter"/> rounded up to whole seconds, for the HTTP
    /// <c>Retry-After</c> header. Never under-reports the wait.
    /// </summary>
    public long RetryAfterSecondsRoundedUp => (long)Math.Ceiling(Math.Max(0, RetryAfter.TotalSeconds));

    /// <summary>Creates an admitted result.</summary>
    public static RateLimitResult Allowed(int limit, int remaining) =>
        new(true, limit, remaining, TimeSpan.Zero);

    /// <summary>Creates a denied result carrying how long the caller should wait.</summary>
    public static RateLimitResult Denied(int limit, int remaining, TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retryAfter, TimeSpan.FromTicks(1));
        return new RateLimitResult(false, limit, remaining, retryAfter);
    }
}
