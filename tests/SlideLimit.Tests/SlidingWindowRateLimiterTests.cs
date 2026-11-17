using SlideLimit;

namespace SlideLimit.Tests;

public class SlidingWindowRateLimiterTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (SlidingWindowRateLimiter Limiter, FakeTimeProvider Clock) Create(int limit, TimeSpan? window = null)
    {
        var clock = new FakeTimeProvider(T0);
        var limiter = new SlidingWindowRateLimiter(limit, window ?? Window, clock, new InMemoryRateLimitStore());
        return (limiter, clock);
    }

    [Fact]
    public void AdmitsExactlyLimitRequests_ThenBlocks()
    {
        var (limiter, _) = Create(limit: 3);

        Assert.Equal(RateLimitResult.Allowed(3, 2), limiter.TryAcquire("k"));
        Assert.Equal(RateLimitResult.Allowed(3, 1), limiter.TryAcquire("k"));
        Assert.Equal(RateLimitResult.Allowed(3, 0), limiter.TryAcquire("k"));

        var denied = limiter.TryAcquire("k");
        Assert.False(denied.IsAllowed);
        Assert.Equal(0, denied.Remaining);
        Assert.Equal(3, denied.Limit);
    }

    [Fact]
    public void BlocksOneTickBeforeWindowExpiry_AdmitsOnTheExactTickItExpires()
    {
        var (limiter, clock) = Create(limit: 1);
        Assert.True(limiter.TryAcquire("k").IsAllowed);

        // Entry admitted at T0 leaves the window when cutoff = its tick, i.e. at T0 + window.
        clock.Advance(Window - Tick);
        Assert.False(limiter.TryAcquire("k").IsAllowed);

        clock.Advance(Tick); // now == T0 + window, to the tick
        Assert.True(limiter.TryAcquire("k").IsAllowed);
    }

    [Fact]
    public void EntriesAgeOutIndividually_SlidingNotTumbling()
    {
        var (limiter, clock) = Create(limit: 3);
        Assert.True(limiter.TryAcquire("k").IsAllowed);           // T0
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(limiter.TryAcquire("k").IsAllowed);           // T0+5s
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(limiter.TryAcquire("k").IsAllowed);           // T0+9s
        Assert.False(limiter.TryAcquire("k").IsAllowed);

        clock.Advance(TimeSpan.FromSeconds(1));                   // T0+10s: only the first entry is out
        var after = limiter.TryAcquire("k");
        Assert.True(after.IsAllowed);
        Assert.Equal(0, after.Remaining);
        Assert.False(limiter.TryAcquire("k").IsAllowed);
    }

    [Fact]
    public void Denied_RetryAfterIsTheExactWaitThatFreesASlot()
    {
        var (limiter, clock) = Create(limit: 2);
        limiter.TryAcquire("k");                                  // T0
        clock.Advance(TimeSpan.FromSeconds(3));
        limiter.TryAcquire("k");                                  // T0+3s — window is now full

        var denied = limiter.TryAcquire("k");
        Assert.Equal(TimeSpan.FromSeconds(7), denied.RetryAfter); // oldest (T0) ages out at T0+10s

        clock.Advance(denied.RetryAfter - Tick);
        Assert.False(limiter.TryAcquire("k").IsAllowed, "one tick early must still be blocked");

        clock.Advance(Tick);
        Assert.True(limiter.TryAcquire("k").IsAllowed, "waiting exactly RetryAfter must free the slot");
    }

    [Fact]
    public void RetryAfterShrinksOneForOneWithElapsedTime()
    {
        var (limiter, clock) = Create(limit: 1);
        limiter.TryAcquire("k");

        var first = limiter.TryAcquire("k");
        clock.Advance(TimeSpan.FromSeconds(4));
        var second = limiter.TryAcquire("k");

        Assert.Equal(first.RetryAfter - TimeSpan.FromSeconds(4), second.RetryAfter);
    }

    [Fact]
    public void BurstExactlyAtWindowRollover_IsFullyAdmitted()
    {
        var (limiter, clock) = Create(limit: 5);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire("k").IsAllowed);       // 5 at T0
        }

        Assert.False(limiter.TryAcquire("k").IsAllowed);

        clock.Set(T0 + Window);                                   // rollover instant
        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire("k").IsAllowed, $"rollover admission {i}");
        }

        Assert.False(limiter.TryAcquire("k").IsAllowed);
    }

    [Fact]
    public void MultiCountAcquire_ConsumesAtomicallyAndDeniesWhenShort()
    {
        var (limiter, _) = Create(limit: 5);
        Assert.Equal(5, limiter.TryAcquire("k", 5).Limit);
        Assert.Equal(0, limiter.TryAcquire("k", 5).Remaining);
        Assert.False(limiter.TryAcquire("k", 1).IsAllowed);
    }

    [Fact]
    public void RequestLargerThanLimit_ThrowsInsteadOfDenyingForever()
    {
        var (limiter, _) = Create(limit: 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.TryAcquire("k", 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.TryAcquire("k", 0));
        Assert.Throws<ArgumentNullException>(() => limiter.TryAcquire(null!));
    }

    [Fact]
    public void ExhaustingOneKey_LeavesOthersUntouched()
    {
        var (limiter, _) = Create(limit: 1);
        Assert.True(limiter.TryAcquire("alice").IsAllowed);
        Assert.False(limiter.TryAcquire("alice").IsAllowed);

        Assert.True(limiter.TryAcquire("bob").IsAllowed);
    }

    [Fact]
    public void ClockGoingBackwards_DeniesStrictlyAndNeverThrows()
    {
        var (limiter, clock) = Create(limit: 2);
        Assert.True(limiter.TryAcquire("k").IsAllowed);           // T0
        Assert.True(limiter.TryAcquire("k").IsAllowed);           // T0

        clock.Advance(-TimeSpan.FromSeconds(4));                  // NTP correction: now < entries

        // Documented choice: a backwards clock makes the window stricter, never more generous.
        // Both entries are still live; the oldest ages out only at T0 + window, which is now
        // 14 seconds away from the corrected clock reading.
        var denied = limiter.TryAcquire("k");
        Assert.False(denied.IsAllowed);
        Assert.Equal(0, denied.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(14), denied.RetryAfter);
    }

    [Fact]
    public void Constructor_RejectsNonsenseConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter(0, Window));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter(1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter(1, TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentNullException>(() => new SlidingWindowRateLimiter(1, Window, null!, new InMemoryRateLimitStore()));
        Assert.Throws<ArgumentNullException>(() => new SlidingWindowRateLimiter(1, Window, TimeProvider.System, null!));
    }

    [Fact]
    public void DefaultConstructor_WorksAgainstTheRealClock()
    {
        var limiter = new SlidingWindowRateLimiter(2, TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire("live").IsAllowed);
        Assert.True(limiter.TryAcquire("live").IsAllowed);
        Assert.False(limiter.TryAcquire("live").IsAllowed);
    }
}
