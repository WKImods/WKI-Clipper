using System;
using System.Diagnostics;

namespace WKI_Clipper.Services;

/// <summary>Opens a web address in the Windows default browser - never anything but http or https.</summary>
public static class UrlOpener
{
    /// <summary>The address as a URI when it is an absolute http or https URL, else null.</summary>
    public static Uri? SafeUri(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri : null;

    public static bool TryOpen(string? url)
    {
        if (SafeUri(url) is not { } uri) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Opening a link failed: " + ex.Message);
            return false;
        }
    }
}
