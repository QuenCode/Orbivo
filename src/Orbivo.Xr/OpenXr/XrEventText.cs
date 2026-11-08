namespace Orbivo.Xr.OpenXr;

/// <summary>German details of headset events for the event log (F51).</summary>
public static class XrEventText
{
    /// <summary>Connection to the runtime and the headset.</summary>
    public static string Connection(XrConnectionState state, XrRuntimeInfo? runtime)
    {
        string runtimeName = string.IsNullOrEmpty(runtime?.RuntimeName) ? "OpenXR-Runtime" : runtime.RuntimeName;
        return state switch
        {
            XrConnectionState.Starting => "Startet",
            XrConnectionState.LoaderMissing => "OpenXR-Loader fehlt",
            XrConnectionState.RuntimeUnavailable => "Kein OpenXR-Runtime erreichbar (läuft Varjo Base?)",
            XrConnectionState.HeadsetUnavailable => $"{runtimeName}: keine Brille gefunden",
            XrConnectionState.Connecting => $"{runtimeName}: verbinde",
            XrConnectionState.Connected when !string.IsNullOrEmpty(runtime?.SystemName) => $"Verbunden: {runtime.SystemName} über {runtimeName}",
            XrConnectionState.Connected => "Verbunden",
            XrConnectionState.Failed => "Fehler, neuer Versuch läuft",
            XrConnectionState.Stopped => "Beendet",
            _ => state.ToString(),
        };
    }

    /// <summary>State of the OpenXR session.</summary>
    public static string Session(XrSessionState state)
    {
        return state switch
        {
            XrSessionState.None => "Sitzung: keine",
            XrSessionState.Idle => "Sitzung: wartet (IDLE)",
            XrSessionState.Ready => "Sitzung: startet (READY)",
            XrSessionState.Synchronized => "Sitzung: läuft, Bild noch nicht sichtbar (SYNCHRONIZED)",
            XrSessionState.Visible => "Sitzung: sichtbar (VISIBLE)",
            XrSessionState.Focused => "Sitzung: aktiv (FOCUSED)",
            XrSessionState.Stopping => "Sitzung: wird angehalten (STOPPING)",
            XrSessionState.LossPending => "Sitzung: Verbindung geht verloren (LOSS_PENDING)",
            XrSessionState.Exiting => "Sitzung: wird beendet (EXITING)",
            _ => "Sitzung: " + state,
        };
    }
}
