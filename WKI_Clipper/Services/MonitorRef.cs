using WKI_Clipper.Models;

namespace WKI_Clipper.Services;

/// <summary>
/// The saved identity of the monitor chosen for <see cref="CaptureMode.Monitor"/>, strongest part first: the CCD monitor
/// device path, then the EDID manufacturer + product ids, then the GDI name (<c>\\.\DISPLAYn</c> - an adapter source slot,
/// only a legacy value and the tie-breaker between identical models). The friendly name only labels it. Resolution,
/// refresh rate, position and the primary flag are never part of it (R1).
/// </summary>
public sealed record MonitorRef(string? DevicePath, string? GdiName, string? FriendlyName, ushort? EdidMfg, ushort? EdidProduct)
{
    /// <summary>The monitor saved in <paramref name="profile"/>; null when nothing is saved (= the primary monitor).</summary>
    public static MonitorRef? FromProfile(CaptureProfile profile)
    {
        var saved = new MonitorRef(Clean(profile.MonitorDevicePath), Clean(profile.MonitorDeviceName),
                                   Clean(profile.MonitorFriendlyName), profile.MonitorEdidMfg, profile.MonitorEdidProduct);
        return saved is { DevicePath: null, GdiName: null, FriendlyName: null, EdidMfg: null, EdidProduct: null } ? null : saved;
    }

    /// <summary>The identity of a connected monitor, as it would be saved.</summary>
    public static MonitorRef Of(DisplayInfo display)
        => new(Clean(display.DevicePath), Clean(display.GdiName), Clean(display.FriendlyName), display.EdidMfg, display.EdidProduct);

    /// <summary>Both EDID ids are known (one alone does not identify a model).</summary>
    internal bool HasEdid => EdidMfg is not null && EdidProduct is not null;

    /// <summary>
    /// <paramref name="next"/> has a value wherever this ref has one. A sleeping DisplayPort monitor reports no device path
    /// for a while; saving that would throw away the identity.
    /// </summary>
    internal bool LosesNothingTo(MonitorRef next)
        => (DevicePath is null || next.DevicePath is not null)
        && (GdiName is null || next.GdiName is not null)
        && (FriendlyName is null || next.FriendlyName is not null)
        && (EdidMfg is null || next.EdidMfg is not null)
        && (EdidProduct is null || next.EdidProduct is not null);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
