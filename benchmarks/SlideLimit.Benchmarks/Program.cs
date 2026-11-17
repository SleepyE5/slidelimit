using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using SlideLimit;

Summary summary = BenchmarkRunner.Run<RateLimiterBenchmarks>();

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class RateLimiterBenchmarks
{
    private SlidingWindowRateLimiter _window = null!;
    private TokenBucketRateLimiter _bucket = null!;
    private PolicyRateLimiter _policies = null!;

    [GlobalSetup]
    public void Setup()
    {
        _window = new SlidingWindowRateLimiter(100, TimeSpan.FromMinutes(1));
        _bucket = new TokenBucketRateLimiter(100, 5, TimeSpan.FromSeconds(1));
        _policies = new PolicyRateLimiter()
            .AddSlidingWindow("api", 100, TimeSpan.FromMinutes(1))
            .AddTokenBucket("login", 100, 5, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Steady state: the first 100 calls admit, every later call denies against a full
    /// 100-entry log — this measures the deny path with the window at capacity.
    /// </summary>
    [Benchmark]
    public RateLimitResult SlidingWindow_DenyPath_WindowAtCapacity() => _window.TryAcquire("player-1");

    /// <summary>Bucket is drained in the first 100 calls; measures the O(1) deny path.</summary>
    [Benchmark]
    public RateLimitResult TokenBucket_DenyPath_BucketEmpty() => _bucket.TryAcquire("player-1");

    [Benchmark]
    public RateLimitResult Policy_Dispatch_AddsLookupAndKeyPrefix() => _policies.TryAcquire("login", "player-1");
}
