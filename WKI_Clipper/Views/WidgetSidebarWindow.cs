using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WKI_Clipper.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views;

/// <summary>
/// One row of the sidebar: icon, label and an optional count pill. The button is a toggle —
/// checked = the widget is open. Collapsed, only the icon stays (count becomes a dot, the
/// label moves into the tooltip).
/// </summary>
public sealed class SidebarEntry
{
    public ToggleButton Button { get; }

    private readonly TextBlock _label;
    private readonly Border _pill;
    private readonly TextBlock _pillText;
    private readonly Ellipse _dot;
    private string _labelText;
    private int _badge;
    private bool _collapsed;

    public SidebarEntry(FrameworkElement icon, string label)
    {
        _labelText = label;
        _label = new TextBlock { Text = label, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _pillText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        _pill = new Border
        {
            CornerRadius = new CornerRadius(9), MinWidth = 20, Padding = new Thickness(6, 1, 6, 1),
            Background = (Brush)Application.Current.FindResource("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center, Child = _pillText, Visibility = Visibility.Collapsed
        };
        _dot = new Ellipse
        {
            Width = 8, Height = 8, Fill = (Brush)Application.Current.FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -3, -4, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };

        var iconBox = new Grid { Width = 22, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        iconBox.Children.Add(icon);
        iconBox.Children.Add(_dot);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_label, 1);
        Grid.SetColumn(_pill, 2);
        row.Children.Add(iconBox);
        row.Children.Add(_label);
        row.Children.Add(_pill);

        Button = new ToggleButton
        {
            Content = row,
            Template = (ControlTemplate)Application.Current.FindResource("SidebarItemTemplate"),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Foreground = (Brush)Application.Current.FindResource("TextBrush")
        };
        ToolTipService.SetPlacement(Button, PlacementMode.Right);
        ToolTipService.SetInitialShowDelay(Button, 250);
    }

    public void SetLabel(string text)
    {
        _labelText = text;
        _label.Text = text;
        Refresh();
    }

    public void SetBadge(int count)
    {
        _badge = Math.Max(0, count);
        _pillText.Text = _badge > 99 ? "99+" : _badge.ToString();
        Refresh();
    }

    public void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        Refresh();
    }

    private void Refresh()
    {
        _label.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        _pill.Visibility = !_collapsed && _badge > 0 ? Visibility.Visible : Visibility.Collapsed;
        _dot.Visibility = _collapsed && _badge > 0 ? Visibility.Visible : Visibility.Collapsed;
        Button.Padding = _collapsed ? new Thickness(13, 9, 13, 9) : new Thickness(12, 9, 12, 9);
        Button.HorizontalAlignment = _collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        Button.ToolTip = _collapsed ? (_badge > 0 ? $"{_labelText} · {_badge}" : _labelText) : null;
    }
}

/// <summary>
/// The board's sidebar (replaces the old launcher pill at the top): a tall glass panel at
/// the left edge of the board's monitor with a live status header, the widgets grouped in
/// sections, the layout switches and the clock in the footer. Collapses to an icon rail.
/// </summary>
public sealed class WidgetSidebarWindow : Window
{
    public const double ExpandedWidth = 280, CollapsedWidth = 72, EdgeMarginDip = 16;

    private readonly StackPanel _items = new();
    private readonly List<SidebarEntry> _entries = new();
    private readonly List<(TextBlock Title, Border Rule)> _sectionHeads = new();

    private readonly Grid _header = new();
    private readonly StackPanel _headerTexts = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Ellipse _statusDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _collapseButton;
    private readonly TextBlock _collapseGlyph = IconGlyph.Make("", 11);
    private readonly Border _headerBox;

    private readonly WrapPanel _footerChips = new();
    private readonly Grid _footerLine = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _footerEntryHost = new();
    private readonly TextBlock _clock = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(ToggleButton Chip, TextBlock Label)> _chips = new();

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private WinForms.Screen? _screen;

    public bool Collapsed { get; private set; }

    /// <summary>User clicked the collapse/expand button; arg = new collapsed state.</summary>
    public event Action<bool>? CollapseToggled;

    /// <summary>Polled every second while visible for the header's status line.</summary>
    public Func<(string Text, BoardStatusKind Kind)>? StatusProvider { get; set; }

    public WidgetSidebarWindow(bool collapsed)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "WKI Clipper";

        // --- header: avatar · name + live status · collapse ---
        var avatar = MakeAvatar();
        _headerTexts.Children.Add(new TextBlock { Text = "WKI Clipper", FontSize = 15, FontWeight = FontWeights.SemiBold });
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        statusRow.Children.Add(_statusDot);
        statusRow.Children.Add(_statusText);
        _headerTexts.Children.Add(statusRow);

        _collapseGlyph.Foreground = (Brush)Application.Current.FindResource("MutedBrush");
        _collapseButton = new Button
        {
            Content = _collapseGlyph, Width = 28, Height = 28, Padding = new Thickness(0),
            Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center
        };
        _collapseButton.Click += (_, _) => CollapseToggled?.Invoke(!Collapsed);

        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _header.Children.Add(avatar);
        _header.Children.Add(_headerTexts);
        _header.Children.Add(_collapseButton);
        _headerBox = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(10),
            Background = (Brush)Application.Current.FindResource("SoftFillBrush"),
            Child = _header
        };

        // --- footer: layout chips, settings row, clock ---
        var footer = new StackPanel();
        footer.Children.Add(_footerChips);
        _footerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _footerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footerLine.Children.Add(_footerEntryHost);
        Grid.SetColumn(_clock, 1);
        _footerLine.Children.Add(_clock);
        footer.Children.Add(_footerLine);
        var footerBox = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("LineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 8, 0, 0),
            Child = footer
        };

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _items, Margin = new Thickness(0, 6, 0, 0), Focusable = false
        };

        var dock = new DockPanel();
        DockPanel.SetDock(_headerBox, Dock.Top);
        DockPanel.SetDock(footerBox, Dock.Bottom);
        dock.Children.Add(_headerBox);
        dock.Children.Add(footerBox);
        dock.Children.Add(scroller);

        Content = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = (Brush)Application.Current.FindResource("SurfaceBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = dock
        };

        _tick.Tick += (_, _) => UpdateLive();
        SetCollapsed(collapsed);
    }

    private static FrameworkElement MakeAvatar()
    {
        var grid = new Grid { Width = 38, Height = 38, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Ellipse
        {
            Fill = new RadialGradientBrush(
                new GradientStopCollection
                {
                    new((Color)Application.Current.FindResource("AccentLightColor"), 0),
                    new((Color)Application.Current.FindResource("AccentColor"), 0.55),
                    new(Color.FromRgb(0xC8, 0x46, 0x1A), 1)
                }) { GradientOrigin = new Point(0.3, 0.25), Center = new Point(0.35, 0.3), RadiusX = 0.8, RadiusY = 0.8 },
            Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x7A, 0x2C)),
            StrokeThickness = 3
        });
        grid.Children.Add(new TextBlock
        {
            Text = "W", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    // ---- building (by the host) ----

    public void AddSectionHeader(string title)
    {
        var t = new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Style = (Style)Application.Current.FindResource("SectionStyle"),
            Margin = new Thickness(14, _sectionHeads.Count == 0 ? 8 : 16, 0, 6)
        };
        var rule = new Border
        {
            Height = 1, Width = 30, Margin = new Thickness(0, 10, 0, 10),
            Background = (Brush)Application.Current.FindResource("LineBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _items.Children.Add(t);
        _items.Children.Add(rule);
        _sectionHeads.Add((t, rule));
        ApplyCollapsedLayout();
    }

    public SidebarEntry AddEntry(FrameworkElement icon, string label)
    {
        var e = new SidebarEntry(icon, label);
        e.SetCollapsed(Collapsed);
        _entries.Add(e);
        _items.Children.Add(e.Button);
        return e;
    }

    /// <summary>The settings row in the footer (same look as a widget row).</summary>
    public SidebarEntry AddFooterEntry(FrameworkElement icon, string label)
    {
        var e = new SidebarEntry(icon, label);
        e.SetCollapsed(Collapsed);
        _entries.Add(e);
        _footerEntryHost.Children.Add(e.Button);
        return e;
    }

    /// <summary>A layout switch chip (Raster, Überlappen) in the footer.</summary>
    public ToggleButton AddFooterChip(FrameworkElement icon, string label)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), FontSize = 12.5 };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(text);
        var chip = new ToggleButton
        {
            Content = content,
            Template = (ControlTemplate)Application.Current.FindResource("LauncherToggleTemplate"),
            Padding = new Thickness(11, 7, 11, 7),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            Background = (Brush)Application.Current.FindResource("SoftFillBrush")
        };
        ToolTipService.SetPlacement(chip, PlacementMode.Right);
        ToolTipService.SetShowOnDisabled(chip, true);
        _chips.Add((chip, text));
        _footerChips.Children.Add(chip);
        ApplyCollapsedLayout();
        return chip;
    }

    // ---- collapse ----

    public void SetCollapsed(bool collapsed)
    {
        Collapsed = collapsed;
        foreach (var e in _entries) e.SetCollapsed(collapsed);
        ApplyCollapsedLayout();
        Width = collapsed ? CollapsedWidth : ExpandedWidth;
        if (_screen != null && IsVisible) Reposition(_screen);
    }

    private void ApplyCollapsedLayout()
    {
        bool c = Collapsed;
        foreach (var (title, rule) in _sectionHeads)
        {
            title.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
            rule.Visibility = c ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var (_, label) in _chips) label.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (chip, label) in _chips) chip.Margin = c ? new Thickness(0, 0, 0, 6) : new Thickness(0, 0, 6, 6);

        _headerTexts.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
        _headerBox.Background = c ? Brushes.Transparent : (Brush)Application.Current.FindResource("SoftFillBrush");
        _headerBox.Padding = c ? new Thickness(0) : new Thickness(10);
        // Collapsed: avatar on top, the expand button centred under it.
        Grid.SetColumn(_collapseButton, c ? 0 : 2);
        Grid.SetRow(_collapseButton, c ? 1 : 0);
        _collapseButton.HorizontalAlignment = c ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        _collapseButton.Margin = c ? new Thickness(0, 6, 0, 0) : new Thickness(0);
        _collapseGlyph.Text = c ? "" : "";   // chevron right / left
        _collapseButton.ToolTip = c ? L.T("Ausklappen", "Expand") : L.T("Einklappen", "Collapse");
        Grid.SetColumn(_headerTexts, 1);

        _footerChips.Orientation = c ? Orientation.Vertical : Orientation.Horizontal;
        _footerChips.HorizontalAlignment = c ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _clock.FontSize = c ? 12 : 15;
        Grid.SetColumn(_clock, c ? 0 : 1);
        Grid.SetRow(_clock, 0);
        _clock.HorizontalAlignment = c ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        _footerEntryHost.Visibility = Visibility.Visible;
        if (c)
        {
            // Rail: settings icon, then the clock below it.
            if (_footerLine.RowDefinitions.Count == 0)
            {
                _footerLine.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _footerLine.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            Grid.SetRow(_clock, 1);
            _clock.Margin = new Thickness(0, 6, 0, 0);
        }
        else
        {
            _footerLine.RowDefinitions.Clear();
            _clock.Margin = new Thickness(8, 0, 4, 0);
        }
    }

    // ---- show / place ----

    /// <summary>Left edge of the monitor's work area, full height (physical placement, DIP size).</summary>
    public void ShowOn(WinForms.Screen screen)
    {
        _screen = screen;
        UpdateLive();
        Reposition(screen);
        Show();
        Reposition(screen);   // again with the real HWND size after showing
        _tick.Start();
    }

    public void Reposition(WinForms.Screen screen)
    {
        _screen = screen;
        var area = DisplayGeometry.WorkArea(screen);
        double s = DisplayGeometry.ScaleOf(screen);
        Width = Collapsed ? CollapsedWidth : ExpandedWidth;
        Height = Math.Max(240, area.H / s - 2 * EdgeMarginDip);
        DisplayGeometry.MoveTo(this, area.X + EdgeMarginDip * s, area.Y + EdgeMarginDip * s);
    }

    public new void Hide()
    {
        _tick.Stop();
        base.Hide();
    }

    private void UpdateLive()
    {
        _clock.Text = DateTime.Now.ToString("HH:mm");
        if (StatusProvider?.Invoke() is not { } status) return;
        _statusText.Text = status.Text;
        _statusText.Foreground = (Brush)Application.Current.FindResource("MutedBrush");
        _statusDot.Fill = status.Kind switch
        {
            BoardStatusKind.Active => (Brush)Application.Current.FindResource("SuccessBrush"),
            BoardStatusKind.Recording => (Brush)Application.Current.FindResource("DangerBrush"),
            _ => (Brush)Application.Current.FindResource("FaintBrush")
        };
    }
}
