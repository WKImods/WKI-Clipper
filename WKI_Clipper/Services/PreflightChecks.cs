using System.Collections.Generic;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

public enum CheckState { Ok, Warn, Fail, Unknown }

/// <summary>One row of the go-live checklist.</summary>
public readonly record struct CheckResult(string Title, CheckState State, string Detail);

/// <summary>
/// Everything the preflight widget needs to know about the app itself, so the
/// evaluation stays a pure function (no OBS/UI/disk access in the test path).
/// </summary>
public readonly record struct PreflightInput(
    bool ObsConnected,
    string? CurrentScene,
    bool Streaming,
    bool ReplayBufferActive,
    bool MicInputKnown,
    bool MicMuted,
    string MicInputName,
    bool ClipperBufferRunning,
    double FreeDiskGb);

/// <summary>
/// Turns the current OBS + clipper state into a traffic-light checklist. Pure so every
/// combination is unit-testable — the widget only renders what this returns.
/// </summary>
public static class PreflightChecks
{
    /// <summary>Below this the recording/replay disk is considered too tight for a stream.</summary>
    public const double LowDiskGb = 10.0;

    public static List<CheckResult> Evaluate(PreflightInput s)
    {
        var list = new List<CheckResult>();

        list.Add(new CheckResult(
            L.T("OBS verbunden", "OBS connected"),
            s.ObsConnected ? CheckState.Ok : CheckState.Fail,
            s.ObsConnected
                ? L.T("WebSocket steht.", "WebSocket is up.")
                : L.T("Keine Verbindung — OBS starten (Werkzeuge → WebSocket-Servereinstellungen).",
                      "No connection — start OBS (Tools → WebSocket Server Settings).")));

        if (!s.ObsConnected)
        {
            // Without OBS the remaining OBS-side checks cannot be judged.
            list.Add(Unknown(L.T("Mikrofon", "Microphone")));
            list.Add(Unknown(L.T("OBS-Replay-Buffer", "OBS replay buffer")));
            list.Add(Unknown(L.T("Aktuelle Szene", "Current scene")));
        }
        else
        {
            list.Add(!s.MicInputKnown
                ? new CheckResult(L.T("Mikrofon", "Microphone"), CheckState.Warn,
                    L.T($"Eingang \"{s.MicInputName}\" nicht gefunden — Name in den Einstellungen prüfen.",
                        $"Input \"{s.MicInputName}\" not found — check the name in settings."))
                : new CheckResult(L.T("Mikrofon", "Microphone"),
                    s.MicMuted ? CheckState.Fail : CheckState.Ok,
                    s.MicMuted
                        ? L.T("Mikrofon ist stummgeschaltet!", "Microphone is muted!")
                        : L.T("Mikrofon ist offen.", "Microphone is live.")));

            list.Add(new CheckResult(L.T("OBS-Replay-Buffer", "OBS replay buffer"),
                s.ReplayBufferActive ? CheckState.Ok : CheckState.Warn,
                s.ReplayBufferActive
                    ? L.T("Läuft.", "Running.")
                    : L.T("Aus — Go-Live schaltet ihn ein.", "Off — Go Live turns it on.")));

            list.Add(new CheckResult(L.T("Aktuelle Szene", "Current scene"),
                string.IsNullOrEmpty(s.CurrentScene) ? CheckState.Unknown : CheckState.Ok,
                s.CurrentScene ?? "—"));
        }

        list.Add(new CheckResult(L.T("Clipper-Replay-Buffer", "Clipper replay buffer"),
            s.ClipperBufferRunning ? CheckState.Ok : CheckState.Warn,
            s.ClipperBufferRunning
                ? L.T("Läuft — F9 speichert.", "Running — F9 saves.")
                : L.T("Aus — Strg+F10 schaltet ihn ein.", "Off — Ctrl+F10 turns it on.")));

        list.Add(new CheckResult(L.T("Freier Speicher", "Free disk space"),
            s.FreeDiskGb <= 0 ? CheckState.Unknown : s.FreeDiskGb < LowDiskGb ? CheckState.Warn : CheckState.Ok,
            s.FreeDiskGb <= 0 ? "—" : $"{s.FreeDiskGb:0.0} GB"));

        // Already live? Then say so instead of pretending this is a pre-check.
        if (s.Streaming)
            list.Insert(1, new CheckResult(L.T("Stream", "Stream"), CheckState.Ok,
                L.T("Du bist LIVE.", "You are LIVE.")));

        return list;
    }

    /// <summary>True when nothing blocks going live (warnings are allowed).</summary>
    public static bool CanGoLive(IReadOnlyList<CheckResult> checks)
    {
        foreach (var c in checks) if (c.State == CheckState.Fail) return false;
        return true;
    }

    private static CheckResult Unknown(string title)
        => new(title, CheckState.Unknown, L.T("Ohne OBS nicht prüfbar.", "Cannot be checked without OBS."));
}
