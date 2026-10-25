using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WKI_Clipper.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace WKI_Clipper.Views;

/// <summary>How the user left the missing-browser dialog.</summary>
public enum BrowserMissingOutcome
{
    /// <summary>Closed or "Abbrechen": nothing happens.</summary>
    Cancel,
    /// <summary>"Download-Seite öffnen": the caller opens <see cref="BrowserMissingDialog.DownloadUrl"/>.</summary>
    OpenDownload,
    /// <summary>"Diesen nehmen": the caller adds <see cref="BrowserMissingDialog.Other"/> instead.</summary>
    UseOther,
}

/// <summary>
/// "Opera is not installed": offers the browser's download page (opened by the caller, in the Windows default browser), one of the
/// browsers that ARE installed instead, or cancel. It starts nothing itself - it only records the answer in <see cref="Outcome"/> - so it
/// is the same in a test and on screen. After an install the user presses "Neu suchen" in the edit dialog.
/// </summary>
public sealed class BrowserMissingDialog : Window
{
    private readonly BrowserPick _pick;
    private readonly ComboBox _alternatives = new() { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _download;
    private readonly Button _useOther;

    public BrowserMissingOutcome Outcome { get; private set; } = BrowserMissingOutcome.Cancel;

    /// <summary>The browser picked instead (<see cref="BrowserMissingOutcome.UseOther"/> only).</summary>
    public BrowserInfo? Other { get; private set; }

    public string MissingName => _pick.Missing?.Name ?? "";

    /// <summary>False for a browser without a known download page - the button is then not offered.</summary>
    public bool CanOpenDownload => !string.IsNullOrWhiteSpace(_pick.Missing?.DownloadUrl);

    public string? DownloadUrl => CanOpenDownload ? _pick.Missing!.DownloadUrl : null;

    /// <summary>False when no other browser is installed (or all of them are on the bar already).</summary>
    public bool CanUseOther => _pick.Alternatives.Count > 0;

    internal IEnumerable<string> AlternativeIds => _pick.Alternatives.Select(b => b.Id);

    public BrowserMissingDialog(BrowserPick pick, Window? owner = null)
    {
        _pick = pick;
        if (owner != null) Owner = owner;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        Title = L.T("Browser nicht installiert", "Browser not installed");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;   // above the board's topmost windows
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");

        var name = MissingName;
        var root = new StackPanel { Margin = new Thickness(20), MinWidth = 420 };
        root.Children.Add(new TextBlock
        {
            Text = L.T($"{name} ist auf diesem PC nicht installiert.", $"{name} is not installed on this PC."),
            FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(new TextBlock
        {
            Text = L.T("Die Download-Seite öffnen - oder einen Browser nehmen, den es hier schon gibt. Nach der Installation im Bearbeiten-Dialog \"Neu suchen\" drücken.",
                       "Open its download page - or take a browser that is already here. After installing, press \"Rescan\" in the edit dialog."),
            Margin = new Thickness(0, 8, 0, 14), TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
        });

        _download = new Button
        {
            Content = L.T("Download-Seite öffnen", "Open the download page"), HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.FindResource("AccentButton"), IsEnabled = CanOpenDownload,
        };
        _download.Click += (_, _) => ClickDownload();
        root.Children.Add(_download);

        foreach (var b in pick.Alternatives) _alternatives.Items.Add(new AlternativeItem(b));
        _alternatives.SelectedIndex = pick.Alternatives.Count > 0 ? 0 : -1;
        _alternatives.IsEnabled = CanUseOther;
        _useOther = new Button
        {
            Content = L.T("Diesen nehmen", "Use this one"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = CanUseOther,
        };
        _useOther.Click += (_, _) => ClickUseOther();
        var otherRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        otherRow.Children.Add(new TextBlock { Text = L.T("Anderer Browser:", "Other browser:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        otherRow.Children.Add(_alternatives);
        otherRow.Children.Add(_useOther);
        root.Children.Add(otherRow);
        if (!CanUseOther)
            root.Children.Add(new TextBlock
            {
                Text = L.T("Es ist kein weiterer Browser zum Hinzufügen installiert.", "No other installed browser is left to add."),
                Margin = new Thickness(0, 6, 0, 0), Foreground = (Brush)Application.Current.FindResource("FaintBrush"),
            });

        var cancel = new Button { Content = L.T("Abbrechen", "Cancel"), MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        cancel.Click += (_, _) => ClickCancel();
        root.Children.Add(cancel);

        Content = root;
    }

    // The three answers. Each records the outcome and closes; the caller (the edit dialog) acts on it.

    internal void ClickDownload()
    {
        if (!CanOpenDownload) return;
        Outcome = BrowserMissingOutcome.OpenDownload;
        Close();
    }

    internal void SelectAlternative(string id)
    {
        for (int i = 0; i < _alternatives.Items.Count; i++)
            if (_alternatives.Items[i] is AlternativeItem a && string.Equals(a.Browser.Id, id, StringComparison.OrdinalIgnoreCase))
                _alternatives.SelectedIndex = i;
    }

    internal void ClickUseOther()
    {
        if (!CanUseOther) return;
        Other = (_alternatives.SelectedItem as AlternativeItem)?.Browser ?? _pick.Alternatives[0];
        Outcome = BrowserMissingOutcome.UseOther;
        Close();
    }

    internal void ClickCancel()
    {
        Outcome = BrowserMissingOutcome.Cancel;
        Close();
    }

    private sealed record AlternativeItem(BrowserInfo Browser)
    {
        public override string ToString() => Browser.Name;
    }
}
