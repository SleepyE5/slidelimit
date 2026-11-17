using SlideLimit;

namespace SlideLimit.Tests;

public class InMemoryRateLimitStoreTests
{
    private sealed record TestState(int Value);

    [Fact]
    public void Apply_OnMissingKey_PassesNull()
    {
        var store = new InMemoryRateLimitStore();
        TestState? seen = null;

        store.Apply<TestState, int>("absent", current =>
        {
            seen = current;
            return new RateLimitWrite<TestState, int>(new TestState(1), 0);
        });

        Assert.Null(seen);
    }

    [Fact]
    public void Apply_RoundTripsCommittedState()
    {
        var store = new InMemoryRateLimitStore();

        store.Apply<TestState, int>("k", _ => new RateLimitWrite<TestState, int>(new TestState(41), 0));
        int read = store.Apply<TestState, int>("k", current => new RateLimitWrite<TestState, int>(current!, current!.Value));

        Assert.Equal(41, read);
    }

    [Fact]
    public void Apply_NullState_RemovesKey()
    {
        var store = new InMemoryRateLimitStore();

        store.Apply<TestState, int>("k", _ => new RateLimitWrite<TestState, int>(new TestState(7), 0));
        store.Apply<TestState, int>("k", _ => new RateLimitWrite<TestState, int>(null, 0));
        TStateProbe probe = store.Apply<TestState, TStateProbe>("k", current =>
            new RateLimitWrite<TestState, TStateProbe>(null, new TStateProbe(current)));

        Assert.Null(probe.State);
    }

    private sealed record TStateProbe(TestState? State);

    [Fact]
    public void Apply_DoesNotTouchOtherKeys()
    {
        var store = new InMemoryRateLimitStore();

        store.Apply<TestState, int>("a", _ => new RateLimitWrite<TestState, int>(new TestState(1), 0));
        store.Apply<TestState, int>("b", _ => new RateLimitWrite<TestState, int>(new TestState(2), 0));
        store.Apply<TestState, int>("a", _ => new RateLimitWrite<TestState, int>(null, 0));

        int b = store.Apply<TestState, int>("b", current => new RateLimitWrite<TestState, int>(current!, current!.Value));
        Assert.Equal(2, b);
    }

    [Fact]
    public async Task Apply_UnderConcurrentIncrement_EveryCallerObservesADistinctWinner()
    {
        // 512 parallel read-modify-writes on ONE key. If Apply were linearizable, each caller
        // observes exactly one committed increment: the returned values must be precisely
        // 1..512 with no duplicates and no gaps, and the stored state must end at 512.
        var store = new InMemoryRateLimitStore();
        const int workers = 512;
        var results = new int[workers];

        Task[] tasks = new Task[workers];
        for (int i = 0; i < workers; i++)
        {
            int slot = i;
            tasks[i] = Task.Run(() =>
            {
                results[slot] = store.Apply<TestState, int>("counter", current =>
                    new RateLimitWrite<TestState, int>(new TestState((current?.Value ?? 0) + 1), (current?.Value ?? 0) + 1));
            });
        }

        await Task.WhenAll(tasks);

        int final = store.Apply<TestState, int>("counter", current => new RateLimitWrite<TestState, int>(current!, current!.Value));
        Assert.Equal(workers, final);
        Assert.Equal(Enumerable.Range(1, workers).OrderBy(x => x), results.OrderBy(x => x));
    }

    [Fact]
    public void Constructor_RejectsNullArguments()
    {
        var store = new InMemoryRateLimitStore();
        Assert.Throws<ArgumentNullException>(() => store.Apply<TestState, int>(null!, _ => throw new InvalidOperationException()));
        Assert.Throws<ArgumentNullException>(() => store.Apply<TestState, int>("k", null!));
    }
}
