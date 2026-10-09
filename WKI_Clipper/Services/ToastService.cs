using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Forms;
using WKI_Clipper.Views;

namespace WKI_Clipper.Services;

/// <summary>
/// In-app toast notifications shown in the top-right of the primary monitor.
/// Stacks multiple toasts vertically; click to open the associated file.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ToastService
{
    private const double ToastSpacing = 8;
    private const double EdgeMargin = 16;
    private const double TopMargin = 16;

    private static readonly List<ToastNotificationWindow> _active = new();

    public static void Show(ToastKind kind, string title, string body, string? filePath = null,
                            double durationSeconds = 4.0)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        if (app.Dispatcher.CheckAccess())
            ShowInternal(kind, title, body, filePath, durationSeconds);
        else
            app.Dispatcher.BeginInvoke(() =>
                ShowInternal(kind, title, body, filePath, durationSeconds));
    }

    private static void ShowInternal(ToastKind kind, string title, string body,
                                     string? filePath, double durationSeconds)
    {
        try
        {
            var win = new ToastNotificationWindow();
            win.Configure(kind, title, body, filePath);
            win.Closing2 += (s, _) =>
            {
                if (s is ToastNotificationWindow w)
                {
                    _active.Remove(w);
                    RepositionAll();
                }
            };

            _active.Add(win);
            PositionAt(win, _active.Count - 1);
            win.ShowToast(durationSeconds);
        }
        catch (Exception ex)
        {
            Logger.Error("ToastService.Show failed", ex);
        }
    }

    private static void PositionAt(ToastNotificationWindow win, int index)
    {
        // Need to measure window before positioning. Force-render once.
        win.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var (screen, s) = PrimaryArea();

        // Use rough estimate for height before window renders; refined on first frame.
        // Sizes are DIPs, the work area is physical pixels — scale before mixing them.
        double w = win.Width;
        double estimatedH = 88;
        double left = screen.Right - (w + EdgeMargin) * s;

        double y = screen.Top + TopMargin * s;
        for (int i = 0; i < index; i++)
        {
            double h = _active[i].ActualHeight > 0 ? _active[i].ActualHeight : estimatedH;
            y += (h + ToastSpacing) * s;
        }

        DisplayGeometry.MoveTo(win, left, y);
    }

    private static void RepositionAll()
    {
        var (screen, s) = PrimaryArea();
        double y = screen.Top + TopMargin * s;
        foreach (var w in _active)
        {
            double left = screen.Right - (w.Width + EdgeMargin) * s;
            DisplayGeometry.MoveTo(w, left, y);
            double h = w.ActualHeight > 0 ? w.ActualHeight : 88;
            y += (h + ToastSpacing) * s;
        }
    }

    private static (System.Drawing.Rectangle Area, double Scale) PrimaryArea()
    {
        var primary = Screen.PrimaryScreen;
        if (primary is null) return (new System.Drawing.Rectangle(0, 0, 1920, 1080), 1.0);
        return (primary.WorkingArea, DisplayGeometry.ScaleOf(primary));
    }
}
