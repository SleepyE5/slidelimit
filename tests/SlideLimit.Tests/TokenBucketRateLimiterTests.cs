using SlideLimit;

namespace SlideLimit.Tests;

public class TokenBucketRateLimiterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static (TokenBucketRateLimiter Limiter, FakeTimeProvider Clock) Create(
        int capacity, int refillAmount = 1, TimeSpan? refillInterval = null)
    {
        var clock = new FakeTimeProvider(T0);
        var limiter = new TokenBucketRateLimiter(
            capacity, refillAmount, refillInterval ?? Second, clock, new InMemoryRateLimitStore());
        return (limiter, clock);
    }

    [Fact]
    public void FreshBucket_StartsFullAndDrains()
    {
        var (limiter, _) = Create(capacity: 3);

        Assert.Equal(RateLimitResult.Allowed(3, 2), limiter.TryAcquire("k"));
        Assert.Equal(RateLimitResult.Allowed(3, 1), limiter.TryAcquire("k"));
        Assert.Equal(RateLimitResult.Allowed(3, 0), limiter.TryAcquire("k"));

        var denied = limiter.TryAcquire("k");
        Assert.False(denied.IsAllowed);
        Assert.Equal(0, denied.Remaining);
    }

    [Fact]
    public void DrainedBucket_RefillsAtConfiguredRate()
    {
        var (limiter, clock) = Create(capacity: 2, refillAmount: 1, Second);
        limiter.TryAcquire("k");
        limiter.TryAcquire("k");
        Assert.False(limiter.TryAcquire("k").IsAllowed);

        clock.Advance(Second);
        Assert.True(limiter.TryAcquire("k").IsAllowed);
        Assert.False(limiter.TryAcquire("k").IsAllowed);

        clock.Advance(Second);
        Assert.True(limiter.TryAcquire("k").IsAllowed);
    }

    [Fact]
    public void RefillBoundary_BlocksOneTickEarly_AdmitsOnTheExactTick()
    {
        var (limiter, clock) = Create(capacity: 1);
        limiter.TryAcquire("k");

        var denied = limiter.TryAcquire("k");
        Assert.Equal(Second, denied.RetryAfter);

        clock.Advance(denied.RetryAfter - Tick);
        Assert.False(limiter.TryAcquire("k").IsAllowed, "one tick before RetryAfter must still be blocked");

        clock.Advance(Tick);
        Assert.True(limiter.TryAcquire("k").IsAllowed, "waiting exactly RetryAfter must refill the token");
    }

    [Fact]
    public void BurstIsAllowedUpToCapacity_ButNeverBeyond()
    {
        var (limiter, _) = Create(capacity: 5);

        var burst = limiter.TryAcquire("k", 5);
        Assert.True(burst.IsAllowed);
        Assert.Equal(0, burst.Remaining);

        Assert.False(limiter.TryAcquire("k", 1).IsAllowed);
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.TryAcquire("k", 6)); // beyond capacity: impossible by design
    }

    [Fact]
    public void PartialBurstWithRemainder_DeniesUntilRefillCoversTheWholeRequest()
    {
        var (limiter, clock) = Create(capacity: 5);
        limiter.TryAcquire("k", 3);                            // remaining 2

        var denied = limiter.TryAcquire("k", 3);               // 2 < 3
        Assert.False(denied.IsAllowed);
        Assert.Equal(2, denied.Remaining);                     // reports what IS available
        Assert.Equal(Second, denied.RetryAfter);               // one more token at 1/s

        clock.Advance(denied.RetryAfter - Tick);
        Assert.False(limiter.TryAcquire("k", 3).IsAllowed);

        clock.Advance(Tick);
        Assert.True(limiter.TryAcquire("k", 3).IsAllowed);
    }

    [Fact]
    public void TokensNeverAccumulateBeyondCapacity()
    {
        var (limiter, clock) = Create(capacity: 5);
        limiter.TryAcquire("k");                               // 4 left

        clock.Advance(TimeSpan.FromHours(1));                  // way past full

        Assert.True(limiter.TryAcquire("k", 5).IsAllowed);
        Assert.False(limiter.TryAcquire("k", 1).IsAllowed);
    }

    [Fact]
    public void RetryAfterForMultiTokenDemand_ScalesWithDeficit()
    {
        var (limiter, clock) = Create(capacity: 10);
        limiter.TryAcquire("k", 10);                           // empty

        var denied = limiter.TryAcquire("k", 4);               // need 4 at 1/s
        Assert.Equal(TimeSpan.FromSeconds(4), denied.RetryAfter);

        clock.Advance(denied.RetryAfter);
        Assert.True(limiter.TryAcquire("k", 4).IsAllowed);
    }

    [Fact]
    public void SubSecondRefillRate_ReportsWholeSecondHeaderWaitWithoutUnderReporting()
    {
        var (limiter, clock) = Create(capacity: 2, refillAmount: 1, TimeSpan.FromMilliseconds(2500));
        limiter.TryAcquire("k");
        limiter.TryAcquire("k");

        var denied = limiter.TryAcquire("k");
        Assert.Equal(TimeSpan.FromMilliseconds(2500), denied.RetryAfter);
        Assert.Equal(3, denied.RetryAfterSecondsRoundedUp);    // header must round UP, never down

        clock.Advance(TimeSpan.FromMilliseconds(2500) - Tick);
        Assert.False(limiter.TryAcquire("k").IsAllowed);
        clock.Advance(Tick);
        Assert.True(limiter.TryAcquire("k").IsAllowed);
    }

    [Fact]
    public void ExhaustingOneKey_LeavesOthersUntouched()
    {
        var (limiter, _) = Create(capacity: 1);
        Assert.True(limiter.TryAcquire("alice").IsAllowed);
        Assert.False(limiter.TryAcquire("alice").IsAllowed);
        Assert.True(limiter.TryAcquire("bob").IsAllowed);
    }

    [Fact]
    public void ClockGoingBackwards_FreezesRefillAndNeverGrantsPhantomTokens()
    {
        var (limiter, clock) = Create(capacity: 2);
        limiter.TryAcquire("k");
        limiter.TryAcquire("k");
        var denied = limiter.TryAcquire("k");
        Assert.Equal(Second, denied.RetryAfter);               // 1 token short, 1/s

        clock.Advance(-TimeSpan.FromSeconds(30));              // NTP drags now 30s into the past

        var afterCorrection = limiter.TryAcquire("k");
        Assert.False(afterCorrection.IsAllowed, "a backwards clock must not mint tokens");

        clock.Advance(TimeSpan.FromSeconds(30) + Second);      // real time passes the original boundary
        Assert.True(limiter.TryAcquire("k").IsAllowed, "refill resumes from the untouched reference point");
    }

    [Fact]
    public void Constructor_RejectsNonsenseConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketRateLimiter(0, 1, Second));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketRateLimiter(1, 0, Second));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketRateLimiter(1, 1, TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => new TokenBucketRateLimiter(1, 1, Second, null!, new InMemoryRateLimitStore()));
        Assert.Throws<ArgumentNullException>(() => new TokenBucketRateLimiter(1, 1, Second, TimeProvider.System, null!));
    }

    [Fact]
    public void DefaultConstructor_WorksAgainstTheRealClock()
    {
        var limiter = new TokenBucketRateLimiter(2, 1, TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire("live").IsAllowed);
        Assert.True(limiter.TryAcquire("live").IsAllowed);
        Assert.False(limiter.TryAcquire("live").IsAllowed);
    }
}
