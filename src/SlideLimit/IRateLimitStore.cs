namespace SlideLimit;

/// <summary>
/// Pluggable backing state for a rate limiter. The limiters never hold mutable
/// per-key state themselves: every decision is taken inside an atomic
/// read-modify-write cycle provided by the store, which keeps the algorithms
/// pure functions of (previous state, clock) and makes the whole library portable
/// to shared backends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Implementor contract.</b> <see cref="Apply{TState,TResult}"/> must guarantee that the
/// <paramref name="operation"/> of the attempt that commits is the one whose result is returned,
/// even under concurrent calls for the same key. The <paramref name="operation"/> delegate may be
/// invoked more than once (for example, a compare-and-swap retry) and must therefore be pure:
/// no side effects beyond its return value. It is given the current state, or <see langword="null"/>
/// when the key has no state, and returns the new state (<see langword="null"/> removes the key)
/// paired with the caller's result.
/// </para>
/// <para>
/// The only shipped implementation is <see cref="InMemoryRateLimitStore"/>. See the README for how
/// a Redis store maps onto this contract.
/// </para>
/// </remarks>
public interface IRateLimitStore
{
    /// <summary>Atomically reads, transforms, and writes back the state for <paramref name="key"/>.</summary>
    /// <typeparam name="TState">The limiter's state type. Must be treated as an immutable value and
    /// round-tripped faithfully (a shared store may serialize it).</typeparam>
    /// <typeparam name="TResult">Payload produced together with the state — usually <see cref="RateLimitResult"/>.</typeparam>
    /// <param name="key">Partition key; the unit of atomicity.</param>
    /// <param name="operation">Pure transformation from current state to (new state, result).</param>
    TResult Apply<TState, TResult>(string key, Func<TState?, RateLimitWrite<TState, TResult>> operation)
        where TState : class;
}

/// <summary>
/// Output of one <see cref="IRateLimitStore.Apply{TState,TResult}"/> attempt: the state to
/// commit (<see langword="null"/> deletes the key) and the result to hand back if this attempt wins.
/// </summary>
public readonly record struct RateLimitWrite<TState, TResult>(TState? State, TResult Result)
    where TState : class;
