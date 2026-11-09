namespace WKI_Clipper.Services;

/// <summary>A notice about the monitor the buffer records: the fallback warning, or the info that it records the right one again.</summary>
public sealed record CaptureNoticeInfo(string Message, bool IsWarning);

/// <summary>
/// Turns the warning of each capture plan the buffer starts with into at most one notice per transition (decision 4): a
/// warning that appears or changes is announced once, its end once („Aufnahme wieder auf „X““), every restart in between
/// stays silent. Not thread-safe: the buffer calls it under its lifecycle lock.
/// </summary>
internal sealed class CaptureNoticeTracker
{
    // The warning announced last and still standing; null = none.
    private string? _standing;

    /// <returns>The notice <paramref name="plan"/> calls for, or null.</returns>
    public CaptureNoticeInfo? Next(CaptureTargetResolver.CapturePlan plan)
    {
        if (plan.Warning is { } warning)
        {
            if (warning == _standing) return null;
            _standing = warning;
            return new CaptureNoticeInfo(warning, IsWarning: true);
        }

        if (_standing is null) return null;
        _standing = null;
        string name = MonitorLabels.NameOf(plan.Source.Resolution.Display?.FriendlyName);
        return new CaptureNoticeInfo(L.T($"Aufnahme wieder auf „{name}“", $"Capturing “{name}” again"), IsWarning: false);
    }
}
