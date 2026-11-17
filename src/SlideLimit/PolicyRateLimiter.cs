namespace SlideLimit;

/// <summary>
/// One entry point governing several named policies, each with its own algorithm and limits
/// but sharing a single clock and store. Policies are registered up front
/// (<see cref="AddSlidingWindow"/> / <see cref="AddTokenBucket"/>) and then dispatched by name:
/// <c>TryAcquire("api", userId)</c>. Different policies never share state even for the same key.
/// </summary>
/// <remarks>
/// Instances are safe for concurrent use. Registration may happen at any time but is intended
/// for startup; a policy name is unique and re-registering it throws.
/// </remarks>
public sealed class PolicyRateLimiter
{
    private readonly ConcurrentDictionarySlim _policies = new();
    private readonly TimeProvider _clock;
    private readonly IRateLimitStore _store;

    /// <summary>Creates a policy set on the system clock with a private in-memory store.</summary>
    public PolicyRateLimiter()
        : this(TimeProvider.System, new InMemoryRateLimitStore())
    {
    }

    /// <summary>Creates a policy set on an explicit clock and store.</summary>
    public PolicyRateLimiter(TimeProvider clock, IRateLimitStore store)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(store);
        _clock = clock;
        _store = store;
    }

    /// <summary>Registers a sliding-window policy. Returns this instance for fluent chaining.</summary>
    public PolicyRateLimiter AddSlidingWindow(string policy, int limit, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrEmpty(policy);
        Register(policy, new SlidingWindowRateLimiter(limit, window, _clock, new NamespacedStore(_store, policy)));
        return this;
    }

    /// <summary>Registers a token-bucket policy. Returns this instance for fluent chaining.</summary>
    public PolicyRateLimiter AddTokenBucket(string policy, int capacity, int refillAmount, TimeSpan refillInterval)
    {
        ArgumentException.ThrowIfNullOrEmpty(policy);
        Register(policy, new TokenBucketRateLimiter(capacity, refillAmount, refillInterval, _clock, new NamespacedStore(_store, policy)));
        return this;
    }

    /// <summary>The limiter registered for a policy name.</summary>
    /// <exception cref="KeyNotFoundException">No policy is registered under <paramref name="policy"/>.</exception>
    public IRateLimiter Get(string policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return _policies.TryGet(policy, out IRateLimiter limiter)
            ? limiter
            : throw new KeyNotFoundException($"No rate-limit policy named '{policy}'.");
    }

    /// <summary>Registered policy names, in registration order. The returned collection is a snapshot.</summary>
    public IReadOnlyList<string> PolicyNames => _policies.SnapshotNames();

    /// <summary>Checks one unit of allowance for <paramref name="key"/> under <paramref name="policy"/>.</summary>
    public RateLimitResult TryAcquire(string policy, string key) => Get(policy).TryAcquire(key);

    /// <inheritdoc cref="IRateLimiter.TryAcquire(string, int)"/>
    public RateLimitResult TryAcquire(string policy, string key, int count) => Get(policy).TryAcquire(key, count);

    private void Register(string policy, IRateLimiter limiter)
    {
        if (!_policies.TryAdd(policy, limiter))
        {
            throw new InvalidOperationException($"Rate-limit policy '{policy}' is already registered.");
        }
    }

    /// <summary>
    /// Keeps each policy's keys in its own namespace inside a shared store, so a sliding window
    /// and a token bucket can both track key "alice" without their state types colliding.
    /// </summary>
    private sealed class NamespacedStore : IRateLimitStore
    {
        private const char Separator = '\0';

        private readonly IRateLimitStore _inner;
        private readonly string _prefix;

        public NamespacedStore(IRateLimitStore inner, string policy)
        {
            _inner = inner;
            _prefix = policy + Separator;
        }

        public TResult Apply<TState, TResult>(string key, Func<TState?, RateLimitWrite<TState, TResult>> operation)
            where TState : class
            => _inner.Apply(_prefix + key, operation);
    }

    // Small ordered concurrent registry so PolicyNames stays deterministic.
    private sealed class ConcurrentDictionarySlim
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IRateLimiter> _map = new(StringComparer.Ordinal);
        private readonly Lock _namesLock = new();
        private readonly List<string> _names = [];

        public bool TryAdd(string policy, IRateLimiter limiter)
        {
            lock (_namesLock)
            {
                if (_map.ContainsKey(policy))
                {
                    return false;
                }

                if (!_map.TryAdd(policy, limiter))
                {
                    return false;
                }

                _names.Add(policy);
                return true;
            }
        }

        public bool TryGet(string policy, out IRateLimiter limiter)
        {
            bool found = _map.TryGetValue(policy, out IRateLimiter? value);
            limiter = value!;
            return found;
        }

        public IReadOnlyList<string> SnapshotNames()
        {
            lock (_namesLock)
            {
                return _names.ToArray();
            }
        }
    }
}
