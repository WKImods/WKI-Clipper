using System;
using System.Threading;
using System.Windows;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.ViewModels;
using WKI_Clipper.Views;

namespace WKI_Clipper;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "WKI_Clipper.SingleInstance.5F3A2B1C";

    private Mutex? _singleInstanceMutex;
    public static AppHost Host { get; private set; } = null!;
    private OverlayWindow? _overlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (s, ex) =>
        {
            Logger.Error("Dispatcher unhandled exception", ex.Exception);
            MessageBox.Show("Unhandled error: " + ex.Exception.Message
                + "\n\nLog: " + Logger.Path, "WKI Clipper", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            Logger.Error("AppDomain unhandled exception", (ex.ExceptionObject as Exception)
                ?? new Exception(ex.ExceptionObject?.ToString() ?? "unknown"));
        };

        Logger.Rotate();
        Logger.Info("==================== WKI Clipper start ====================");

        // Single-instance lock
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            Logger.Warn("Another instance is already running. Exiting.");
            // Runs before settings/L are initialized → show both languages.
            MessageBox.Show("WKI Clipper läuft bereits (siehe Tray).\nWKI Clipper is already running (see tray).", "WKI Clipper",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            Host = new AppHost();
            Logger.Info("AppHost constructed.");
            Host.Initialize();
            Logger.Info("AppHost initialized.");

            Host.Hotkeys.HotkeyPressed += OnHotkeyPressed;
            Host.Hotkeys.HotkeyRegistrationFailed += (_, action) =>
            {
                Logger.Error($"Hotkey registration failed for action: {action} (already taken by another app?)");
                ToastService.Show(Views.ToastKind.Warning, L.T("Hotkey-Konflikt", "Hotkey conflict"),
                    L.T("Aktion '" + action + "' ist bereits durch eine andere App belegt.",
                        "Action '" + action + "' is already taken by another app."),
                    durationSeconds: 6.0);
            };

            // Tray state + toast notifications
            Host.ReplayBuffer.BufferStateChanged += (_, running) =>
            {
                if (Host.ManualRecording.IsRecording) return;
                TrayHost.UpdateState(running ? TrayState.BufferActive : TrayState.Idle,
                    running ? $"{Host.Settings.Current.ReplayBuffer.DurationSeconds} s" : null);
            };
            Host.ReplayBuffer.ReplaySaved += (_, path) =>
            {
                ToastService.Show(Views.ToastKind.Clip,
                    L.T($"Clip gespeichert ({Host.Settings.Current.ReplayBuffer.DurationSeconds} s)",
                        $"Clip saved ({Host.Settings.Current.ReplayBuffer.DurationSeconds} s)"),
                    System.IO.Path.GetFileName(path),
                    path);
            };
            Host.ReplayBuffer.BufferError += (_, msg) =>
            {
                ToastService.Show(Views.ToastKind.Warning, L.T("Buffer-Fehler", "Buffer error"), msg, durationSeconds: 6.0);
            };
            Host.ReplayBuffer.BufferInfo += (_, msg) =>
            {
                ToastService.Show(Views.ToastKind.Info, L.T("Replay-Buffer", "Replay buffer"), msg, durationSeconds: 3.5);
            };
            Host.ManualRecording.RecordingStarted += (_, path) =>
            {
                TrayHost.UpdateState(TrayState.Recording, System.IO.Path.GetFileNameWithoutExtension(path));
                ToastService.Show(Views.ToastKind.Recording, L.T("Aufnahme gestartet", "Recording started"),
                    System.IO.Path.GetFileName(path), durationSeconds: 2.5);
            };
            Host.ManualRecording.RecordingStopped += (_, result) =>
            {
                // Revert the tray icon regardless of outcome.
                TrayHost.UpdateState(Host.ReplayBuffer.IsRunning ? TrayState.BufferActive : TrayState.Idle,
                    Host.ReplayBuffer.IsRunning ? $"{Host.Settings.Current.ReplayBuffer.DurationSeconds} s" : null);
                if (result.Success)
                    ToastService.Show(Views.ToastKind.Recording, L.T("Aufnahme gespeichert", "Recording saved"),
                        System.IO.Path.GetFileName(result.Path), result.Path);
                else
                    ToastService.Show(Views.ToastKind.Warning, L.T("Aufnahme fehlgeschlagen", "Recording failed"),
                        result.Error ?? L.T("Unbekannter Fehler.", "Unknown error."), durationSeconds: 6.0);
            };
            Host.Screenshots.ScreenshotSaved += (_, path) =>
            {
                ToastService.Show(Views.ToastKind.Screenshot, "Screenshot",
                    System.IO.Path.GetFileName(path), path);
            };

            if (Host.Settings.Current.ReplayBuffer.Enabled && Host.Settings.Current.Behavior.StartBufferOnLaunch)
            {
                Logger.Info("Starting replay buffer on launch.");
                Host.ReplayBuffer.Start();
            }

            _overlay = new OverlayWindow(Host);
            Logger.Info("OverlayWindow constructed.");
            // Don't show — tray icon controls visibility.

            TrayHost.Install(Host, _overlay);
            Logger.Info("TrayHost installed.");

            Logger.Info("Startup complete.");
        }
        catch (Exception ex)
        {
            Logger.Error("Startup failure", ex);
            MessageBox.Show("Startup-Fehler: " + ex.Message + "\n\nSiehe Log:\n" + Logger.Path,
                "WKI Clipper", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async void OnHotkeyPressed(object? sender, string action)
    {
        Logger.Info($"Hotkey fired: {action}");
        try
        {
            switch (action)
            {
                case HotkeyActions.SaveReplay:
                    await Host.ReplayBuffer.SaveLastAsync();
                    break;
                case HotkeyActions.Screenshot:
                    await Host.Screenshots.CaptureActiveWindowAsync();
                    break;
                case HotkeyActions.ToggleRecording:
                    await Host.ManualRecording.ToggleAsync();
                    break;
                case HotkeyActions.ToggleOverlay:
                    ToggleOverlay();
                    break;
                case HotkeyActions.ToggleBuffer:
                    await Host.ReplayBuffer.ToggleAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Hotkey action {action} threw", ex);
        }
    }

    private void ToggleOverlay()
    {
        if (_overlay is null) return;
        if (_overlay.IsVisible) _overlay.Hide();
        else _overlay.ShowOnActiveMonitor();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("App exiting.");
        try { Host?.Dispose(); } catch { }
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
