using System;
using Microsoft.Win32;

namespace WKI_Clipper.Services;

/// <summary>
/// Toggles a per-user Windows autostart Run-key entry pointing at the current
/// WKI_Clipper.exe. No admin needed (HKCU).
///
/// Windows starts a Run entry only while its switch under Explorer\StartupApproved\Run is
/// not off — the switch behind Settings → Apps → Autostart and Task Manager's startup tab.
/// Reading only the Run key showed "on" for an entry Windows never started.
/// </summary>
public sealed class AutoStartService
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal const string ValueName = "WKI_Clipper";

    private readonly RegistryKey _root;
    private readonly Func<string?> _exePath;

    public AutoStartService() : this(Registry.CurrentUser, () => Environment.ProcessPath) { }

    /// <summary>Test seam: a throw-away registry root and a fixed exe path.</summary>
    internal AutoStartService(RegistryKey root, Func<string?> exePath)
    {
        _root = root;
        _exePath = exePath;
    }

    /// <summary>True when Windows will actually start the app: registered and not switched off.</summary>
    public bool IsEnabled()
    {
        try
        {
            using var run = _root.OpenSubKey(RunKeyPath, writable: false);
            if (string.IsNullOrWhiteSpace(run?.GetValue(ValueName) as string)) return false;
            using var approved = _root.OpenSubKey(ApprovedKeyPath, writable: false);
            return !IsSwitchedOff(approved?.GetValue(ValueName) as byte[]);
        }
        catch (Exception ex)
        {
            Logger.Error("AutoStart.IsEnabled", ex);
            return false;
        }
    }

    /// <summary>
    /// Windows' startup switch: 12 bytes, the first one 02 (on) or 03 (off, followed by the
    /// time it was switched off). A missing value means on.
    /// </summary>
    internal static bool IsSwitchedOff(byte[]? flag) => flag is { Length: > 0 } && (flag[0] & 1) == 1;

    public void SetEnabled(bool enable)
    {
        try
        {
            using var run = _root.CreateSubKey(RunKeyPath);
            if (enable)
            {
                var exe = _exePath();
                if (string.IsNullOrEmpty(exe)) return;
                run.SetValue(ValueName, $"\"{exe}\"");
                // An earlier "off" in Settings or Task Manager would keep winning silently; the
                // user just asked for on. Without the value, Windows treats the entry as on.
                using (var approved = _root.OpenSubKey(ApprovedKeyPath, writable: true))
                    approved?.DeleteValue(ValueName, throwOnMissingValue: false);
                Logger.Info("AutoStart enabled: " + exe);
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
                Logger.Info("AutoStart disabled");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("AutoStart.SetEnabled", ex);
        }
    }
}
