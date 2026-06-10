using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WKI_Clipper.Models;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// Global Windows hotkey service. Owns a hidden HwndSource and processes WM_HOTKEY.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly SettingsService _settings;
    private HwndSource? _hwndSource;
    private readonly Dictionary<int, string> _idToAction = new();
    private int _nextId = 0x9000;

    public event EventHandler<string>? HotkeyPressed;
    public event EventHandler<string>? HotkeyRegistrationFailed;

    public HotkeyService(SettingsService settings)
    {
        _settings = settings;
    }

    public void Initialize()
    {
        // NOT message-only: a real (invisible) HwndSource. WM_HOTKEY arrives reliably.
        var parameters = new HwndSourceParameters("WKI_Clipper_HotkeySink")
        {
            Width = 1,
            Height = 1,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x80 | 0x8 // WS_EX_TOOLWINDOW | WS_EX_TOPMOST (we keep it off-screen anyway)
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
        Logger.Info($"HotkeyService HwndSource created. HWND=0x{_hwndSource.Handle.ToInt64():X}");
        RegisterAll();
    }

    public void RegisterAll()
    {
        UnregisterAll();
        if (_hwndSource is null) return;

        foreach (var (action, binding) in _settings.Current.Hotkeys)
        {
            if (binding.Key == 0) continue;
            int id = _nextId++;
            bool ok = User32.RegisterHotKey(_hwndSource.Handle, id, (uint)binding.Modifiers, binding.Key);
            if (ok)
            {
                _idToAction[id] = action;
                Logger.Info($"Hotkey registered: {action} = {DescribeBinding(binding)} (id 0x{id:X})");
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Error($"Hotkey FAILED: {action} = {DescribeBinding(binding)} | Win32 error {err} ({(err == 1409 ? "ERROR_HOTKEY_ALREADY_REGISTERED" : "see Win32 docs")})");
                HotkeyRegistrationFailed?.Invoke(this, action);
            }
        }
    }

    private void UnregisterAll()
    {
        if (_hwndSource is null) return;
        foreach (var id in _idToAction.Keys)
        {
            User32.UnregisterHotKey(_hwndSource.Handle, id);
        }
        _idToAction.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_idToAction.TryGetValue(id, out var action))
            {
                handled = true;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    HotkeyPressed?.Invoke(this, action);
                });
            }
        }
        return IntPtr.Zero;
    }

    private static string DescribeBinding(HotkeyBinding b)
    {
        var mods = b.Modifiers == HotkeyModifier.None ? "" : (b.Modifiers + "+");
        var vk = b.Key;
        var keyName = vk switch
        {
            >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),     // F1..F12
            >= 0x30 and <= 0x39 => ((char)vk).ToString(), // 0..9
            >= 0x41 and <= 0x5A => ((char)vk).ToString(), // A..Z
            _ => $"VK_0x{vk:X}"
        };
        return mods + keyName;
    }

    public void Dispose()
    {
        UnregisterAll();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.Dispose();
        _hwndSource = null;
    }
}
