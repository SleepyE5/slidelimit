namespace SlideLimit.Tests;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> for tests: time only moves when the test says so,
/// to the tick. This is the entire reason the library routes every clock read through an
/// injected provider.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private long _utcTicks;

    public FakeTimeProvider(DateTimeOffset start) => _utcTicks = start.UtcTicks;

    public FakeTimeProvider()
        : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() =>
        new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    /// <summary>Moves time forward. Negative values simulate an NTP correction backwards.</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);

    public void Set(DateTimeOffset instant) => Interlocked.Exchange(ref _utcTicks, instant.UtcTicks);
}
