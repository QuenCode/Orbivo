namespace Orbivo.Core.Tests.Playback;

/// <summary>Time that only moves when the test says so. 1 timestamp tick = 1 µs.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    public long Timestamp { get; set; } = 1_000_000;

    public override long TimestampFrequency => 1_000_000;

    public override long GetTimestamp() => Timestamp;

    public void AdvanceMilliseconds(double milliseconds) => Timestamp += (long)(milliseconds * 1000);
}
