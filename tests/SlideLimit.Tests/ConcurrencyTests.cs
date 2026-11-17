using SlideLimit;

namespace SlideLimit.Tests;

/// <summary>
/// Storm tests: many threads hammering ONE key with the clock frozen, so the admitted count
/// is exactly predictable. A lost update or a torn read-modify-write shows up immediately as
/// admitted &gt; limit. Each test replays many rounds because a race is probabilistic.
/// </summary>
public class ConcurrencyTests
{
    private const int Attackers = 256;
    private const int Rounds = 10;

    [Fact]
    public Task SlidingWindow_StormOnSingleKey_NeverAdmitsMoreThanLimit()
    {
        const int Limit = 10;
        return Replay(round =>
        {
            var clock = new FakeTimeProvider(); // frozen: the window never slides mid-storm
            var limiter = new SlidingWindowRateLimiter(Limit, TimeSpan.FromMinutes(1), clock, new InMemoryRateLimitStore());
            return AssertStormAsync(key => limiter.TryAcquire(key), expectedAdmitted: Limit);
        });
    }

    [Fact]
    public Task SlidingWindow_StormWithMultiCount_NeverAdmitsMoreThanLimit()
    {
        const int Limit = 12;
        return Replay(round =>
        {
            var clock = new FakeTimeProvider();
            var limiter = new SlidingWindowRateLimiter(Limit, TimeSpan.FromMinutes(1), clock, new InMemoryRateLimitStore());

            // Every attacker wants 3 of 12: exactly 4 winners maximum, and losers cannot mix in.
            return AssertStormAsync(key => limiter.TryAcquire(key, 3), expectedAdmitted: 4);
        });
    }

    [Fact]
    public Task TokenBucket_StormOnSingleKey_NeverAdmitsMoreThanCapacity()
    {
        const int Capacity = 8;
        return Replay(round =>
        {
            var clock = new FakeTimeProvider(); // frozen: not one token is ever refilled
            var limiter = new TokenBucketRateLimiter(Capacity, 1, TimeSpan.FromMinutes(1), clock, new InMemoryRateLimitStore());
            return AssertStormAsync(key => limiter.TryAcquire(key), expectedAdmitted: Capacity);
        });
    }

    [Fact]
    public Task TokenBucket_StormWithMultiCount_ConservesTokens()
    {
        return Replay(round =>
        {
            var clock = new FakeTimeProvider();
            var limiter = new TokenBucketRateLimiter(10, 1, TimeSpan.FromMinutes(1), clock, new InMemoryRateLimitStore());

            // 20 attackers wanting 2 tokens from a 10-token bucket: exactly 5 winners, no oversell.
            return AssertStormAsync(
                key => limiter.TryAcquire(key, 2), expectedAdmitted: 5, attackers: 20);
        });
    }

    [Fact]
    public Task PolicyRateLimiter_SharedStoreStorm_KeysStayIndependentUnderLoad()
    {
        return Replay(async round =>
        {
            var clock = new FakeTimeProvider();
            var policies = new PolicyRateLimiter(clock, new InMemoryRateLimitStore())
                .AddSlidingWindow("api", limit: 5, window: TimeSpan.FromMinutes(1));

            string[] keys = ["a", "b", "c", "d", "e"];
            int[] perKey = new int[keys.Length];
            var tasks = new List<Task>();

            for (int k = 0; k < keys.Length; k++)
            {
                int slot = k;
                for (int i = 0; i < 64; i++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        if (policies.TryAcquire("api", keys[slot]).IsAllowed)
                        {
                            Interlocked.Increment(ref perKey[slot]);
                        }
                    }));
                }
            }

            await Task.WhenAll(tasks);

            for (int k = 0; k < keys.Length; k++)
            {
                Assert.Equal(5, perKey[k]); // every partition independently hits exactly its limit
            }
        });
    }

    private static async Task AssertStormAsync(
        Func<string, RateLimitResult> act, int expectedAdmitted, int attackers = Attackers)
    {
        using var ready = new CountdownEvent(attackers);
        using var go = new ManualResetEventSlim();
        int admitted = 0;

        var tasks = new Task[attackers];
        for (int i = 0; i < attackers; i++)
        {
            // LongRunning = one dedicated thread per attacker. Pool threads must not be used:
            // parking 256 of them on the start gate starves the pool and stretches the test
            // from milliseconds to minutes.
            tasks[i] = Task.Factory.StartNew(() =>
            {
                ready.Signal();
                go.Wait(); // every attacker is parked on the gate before any is released
                if (act("hot-key").IsAllowed)
                {
                    Interlocked.Increment(ref admitted);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        ready.Wait();
        go.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(expectedAdmitted, admitted);
    }

    private static Task Replay(Func<int, Task> round)
    {
        // Sequential rounds; each round builds a fresh limiter, so failures also say WHICH round.
        return Run(round);

        static async Task Run(Func<int, Task> body)
        {
            for (int round = 0; round < Rounds; round++)
            {
                try
                {
                    await body(round).ConfigureAwait(false);
                }
                catch (Xunit.Sdk.EqualException ex)
                {
                    throw new InvalidOperationException($"Storm invariant broken in round {round}: {ex.Message}", ex);
                }
            }
        }
    }
}
