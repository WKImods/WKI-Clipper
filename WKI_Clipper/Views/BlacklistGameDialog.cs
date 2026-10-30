using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WKI_Clipper.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace WKI_Clipper.Views;

/// <summary>
/// "Spiel hinzufügen" for the AutoClicker blacklist: the games the clipper knows (the one in front, the ones with a profile, the ones on
/// the shortcut bar) to tick, plus "Programm wählen…" for any other exe. It only records the answer; the caller adds the entries.
/// </summary>
internal sealed class BlacklistGameDialog : Window
{
    private readonly ListBox _list = new() { SelectionMode = System.Windows.Controls.SelectionMode.Extended, MinHeight = 200, MaxHeight = 420, MinWidth = 440 };

    private readonly bool _whitelist;

    internal ListBox ChoiceList => _list;

    /// <summary>The suggestions the user ticked and confirmed (empty after cancel or after "Programm wählen…").</summary>
    public IReadOnlyList<BlacklistCandidate> Selected { get; private set; } = Array.Empty<BlacklistCandidate>();

    /// <summary>The exe picked with "Programm wählen…", or null.</summary>
    public string? PickedExe { get; private set; }

    /// <param name="whitelist">The same dialog for the AutoClicker whitelist (the titles name the whitelist).</param>
    public BlacklistGameDialog(IReadOnlyList<BlacklistCandidate> candidates, Window? owner, bool whitelist = false)
    {
        if (owner is { IsVisible: true }) Owner = owner;
        WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        _whitelist = whitelist;
        Title = whitelist
            ? L.T("Programm zur Whitelist hinzufügen", "Add a program to the whitelist")
            : L.T("Spiel zur Blacklist hinzufügen", "Add a game to the blacklist");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;   // above the board's topmost windows
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");

        foreach (var c in candidates)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
            row.Children.Add(new TextBlock
            {
                Text = c.AlreadyListed ? $"{c.DisplayName}  ({c.ExeName}, {(whitelist ? L.T("schon auf der Whitelist", "already on the whitelist") : L.T("schon auf der Blacklist", "already on the blacklist"))})" : $"{c.DisplayName}  ({c.ExeName})",
                VerticalAlignment = VerticalAlignment.Center,
            });
            _list.Items.Add(new ListBoxItem { Content = row, IsEnabled = !c.AlreadyListed, Opacity = c.AlreadyListed ? 0.5 : 1.0, Tag = c });
        }

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = candidates.Count > 0
                ? L.T("Mehrere mit Strg oder Umschalt auswählen.", "Select several with Ctrl or Shift.")
                : L.T("Es ist noch kein Spiel bekannt. Starte ein Spiel oder wähle ein Programm.", "No game is known yet. Start a game or pick a program."),
            Style = (Style)Application.Current.FindResource("MutedStyle"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap,
        });
        if (candidates.Count > 0) root.Children.Add(_list);

        var add = new Button { Content = L.T("Hinzufügen", "Add"), MinWidth = 100, Style = (Style)Application.Current.FindResource("AccentButton"), IsEnabled = candidates.Any(c => !c.AlreadyListed) };
        add.Click += (_, _) => Confirm();
        var pick = new Button { Content = L.T("Programm wählen…", "Choose program…"), Margin = new Thickness(0, 0, 8, 0) };
        pick.Click += (_, _) => ChooseProgram();
        var cancel = new Button { Content = L.T("Abbrechen", "Cancel"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(pick, Dock.Left);
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(add, Dock.Right);
        buttons.Children.Add(pick);
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        root.Children.Add(buttons);
        Content = root;
    }

    private void Confirm()
    {
        Selected = _list.SelectedItems.OfType<ListBoxItem>().Select(i => (BlacklistCandidate)i.Tag).Where(c => !c.AlreadyListed).ToList();
        Close();
    }

    private void ChooseProgram()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = _whitelist
                ? L.T("Programm für die Whitelist wählen", "Choose a program for the whitelist")
                : L.T("Programm für die Blacklist wählen", "Choose a program for the blacklist"),
            Filter = L.T("Programme (*.exe)|*.exe", "Programs (*.exe)|*.exe"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        PickedExe = dlg.FileName;
        Close();
    }
}
