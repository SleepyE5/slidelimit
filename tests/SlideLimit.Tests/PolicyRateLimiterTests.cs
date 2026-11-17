using SlideLimit;

namespace SlideLimit.Tests;

public class PolicyRateLimiterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (PolicyRateLimiter Policies, FakeTimeProvider Clock) Create()
    {
        var clock = new FakeTimeProvider(T0);
        var policies = new PolicyRateLimiter(clock, new InMemoryRateLimitStore());
        policies
            .AddSlidingWindow("api", limit: 2, window: TimeSpan.FromSeconds(10))
            .AddTokenBucket("login", capacity: 3, refillAmount: 1, TimeSpan.FromSeconds(2));
        return (policies, clock);
    }

    [Fact]
    public void EachPolicyEnforcesItsOwnLimits()
    {
        var (policies, _) = Create();

        Assert.True(policies.TryAcquire("api", "alice").IsAllowed);
        Assert.True(policies.TryAcquire("api", "alice").IsAllowed);
        var denied = policies.TryAcquire("api", "alice");
        Assert.False(denied.IsAllowed);
        Assert.Equal(TimeSpan.FromSeconds(10), denied.RetryAfter);

        Assert.Equal(RateLimitResult.Allowed(3, 2), policies.TryAcquire("login", "alice")); // burst up front
        Assert.True(policies.TryAcquire("login", "alice", 2).IsAllowed);
        Assert.False(policies.TryAcquire("login", "alice").IsAllowed);
    }

    [Fact]
    public void SameKeyUnderDifferentPolicies_HasIndependentState()
    {
        var (policies, _) = Create();

        Assert.True(policies.TryAcquire("api", "alice").IsAllowed);
        Assert.True(policies.TryAcquire("api", "alice").IsAllowed);
        Assert.False(policies.TryAcquire("api", "alice").IsAllowed);

        // "alice" exhausted on "api" must not affect "login".
        Assert.True(policies.TryAcquire("login", "alice").IsAllowed);
    }

    [Fact]
    public void UnknownPolicy_ThrowsKeyNotFound()
    {
        var (policies, _) = Create();
        Assert.Throws<KeyNotFoundException>(() => policies.TryAcquire("nope", "alice"));
        Assert.Throws<KeyNotFoundException>(() => policies.Get("nope"));
    }

    [Fact]
    public void DuplicatePolicyName_Throws()
    {
        var (policies, _) = Create();
        Assert.Throws<InvalidOperationException>(() =>
            policies.AddSlidingWindow("api", limit: 5, window: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void PolicyNames_IsRegistrationOrderSnapshot()
    {
        var (policies, _) = Create();

        IReadOnlyList<string> names = policies.PolicyNames;
        Assert.Equal(["api", "login"], names);

        policies.AddSlidingWindow("extra", limit: 1, window: TimeSpan.FromSeconds(1));
        Assert.Equal(["api", "login"], names); // snapshot does not change
        Assert.Equal(["api", "login", "extra"], policies.PolicyNames);
    }

    [Fact]
    public void Get_ExposesPolicyAsPlainRateLimiter()
    {
        var (policies, clock) = Create();
        IRateLimiter api = policies.Get("api");

        Assert.True(api.TryAcquire("bob").IsAllowed);
        Assert.True(api.TryAcquire("bob").IsAllowed);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(api.TryAcquire("bob").IsAllowed); // window slid
    }

    [Fact]
    public void Constructor_RejectsNulls_AndEmptyPolicyNames()
    {
        Assert.Throws<ArgumentNullException>(() => new PolicyRateLimiter(null!, new InMemoryRateLimitStore()));
        Assert.Throws<ArgumentNullException>(() => new PolicyRateLimiter(TimeProvider.System, null!));

        var policies = new PolicyRateLimiter();
        Assert.Throws<ArgumentException>(() => policies.AddSlidingWindow("", 1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentNullException>(() => policies.AddTokenBucket(null!, 1, 1, TimeSpan.FromSeconds(1)));
    }
}
