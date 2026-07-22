using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper;

public enum TrayState { Idle, BufferActive, Recording }

[SupportedOSPlatform("windows")]
internal static class TrayHost
{
    private static WinForms.NotifyIcon? _trayIcon;
    private static AppHost? _host;
    private static OverlayWindow? _overlay;

    // Cached icons per state. Built on demand via Bitmap → Icon.
    private static readonly Dictionary<TrayState, Icon> _icons = new();
    private static TrayState _currentState = TrayState.Idle;

    private static string? _lastClipPath;

    public static void Install(AppHost host, OverlayWindow overlay)
    {
        _host = host;
        _overlay = overlay;

        var cms = BuildContextMenu();

        _trayIcon = new WinForms.NotifyIcon
        {
            Text = L.T("WKI Clipper — Bereit", "WKI Clipper — Ready"),
            ContextMenuStrip = cms,
            Visible = true,
            Icon = GetIcon(TrayState.Idle)
        };
        _trayIcon.DoubleClick += (_, _) => ShowOverlay();
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) ShowOverlay();
        };
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_lastClipPath) && File.Exists(_lastClipPath))
            {
                try { Process.Start(new ProcessStartInfo(_lastClipPath) { UseShellExecute = true }); } catch { }
            }
        };

        Logger.Info("Tray icon visible.");
    }

    public static void UpdateState(TrayState state, string? detail = null)
    {
        if (_trayIcon is null) return;
        _currentState = state;
        try { _trayIcon.Icon = GetIcon(state); } catch { }
        _trayIcon.Text = state switch
        {
            TrayState.Idle         => detail is null ? L.T("WKI Clipper — Bereit", "WKI Clipper — Ready") : "WKI Clipper — " + detail,
            TrayState.BufferActive => detail is null ? L.T("WKI Clipper — Buffer aktiv", "WKI Clipper — Buffer active") : L.T("WKI Clipper — Buffer aktiv (", "WKI Clipper — Buffer active (") + detail + ")",
            TrayState.Recording    => detail is null ? L.T("WKI Clipper — Recording läuft", "WKI Clipper — Recording") : "WKI Clipper — Recording: " + detail,
            _                      => "WKI Clipper"
        };
        // NotifyIcon.Text max 63 chars
        if (_trayIcon.Text.Length > 63) _trayIcon.Text = _trayIcon.Text.Substring(0, 63);
    }

    public static void ShowBalloon(string title, string body, string? clickToOpenPath = null)
    {
        if (_trayIcon is null) return;
        if (_host != null && !_host.Settings.Current.Behavior.ShowToastNotifications) return;
        _lastClipPath = clickToOpenPath;
        try
        {
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = body;
            _trayIcon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(3000);
        }
        catch (Exception ex) { Logger.Error("ShowBalloon failed", ex); }
    }

    private static Icon GetIcon(TrayState state)
    {
        if (_icons.TryGetValue(state, out var cached)) return cached;
        var icon = CreateStateIcon(state);
        _icons[state] = icon;
        return icon;
    }

    private static Icon CreateStateIcon(TrayState state)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            // Base colour is the primary state signal: red = recording, orange =
            // buffer active, desaturated grey = idle (so "puffert es gerade?" is
            // readable at a glance, not only via the tiny corner dot).
            var bgColor = state switch
            {
                TrayState.Recording    => System.Drawing.Color.FromArgb(255, 0xE0, 0x3E, 0x3E),
                TrayState.BufferActive => System.Drawing.Color.FromArgb(255, 0xFF, 0x6A, 0x2C),
                _                      => System.Drawing.Color.FromArgb(255, 0x4A, 0x4A, 0x52),
            };

            using var bg = new SolidBrush(bgColor);
            g.FillRectangle(bg, 0, 0, size, size);
            using var pen = new Pen(System.Drawing.Color.FromArgb(160, 0, 0, 0), 2f);
            g.DrawRectangle(pen, 1, 1, size - 3, size - 3);

            using var font = new Font(new System.Drawing.FontFamily("Segoe UI"), 16, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(System.Drawing.Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("W", font, fg, new RectangleF(0, 0, size, size), sf);

            // Indicator dot bottom-right for Buffer/Recording
            if (state == TrayState.BufferActive)
            {
                using var dot = new SolidBrush(System.Drawing.Color.FromArgb(255, 0x4A, 0xD8, 0x6A));
                g.FillEllipse(dot, size - 12, size - 12, 10, 10);
                g.DrawEllipse(new Pen(System.Drawing.Color.Black, 1), size - 12, size - 12, 10, 10);
            }
            else if (state == TrayState.Recording)
            {
                using var dot = new SolidBrush(System.Drawing.Color.White);
                g.FillEllipse(dot, size - 12, size - 12, 10, 10);
                g.DrawEllipse(new Pen(System.Drawing.Color.Black, 1), size - 12, size - 12, 10, 10);
            }
        }

        // Convert Bitmap → real Icon by emitting an in-memory .ico stream.
        var hicon = bmp.GetHicon();
        try
        {
            using var src = Icon.FromHandle(hicon);
            using var ms = new MemoryStream();
            src.Save(ms);
            ms.Position = 0;
            return new Icon(ms);
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }

    private static WinForms.ContextMenuStrip BuildContextMenu()
    {
        var cms = new WinForms.ContextMenuStrip();

        var openItem = new WinForms.ToolStripMenuItem(L.T("Overlay öffnen  (Strg+Alt+G)", "Open overlay  (Ctrl+Alt+G)")) { Font = new Font(cms.Font, System.Drawing.FontStyle.Bold) };
        openItem.Click += (_, _) => ShowOverlay();
        cms.Items.Add(openItem);

        cms.Items.Add(new WinForms.ToolStripSeparator());

        var bufferItem = new WinForms.ToolStripMenuItem(L.T("Replay-Buffer ein/aus", "Replay buffer on/off"));
        bufferItem.Click += async (_, _) => { if (_host != null) await _host.ReplayBuffer.ToggleAsync(); };
        cms.Items.Add(bufferItem);

        var recItem = new WinForms.ToolStripMenuItem(L.T("Recording starten/stoppen", "Start/stop recording"));
        recItem.Click += async (_, _) => { if (_host != null) await _host.ManualRecording.ToggleAsync(); };
        cms.Items.Add(recItem);

        var shotItem = new WinForms.ToolStripMenuItem("Screenshot");
        shotItem.Click += async (_, _) => { if (_host != null) await _host.Screenshots.CaptureActiveWindowAsync(); };
        cms.Items.Add(shotItem);

        var saveReplayItem = new WinForms.ToolStripMenuItem(L.T("Letzte N Sekunden speichern", "Save last N seconds"));
        saveReplayItem.Click += async (_, _) => { if (_host != null) await _host.ReplayBuffer.SaveLastAsync(); };
        cms.Items.Add(saveReplayItem);

        cms.Items.Add(new WinForms.ToolStripSeparator());

        var openClipsItem = new WinForms.ToolStripMenuItem(L.T("Clips-Ordner öffnen", "Open clips folder"));
        openClipsItem.Click += (_, _) =>
        {
            if (_host is null) return;
            var dir = SettingsService.ExpandPath(_host.Settings.Current.Output.ClipsFolder);
            try { Process.Start("explorer.exe", dir); } catch { }
        };
        cms.Items.Add(openClipsItem);

        var openLogItem = new WinForms.ToolStripMenuItem(L.T("Log öffnen", "Open log"));
        openLogItem.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Logger.Path + "\"") { UseShellExecute = true }); } catch { }
        };
        cms.Items.Add(openLogItem);

        cms.Items.Add(new WinForms.ToolStripSeparator());

        var exitItem = new WinForms.ToolStripMenuItem(L.T("Beenden", "Quit"));
        exitItem.Click += (_, _) =>
        {
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
            Application.Current.Shutdown();
        };
        cms.Items.Add(exitItem);

        return cms;
    }

    private static void ShowOverlay() => _overlay?.ShowOnActiveMonitor();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
