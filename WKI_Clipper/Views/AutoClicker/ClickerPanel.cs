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

/// <summary>
/// Base of the sub-tab panels of the AutoClicker tab. The controls are built once (texts in the current language - a language or
/// theme switch rebuilds the whole widget) and <see cref="Refresh"/> copies the settings into them. Every edit goes through
/// <see cref="Commit"/>: change, normalize, the normal settings save (which also pushes the config to the helper, debounced).
/// A text box that has the keyboard focus is never overwritten by a refresh, so typing is not disturbed by a save elsewhere.
/// </summary>
public abstract class ClickerPanel : UserControl
{
    private readonly List<Action<ClickerSettings>> _syncs = new();
    private bool _refreshing;

    protected ClickerPanel()
    {
        Root = new StackPanel();
        Content = Root;
    }

    protected StackPanel Root { get; }

    protected static AppHost? Host => App.Host;

    protected static ClickerSettings? Clicker => Host?.Settings.Current.AutoClicker?.Clicker;

    /// <summary>Copies the current settings into the controls (UI thread).</summary>
    public void Refresh()
    {
        if (Clicker is not { } c || _refreshing) return;
        _refreshing = true;
        try
        {
            foreach (var sync in _syncs) sync(c);
            OnRefresh(c);
        }
        finally { _refreshing = false; }
    }

    /// <summary>Panel-specific refresh (lists), after the registered control syncs.</summary>
    protected virtual void OnRefresh(ClickerSettings c) { }

    /// <summary>Applies an edit, normalizes and saves. Ignored while the controls are being filled from the settings.</summary>
    protected void Commit(Action<ClickerSettings> edit)
    {
        if (_refreshing || Host is not { } host || Clicker is not { } c) return;
        edit(c);
        ClickerSettingsRules.Normalize(c);
        host.Settings.Save();
        Refresh();
    }

    protected void OnSync(Action<ClickerSettings> sync) => _syncs.Add(sync);

    // ---------------- builders ----------------

    /// <summary>A panel section (heading, optional hint) like the other sections of the tab; returns the stack to fill.</summary>
    protected StackPanel Section(string title, string? hint = null)
    {
        var stack = new StackPanel();
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        stack.Children.Add(heading);
        if (!string.IsNullOrEmpty(hint)) stack.Children.Add(Muted(hint, new Thickness(0, 4, 0, 4)));

        var border = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 12), Child = stack };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        Root.Children.Add(border);
        return stack;
    }

    protected static TextBlock Muted(string text, Thickness? margin = null)
        => new() { Text = text, Style = (Style)Application.Current.FindResource("MutedStyle"), TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };

    /// <summary>The warnings of one hotkey field, under its row (aligned with the controls); collapsed while there are none.</summary>
    protected TextBlock HotkeyWarnings(bool master)
    {
        var text = Muted("", new Thickness(170, 4, 0, 0));
        OnSync(_ =>
        {
            var lines = Host is { } h
                ? ClickerConflicts.ForHotkey(ClickerConflicts.Evaluate(h.Settings.Current), master).Select(x => x.Message)
                : Enumerable.Empty<string>();
            text.Text = string.Join(Environment.NewLine, lines);
            text.Visibility = text.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        });
        return text;
    }

    protected static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return t;
    }

    /// <summary>A label column of fixed width and the control(s) next to it.</summary>
    protected static Grid Row(string label, params UIElement[] controls)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Label(label));
        var right = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in controls)
        {
            if (c is CheckBox || (c is FrameworkElement { Margin: var m } && m == default)) ((FrameworkElement)c).Margin = new Thickness(0, 0, 8, 0);
            if (c is FrameworkElement el) el.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(c);
        }
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    protected CheckBox Check(string text, Func<ClickerSettings, bool> get, Action<ClickerSettings, bool> set, string? tooltip = null)
    {
        var box = new CheckBox { Content = text, Margin = new Thickness(0, 6, 0, 0), ToolTip = tooltip };
        box.Checked += (_, _) => Commit(c => set(c, true));
        box.Unchecked += (_, _) => Commit(c => set(c, false));
        OnSync(c => box.IsChecked = get(c));
        return box;
    }

    /// <summary>A number box that commits on Enter or when it loses the focus; an unreadable entry falls back to the stored value.</summary>
    protected TextBox Number(Func<ClickerSettings, double> get, Action<ClickerSettings, double> set, double width = 80, string? tooltip = null)
    {
        var box = new TextBox { Width = width, ToolTip = tooltip, VerticalContentAlignment = VerticalAlignment.Center };
        void Apply()
        {
            if (_refreshing || Clicker is not { } current) return;
            if (ClickerNumber.TryParse(box.Text, out var value) && value != get(current)) Commit(c => set(c, value));
            if (Clicker is { } after) box.Text = ClickerNumber.Format(get(after));   // the normalized value, also after a bad entry
        }
        box.LostKeyboardFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
        OnSync(c => { if (!box.IsKeyboardFocused) box.Text = ClickerNumber.Format(get(c)); });
        return box;
    }

    /// <summary>A drop-down over fixed values.</summary>
    protected ComboBox Choice<T>(IEnumerable<(T Value, string Text)> items, Func<ClickerSettings, T> get, Action<ClickerSettings, T> set, double minWidth = 150)
    {
        var combo = new ComboBox { MinWidth = minWidth };
        foreach (var (value, text) in items) combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T value }) Commit(c => set(c, value));
        };
        OnSync(c =>
        {
            var current = get(c);
            foreach (ComboBoxItem item in combo.Items)
                if (item.Tag is T v && EqualityComparer<T>.Default.Equals(v, current)) { combo.SelectedItem = item; break; }
        });
        return combo;
    }

    /// <summary>Shows the element only while the condition holds.</summary>
    protected void VisibleWhen(UIElement element, Func<ClickerSettings, bool> condition)
        => OnSync(c => element.Visibility = condition(c) ? Visibility.Visible : Visibility.Collapsed);

    protected void EnabledWhen(UIElement element, Func<ClickerSettings, bool> condition)
        => OnSync(c => element.IsEnabled = condition(c));

    protected static Button Btn(string text, Action onClick, bool accent = false, string? tooltip = null)
    {
        var b = new Button { Content = text, ToolTip = tooltip, Margin = new Thickness(0, 0, 8, 0) };
        if (accent) b.Style = (Style)Application.Current.FindResource("AccentButton");
        b.Click += (_, _) => onClick();
        return b;
    }

    protected static WrapPanel Buttons(params UIElement[] children)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    /// <summary>A list row: text on the left (ellipsis), controls on the right.</summary>
    protected static Grid ListRow(UIElement left, params UIElement[] right)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(left);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var r in right)
        {
            if (r is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(8, 0, 0, 0);
            panel.Children.Add(r);
        }
        Grid.SetColumn(panel, 1);
        row.Children.Add(panel);
        return row;
    }

    /// <summary>Two lines: a bold name and a muted detail line.</summary>
    protected static StackPanel TwoLines(string name, string detail)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var n = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        n.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        text.Children.Add(n);
        if (!string.IsNullOrEmpty(detail))
            text.Children.Add(new TextBlock { Text = detail, Style = (Style)Application.Current.FindResource("MutedStyle"), TextTrimming = TextTrimming.CharacterEllipsis });
        return text;
    }

    /// <summary>Swaps a list in a copy-then-assign way (the controller reads the settings on pool threads) and saves.</summary>
    protected void CommitList<T>(Func<ClickerSettings, List<T>> get, Action<ClickerSettings, List<T>> set, Func<List<T>, bool> edit)
    {
        if (Clicker is not { } c) return;
        var copy = new List<T>(get(c));
        if (edit(copy)) Commit(s => set(s, copy));
    }
}
