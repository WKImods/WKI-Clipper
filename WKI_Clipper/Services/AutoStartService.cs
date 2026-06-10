using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace WKI_Clipper.Services;

/// <summary>
/// Toggles a per-user Windows autostart Run-key entry pointing at the current
/// WKI_Clipper.exe. No admin needed (HKCU).
/// </summary>
public sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WKI_Clipper";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var current = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(current);
        }
        catch (Exception ex)
        {
            Logger.Error("AutoStart.IsEnabled", ex);
            return false;
        }
    }

    public void SetEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            if (enable)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\"");
                Logger.Info("AutoStart enabled: " + exe);
            }
            else
            {
                if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
                Logger.Info("AutoStart disabled");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("AutoStart.SetEnabled", ex);
        }
    }
}
