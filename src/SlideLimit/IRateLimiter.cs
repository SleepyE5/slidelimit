namespace SlideLimit;

/// <summary>
/// Decides, per key, whether a request may proceed. Implemented by
/// <c>SlidingWindowRateLimiter</c> and <c>TokenBucketRateLimiter</c>.
/// </summary>
/// <remarks>
/// Implementations must be safe for concurrent use with the same key.
/// </remarks>
public interface IRateLimiter
{
    /// <summary>Checks and, if allowed, consumes a single unit of allowance for <paramref name="key"/>.</summary>
    /// <param name="key">The identity being limited: a user id, client IP, endpoint, or any caller-chosen string.</param>
    RateLimitResult TryAcquire(string key);

    /// <summary>Checks and, if allowed, consumes <paramref name="count"/> units of allowance atomically for <paramref name="key"/>.</summary>
    /// <param name="key">The identity being limited.</param>
    /// <param name="count">Units to consume; must be between 1 and the policy's configured limit. A request larger than the
    /// limit can never be admitted, so it throws instead of denying.</param>
    RateLimitResult TryAcquire(string key, int count);
}
