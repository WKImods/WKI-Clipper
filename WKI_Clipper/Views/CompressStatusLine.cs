using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WKI_Clipper.Services;

namespace WKI_Clipper.Views;

/// <summary>
/// The line under a gallery row's file name that follows a Discord compress: a progress bar with a cancel
/// button while it runs, the reason in red (with a dismiss button) if it failed. Hidden otherwise, so a row
/// without a compress looks exactly as before.
/// </summary>
internal sealed class CompressStatusLine
{
    private readonly System.Windows.Controls.ProgressBar _bar;
    private readonly TextBlock _text;
    private readonly System.Windows.Controls.Button _cancel;
    private readonly System.Windows.Controls.Button _dismiss;
    private readonly Brush _muted;
    private readonly Brush _danger;

    public DockPanel Root { get; }

    /// <param name="owner">Supplies the theme brushes.</param>
    /// <param name="cancel">The cancel button was pressed.</param>
    /// <param name="dismiss">The dismiss button of a failure was pressed (the line hides itself).</param>
    public CompressStatusLine(FrameworkElement owner, Action cancel, Action dismiss)
    {
        _muted = (Brush)owner.FindResource("MutedBrush");
        _danger = (Brush)owner.FindResource("DangerBrush");

        _bar = new System.Windows.Controls.ProgressBar
        {
            Width = 150, Height = 6, Minimum = 0, Maximum = 1, BorderThickness = new Thickness(0),
            Foreground = (Brush)owner.FindResource("AccentBrush"),
            Background = (Brush)owner.FindResource("PanelHoverBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        };
        _cancel = new System.Windows.Controls.Button
        {
            Content = L.T("Abbrechen", "Cancel"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(10, 0, 0, 0)
        };
        _dismiss = new System.Windows.Controls.Button
        {
            Content = "✕", Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(10, 0, 0, 0),
            Background = Brushes.Transparent, ToolTip = L.T("Meldung ausblenden", "Dismiss")
        };
        _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        _cancel.Click += (_, _) => cancel();
        _dismiss.Click += (_, _) =>
        {
            dismiss();
            Apply(null);
        };

        Root = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        DockPanel.SetDock(_bar, Dock.Left);
        DockPanel.SetDock(_cancel, Dock.Right);
        DockPanel.SetDock(_dismiss, Dock.Right);
        Root.Children.Add(_bar);
        Root.Children.Add(_cancel);
        Root.Children.Add(_dismiss);
        Root.Children.Add(_text);
    }

    // Test access
    internal string Text => _text.Text;
    internal bool IsBarIndeterminate => _bar.IsIndeterminate;
    internal double BarValue => _bar.Value;
    internal bool CancelVisible => _cancel.Visibility == Visibility.Visible;
    internal bool DismissVisible => _dismiss.Visibility == Visibility.Visible;
    internal bool BarVisible => _bar.Visibility == Visibility.Visible;
    internal bool TextIsDanger => ReferenceEquals(_text.Foreground, _danger);
    internal void ClickCancel() => _cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    internal void ClickDismiss() => _dismiss.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    /// <summary>Shows the state of the clip's job; null, a finished or a cancelled job hides the line.</summary>
    public void Apply(CompressJobSnapshot? s)
    {
        bool failed = s is { Outcome: CompressStatus.Failed } && !string.IsNullOrEmpty(s.Error);
        if (s is null || (!s.Active && !failed))
        {
            Root.Visibility = Visibility.Collapsed;
            return;
        }

        Root.Visibility = Visibility.Visible;
        var label = "Discord " + DiscordCompressRules.TargetLabel(s.TargetBytes);
        if (failed)
        {
            _bar.Visibility = _cancel.Visibility = Visibility.Collapsed;
            _dismiss.Visibility = Visibility.Visible;
            _text.Foreground = _danger;
            _text.Text = $"{label}: {s.Error}";
            _text.ToolTip = _text.Text;   // the line is cut off at the row's width; the tooltip has all of it
            return;
        }

        _bar.Visibility = _cancel.Visibility = Visibility.Visible;
        _dismiss.Visibility = Visibility.Collapsed;
        _text.Foreground = _muted;
        _text.ToolTip = null;
        bool measurable = s.Stage is CompressStage.Encoding or CompressStage.Copying;
        _bar.IsIndeterminate = !measurable;
        if (measurable) _bar.Value = Math.Clamp(s.Fraction, 0, 1);

        string percent = ((int)Math.Round(Math.Clamp(s.Fraction, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture);
        _text.Text = s.Stage switch
        {
            CompressStage.Queued         => $"{label}: " + L.T("in der Warteschlange…", "queued…"),
            CompressStage.WaitingForGame => $"{label}: " + L.T("wartet, bis das Spiel beendet ist…", "waiting for the game to end…"),
            CompressStage.Analyzing      => $"{label}: " + L.T("analysiere…", "analyzing…"),
            CompressStage.Copying        => $"{label}: " + L.T($"kopiere… {percent} %", $"copying… {percent} %"),
            _                            => $"{label}: " + L.T($"komprimiere… {percent} %", $"compressing… {percent} %")
                                            + (s.Attempt > 1 ? L.T($" · Versuch {s.Attempt}", $" · attempt {s.Attempt}") : "")
        };
    }
}