using System.Collections.Concurrent;

namespace SlideLimit;

/// <summary>
/// Default <see cref="IRateLimitStore"/>: process-local state in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>, linearized per key through a
/// compare-and-swap retry loop. Keys are never evicted — state disappears only when a
/// limiter writes <see langword="null"/> — so memory tracks the number of distinct keys seen.
/// Suitable for a single process; share nothing across processes with this store.
/// </summary>
public sealed class InMemoryRateLimitStore : IRateLimitStore
{
    private readonly ConcurrentDictionary<string, object> _state = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public TResult Apply<TState, TResult>(string key, Func<TState?, RateLimitWrite<TState, TResult>> operation)
        where TState : class
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(operation);

        while (true)
        {
            bool had = _state.TryGetValue(key, out object? currentRaw);
            object current = currentRaw!; // non-null whenever had is true
            RateLimitWrite<TState, TResult> write = operation(had ? (TState)current : null);

            bool committed;
            if (write.State is null)
            {
                committed = !had || ((IDictionary<string, object>)_state)
                    .Remove(new KeyValuePair<string, object>(key, current));
            }
            else if (had)
            {
                committed = _state.TryUpdate(key, write.State, current);
            }
            else
            {
                committed = _state.TryAdd(key, write.State);
            }

            if (committed)
            {
                return write.Result;
            }
            // Lost the race: re-read and replay. The operation is pure, and only the
            // committing attempt's result is ever returned.
        }
    }
}
