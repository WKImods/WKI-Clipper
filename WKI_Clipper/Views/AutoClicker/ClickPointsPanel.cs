using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;
using Orientation = System.Windows.Controls.Orientation;

namespace WKI_Clipper.Views.AutoClicker;

/// <summary>"Klickpunkte": the route of points the clicker visits (x, y, clicks, jitter radius), reorder, delete, pick on screen.</summary>
public sealed class ClickPointsPanel : ClickerPanel
{
    private readonly StackPanel _rows = new();
    private readonly TextBlock _empty;
    private readonly Button _pick;
    private string? _shown;
    private bool _keepRows;
    private bool _subscribed;

    public ClickPointsPanel()
    {
        var section = Section(L.T("Klickpunkte", "Click points"), L.T(
            "Der Clicker fährt die Punkte der Reihe nach an und klickt jeweils so oft wie angegeben. Radius = zufällige Abweichung in Pixeln. Ohne Klickpunkte bewegt er die Maus nie.",
            "The clicker visits the points in order and clicks each as often as set. Radius = random offset in pixels. Without click points it never moves the mouse."));
        section.Children.Add(Check(L.T("Klickpunkte verwenden", "Use click points"), c => c.ClickPointsEnabled, (c, v) => c.ClickPointsEnabled = v));
        section.Children.Add(Check(L.T("Stoppen, wenn alle Punkte erledigt sind (sonst von vorn)", "Stop when all points are done (else start over)"),
            c => c.StopWhenComplete, (c, v) => c.StopWhenComplete = v));
        var holdNote = Muted(L.T("Im Halte-Modus (Taste gedrückt halten) werden Klickpunkte nicht verwendet.",
                                 "In hold mode (button held down) click points are not used."), new Thickness(0, 4, 0, 0));
        section.Children.Add(holdNote);
        VisibleWhen(holdNote, c => c.ClickPointsEnabled && (c.DutyMode == DutyMode.Hold || KeyboardRepeat.IsHoldMode(c)));

        _rows.Margin = new Thickness(0, 10, 0, 4);
        section.Children.Add(_rows);
        _empty = Muted(L.T("Noch keine Punkte.", "No points yet."), new Thickness(0, 0, 0, 4));
        section.Children.Add(_empty);

        _pick = Btn(L.T("Punkte wählen", "Pick points"), OpenPicker, accent: true,
            tooltip: L.T("Klick setzt einen Punkt, Rücktaste entfernt den letzten, Rechtsklick oder Esc beendet.",
                         "A click adds a point, Backspace removes the last, right-click or Esc finishes."));
        var clear = Btn(L.T("Alle löschen", "Delete all"), () =>
        {
            if (Clicker is not { ClickPoints.Count: > 0 }) return;
            var answer = MessageBox.Show(Window.GetWindow(this), L.T("Alle Klickpunkte löschen?", "Delete all click points?"),
                "AutoClicker", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) Commit(c => c.ClickPoints = new List<ClickPoint>());
        });
        section.Children.Add(Buttons(_pick, clear));

        Loaded += (_, _) =>
        {
            if (_subscribed) return;
            PickerSession.OpenChanged += Refresh;
            _subscribed = true;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (!_subscribed) return;
            PickerSession.OpenChanged -= Refresh;
            _subscribed = false;
        };
    }

    private static string RowsKey(ClickerSettings c) => string.Join("|", c.ClickPoints.Select(p => $"{p.X};{p.Y};{p.Clicks};{p.Radius}"));

    protected override void OnRefresh(ClickerSettings c)
    {
        _pick.IsEnabled = !PickerSession.IsOpen;
        _empty.Visibility = c.ClickPoints.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var key = RowsKey(c);
        if (_keepRows) _shown = key;   // an edit inside a row: keep the rows (and the focus of the next box)
        if (key == _shown) return;
        _shown = key;
        _rows.Children.Clear();
        for (int i = 0; i < c.ClickPoints.Count; i++) _rows.Children.Add(BuildRow(c.ClickPoints, i));
    }

    private UIElement BuildRow(IReadOnlyList<ClickPoint> points, int index)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var number = Label($"{index + 1}.");
        number.Width = 32;
        row.Children.Add(number);

        row.Children.Add(Field("X", 70, q => q.X, (q, v) => q.X = v, points, index));
        row.Children.Add(Field("Y", 70, q => q.Y, (q, v) => q.Y = v, points, index));
        row.Children.Add(Field(L.T("Klicks", "Clicks"), 70, q => q.Clicks, (q, v) => q.Clicks = v, points, index));
        row.Children.Add(Field(L.T("Radius", "Radius"), 60, q => q.Radius, (q, v) => q.Radius = v, points, index));

        var up = new Button { Content = "▲", IsEnabled = index > 0, Margin = new Thickness(8, 0, 0, 0), ToolTip = L.T("Nach oben", "Move up") };
        up.Click += (_, _) => Move(index, -1);
        var down = new Button { Content = "▼", IsEnabled = index < points.Count - 1, Margin = new Thickness(4, 0, 0, 0), ToolTip = L.T("Nach unten", "Move down") };
        down.Click += (_, _) => Move(index, +1);
        var delete = new Button { Content = L.T("Löschen", "Delete"), Margin = new Thickness(4, 0, 0, 0) };
        delete.Click += (_, _) => CommitList(s => s.ClickPoints, (s, l) => s.ClickPoints = l, l => { if (index >= l.Count) return false; l.RemoveAt(index); return true; });
        row.Children.Add(up);
        row.Children.Add(down);
        row.Children.Add(delete);
        return row;
    }

    /// <summary>A small labelled integer box; commits on Enter or focus loss, on a copy of the point.</summary>
    private UIElement Field(string label, double width, Func<ClickPoint, int> get, Action<ClickPoint, int> set, IReadOnlyList<ClickPoint> points, int index)
    {
        int value = get(points[index]);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
        var l = Label(label);
        l.Margin = new Thickness(0, 0, 4, 0);
        panel.Children.Add(l);
        var box = new TextBox { Width = width, Text = value.ToString(), VerticalContentAlignment = VerticalAlignment.Center };
        void Apply()
        {
            if (!ClickerNumber.TryParse(box.Text, out var v)) { box.Text = value.ToString(); return; }
            int n = (int)Math.Clamp(Math.Round(v), int.MinValue, int.MaxValue);
            if (n == value) return;
            _keepRows = true;
            try
            {
                CommitList(s => s.ClickPoints, (s, list) => s.ClickPoints = list, list =>
                {
                    if (index >= list.Count) return false;
                    var o = list[index];
                    var copy = new ClickPoint { X = o.X, Y = o.Y, Clicks = o.Clicks, Radius = o.Radius };
                    set(copy, n);
                    list[index] = copy;
                    return true;
                });
            }
            finally { _keepRows = false; }
            if (Clicker?.ClickPoints is { } now && index < now.Count)
            {
                value = get(now[index]);   // the normalized value (clicks 1..999999, radius 0..9999, coordinates clamped)
                box.Text = value.ToString();
            }
        }
        box.LostKeyboardFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
        panel.Children.Add(box);
        return panel;
    }

    private void Move(int index, int delta)
        => CommitList(s => s.ClickPoints, (s, l) => s.ClickPoints = l, l =>
        {
            int target = index + delta;
            if (index >= l.Count || target < 0 || target >= l.Count) return false;
            (l[index], l[target]) = (l[target], l[index]);
            return true;
        });

    private void OpenPicker()
    {
        if (Host is not { } host) return;
        host.CloseBoard?.Invoke();   // the board's topmost windows would cover the screen to click on
        ClickPointPicker.Open(host, () => { if (IsLoaded) Refresh(); });
    }
}
