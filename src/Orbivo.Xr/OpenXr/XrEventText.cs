using Orbivo.Core.Localization;

namespace Orbivo.Xr.OpenXr;

/// <summary>
/// German details of headset events for the event log (F51); they stay German in every UI language (docs/DECISIONS.md,
/// M11). Only <see cref="Since"/> is for the operator window and follows the UI language.
/// </summary>
public static class XrEventText
{
    /// <summary>Connection to the runtime and the headset.</summary>
    /// <param name="state">The new state.</param>
    /// <param name="runtime">Runtime and headset, if known.</param>
    /// <param name="wasConnected">A headset was connected before in this run: a loss is a disconnect.</param>
    public static string Connection(XrConnectionState state, XrRuntimeInfo? runtime, bool wasConnected = false)
    {
        string runtimeName = string.IsNullOrEmpty(runtime?.RuntimeName) ? "OpenXR-Runtime" : runtime.RuntimeName;
        return state switch
        {
            XrConnectionState.Starting => "Startet",
            XrConnectionState.LoaderMissing => "OpenXR-Loader fehlt",
            XrConnectionState.RuntimeUnavailable when wasConnected => "Getrennt: Varjo Base läuft nicht mehr",
            XrConnectionState.RuntimeUnavailable => "Kein OpenXR-Runtime erreichbar (läuft Varjo Base?)",
            XrConnectionState.HeadsetUnavailable when wasConnected => $"Getrennt: {runtimeName} findet die Brille nicht mehr",
            XrConnectionState.HeadsetUnavailable => $"{runtimeName}: keine Brille gefunden",
            XrConnectionState.Connecting when wasConnected => $"{runtimeName}: verbinde neu",
            XrConnectionState.Connecting => $"{runtimeName}: verbinde",
            XrConnectionState.Connected when !string.IsNullOrEmpty(runtime?.SystemName) => $"Verbunden: {runtime.SystemName} über {runtimeName}",
            XrConnectionState.Connected => "Verbunden",
            XrConnectionState.Failed => "Fehler, neuer Versuch läuft",
            XrConnectionState.Stopped => "Beendet",
            _ => state.ToString(),
        };
    }

    /// <summary>The runtime moved a reference space, e.g. after a recenter in Varjo Base.</summary>
    public static string ReferenceSpaceChanged(string space) =>
        $"Bezugssystem {space} der Brille geändert (z. B. Recenter in Varjo Base)";

    /// <summary>" since 14:12:03" (" seit 14:12:03") in local time, or with the date after midnight, for status texts.</summary>
    public static string Since(DateTime sinceUtc, DateTime nowUtc)
    {
        if (sinceUtc == default)
        {
            return string.Empty;
        }

        DateTime local = sinceUtc.ToLocalTime();
        string format = local.Date == nowUtc.ToLocalTime().Date ? "HH:mm:ss" : "dd.MM. HH:mm";
        return Loc.Format(CoreText.XrSince, local.ToString(format, System.Globalization.CultureInfo.InvariantCulture));
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
