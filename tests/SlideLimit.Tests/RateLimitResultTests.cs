using SlideLimit;

namespace SlideLimit.Tests;

public class RateLimitResultTests
{
    [Fact]
    public void Allowed_HasNoRetryAfterAndCarriesAllowance()
    {
        var result = RateLimitResult.Allowed(limit: 10, remaining: 7);

        Assert.True(result.IsAllowed);
        Assert.Equal(10, result.Limit);
        Assert.Equal(7, result.Remaining);
        Assert.Equal(TimeSpan.Zero, result.RetryAfter);
        Assert.Equal(0, result.RetryAfterSecondsRoundedUp);
    }

    [Fact]
    public void Denied_RetainsWaitAndRoundsRetryAfterUp()
    {
        var result = RateLimitResult.Denied(limit: 10, remaining: 0, TimeSpan.FromMilliseconds(1500));

        Assert.False(result.IsAllowed);
        Assert.Equal(0, result.Remaining);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), result.RetryAfter);
        Assert.Equal(2, result.RetryAfterSecondsRoundedUp);
    }

    [Fact]
    public void Denied_OneTickWaitsOneSecondInHeader()
    {
        // A sub-tick wait must still round UP to 1 second, never down to 0:
        // a Retry-After: 0 on a 429 tells the client to hammer immediately.
        var result = RateLimitResult.Denied(limit: 5, remaining: 0, TimeSpan.FromTicks(1));

        Assert.Equal(1, result.RetryAfterSecondsRoundedUp);
    }

    [Fact]
    public void Constructor_RejectsRemainingAboveLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RateLimitResult(isAllowed: true, limit: 5, remaining: 6, TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_RejectsNegativeRetryAfter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RateLimitResult(isAllowed: false, limit: 5, remaining: 0, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Denied_RejectsZeroWait()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RateLimitResult.Denied(limit: 5, remaining: 0, TimeSpan.Zero));
    }

    [Fact]
    public void IsValueEqual_RecordStructSemantics()
    {
        var a = RateLimitResult.Allowed(10, 3);
        var b = RateLimitResult.Allowed(10, 3);

        Assert.Equal(a, b);
    }
}
