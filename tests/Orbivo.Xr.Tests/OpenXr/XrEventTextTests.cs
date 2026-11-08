using Orbivo.Xr.OpenXr;

namespace Orbivo.Xr.Tests.OpenXr;

/// <summary>German texts of headset events (F51) and the "seit" of the headset status (N3).</summary>
public sealed class XrEventTextTests
{
    private static readonly XrRuntimeInfo Varjo = new("Varjo OpenXR Runtime", "4.4", "Varjo XR-3", 2880, 2720, "R8G8B8A8_UNorm_SRgb", "RTX 5080");

    [Fact]
    public void Connected_NamesHeadsetAndRuntime()
    {
        Assert.Equal("Verbunden: Varjo XR-3 über Varjo OpenXR Runtime", XrEventText.Connection(XrConnectionState.Connected, Varjo));
        Assert.Equal("Verbunden", XrEventText.Connection(XrConnectionState.Connected, null));
    }

    [Theory]
    [InlineData(XrConnectionState.HeadsetUnavailable, false, "Varjo OpenXR Runtime: keine Brille gefunden")]
    [InlineData(XrConnectionState.HeadsetUnavailable, true, "Getrennt: Varjo OpenXR Runtime findet die Brille nicht mehr")]
    [InlineData(XrConnectionState.RuntimeUnavailable, false, "Kein OpenXR-Runtime erreichbar (läuft Varjo Base?)")]
    [InlineData(XrConnectionState.RuntimeUnavailable, true, "Getrennt: Varjo Base läuft nicht mehr")]
    [InlineData(XrConnectionState.Connecting, true, "Varjo OpenXR Runtime: verbinde neu")]
    public void LossAfterAConnection_IsADisconnect(XrConnectionState state, bool wasConnected, string expected)
    {
        Assert.Equal(expected, XrEventText.Connection(state, Varjo, wasConnected));
    }

    [Fact]
    public void Since_ShowsTheLocalTime()
    {
        DateTime now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        DateTime since = now.AddMinutes(-3);
        Assert.Equal(" seit " + since.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), XrEventText.Since(since, now));
        Assert.Equal(string.Empty, XrEventText.Since(default, now));
        Assert.StartsWith(" seit ", XrEventText.Since(now.AddDays(-2), now), StringComparison.Ordinal);
        Assert.Contains(".", XrEventText.Since(now.AddDays(-2), now), StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStates_AreGerman()
    {
        Assert.Equal("Sitzung: aktiv (FOCUSED)", XrEventText.Session(XrSessionState.Focused));
        Assert.Equal("Sitzung: Verbindung geht verloren (LOSS_PENDING)", XrEventText.Session(XrSessionState.LossPending));
    }
}
