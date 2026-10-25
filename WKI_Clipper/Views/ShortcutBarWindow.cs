using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WKI_Clipper.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views;

/// <summary>
/// A plain clickable tile: transparent at rest, a soft fill on hover, the active wash while pressed. Fires <see cref="Clicked"/> on
/// a full click (press and release over it). Built from the theme's brushes only, so a theme change recolours it.
/// </summary>
internal sealed class BarButton : Border
{
    public const double Size = 40;

    private readonly Brush _hover = (Brush)Application.Current.FindResource("SoftFillHoverBrush");
    private readonly Brush _pressed = (Brush)Application.Current.FindResource("ActiveBrush");

    public event Action? Clicked;

    public BarButton(FrameworkElement content, double size = Size)
    {
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(10);
        Background = Brushes.Transparent;   // transparent but present: the hit test needs a brush
        Child = content;
        Cursor = System.Windows.Input.Cursors.Hand;
        Focusable = false;
        Margin = new Thickness(2, 0, 2, 0);
        VerticalAlignment = VerticalAlignment.Center;

        MouseEnter += (_, _) => { if (!IsMouseCaptured) Background = _hover; };
        MouseLeave += (_, _) => { if (!IsMouseCaptured) Background = Brushes.Transparent; };
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); Background = _pressed; e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!IsMouseCaptured) return;
            bool inside = IsMouseOver;
            ReleaseMouseCapture();
            Background = inside ? _hover : Brushes.Transparent;
            e.Handled = true;
            if (inside) Clicked?.Invoke();
        };
    }

    /// <summary>What a click does, without a mouse (tests, keyboard later).</summary>
    internal void RaiseClick() => Clicked?.Invoke();
}

/// <summary>
/// What the shortcut bar shows. Collapsed: the game chip (icon of the game in front, else the WKI logo) and "›". Expanded: "‹", the
/// games, launchers and browsers as 32 px icons with a separator between non-empty groups, then the gear. It only draws and
/// reports clicks (<see cref="ItemClicked"/>, <see cref="ExpandedChanged"/>, <see cref="EditClicked"/>); what they do is the owner's
/// business, so it renders the same in a test without a window.
/// </summary>
public sealed class ShortcutBarView : Border
{
    private const double IconSize = 32;

    private readonly StackPanel _collapsed = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _expandedRow = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _scroller;
    private readonly Border _gearSeparatorHost = new() { VerticalAlignment = VerticalAlignment.Center };

    private readonly BarButton _chip;
    private readonly BarButton _expandButton;
    private readonly BarButton _collapseButton;
    private readonly BarButton _editButton;
    private readonly Border _chipHost = new() { Width = IconSize, Height = IconSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, (BarButton Button, BarItem Item)> _buttons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();
    private int _separators;
    private bool _chipShowsGame;

    public bool Expanded { get; private set; }

    /// <summary>A tile was clicked: the entry's id.</summary>
    public event Action<string>? ItemClicked;

    /// <summary>The user clicked "‹" or "›" (or the chip): the state they ask for. The owner applies it with <see cref="SetExpanded"/>.</summary>
    public event Action<bool>? ExpandedChanged;

    /// <summary>The gear was clicked.</summary>
    public event Action? EditClicked;

    public ShortcutBarView(bool expanded)
    {
        Expanded = expanded;
        Height = BarLayout.HeightDip;
        CornerRadius = new CornerRadius(18);
        Background = (Brush)Application.Current.FindResource("SurfaceBrush");
        BorderBrush = (Brush)Application.Current.FindResource("LineBrush");
        BorderThickness = new Thickness(1);
        Padding = new Thickness(8, 0, 8, 0);

        var muted = (Brush)Application.Current.FindResource("MutedBrush");
        FrameworkElement Glyph(string glyph)
        {
            var g = IconGlyph.Make(glyph, 14);
            g.Foreground = muted;
            return g;
        }

        _chip = new BarButton(_chipHost);
        _chip.Clicked += () => ExpandedChanged?.Invoke(true);   // the chip is just as good a handle as the arrow
        _expandButton = new BarButton(Glyph(IconGlyph.ChevronRight), 28) { ToolTip = L.T("Verknüpfungen einblenden", "Show shortcuts") };
        _expandButton.Clicked += () => ExpandedChanged?.Invoke(true);
        _collapsed.Children.Add(_chip);
        _collapsed.Children.Add(_expandButton);

        _collapseButton = new BarButton(Glyph(IconGlyph.ChevronLeft), 28) { ToolTip = L.T("Verknüpfungen ausblenden", "Hide shortcuts") };
        _collapseButton.Clicked += () => ExpandedChanged?.Invoke(false);
        _editButton = new BarButton(Glyph(IconGlyph.Settings), 32) { ToolTip = L.T("Verknüpfungen bearbeiten", "Edit shortcuts") };
        _editButton.Clicked += () => EditClicked?.Invoke();

        _scroller = new ScrollViewer
        {
            Content = _items, Focusable = false, VerticalAlignment = VerticalAlignment.Center,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        // A bar wider than the screen scrolls with the wheel instead of running off it.
        _scroller.PreviewMouseWheel += (_, e) =>
        {
            _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        };

        _expandedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _expandedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _expandedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _expandedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_collapseButton, 0);
        Grid.SetColumn(_scroller, 1);
        Grid.SetColumn(_gearSeparatorHost, 2);
        Grid.SetColumn(_editButton, 3);
        _expandedRow.Children.Add(_collapseButton);
        _expandedRow.Children.Add(_scroller);
        _expandedRow.Children.Add(_gearSeparatorHost);
        _expandedRow.Children.Add(_editButton);

        var root = new Grid();
        root.Children.Add(_collapsed);
        root.Children.Add(_expandedRow);
        Child = root;

        SetGame(null, null);
        ApplyState();
    }

    // ---- feeding ----

    /// <summary>The tiles: groups in the order games, launchers, browsers; a separator only between two non-empty groups.</summary>
    public void SetItems(IReadOnlyList<BarItem> items)
    {
        _items.Children.Clear();
        _buttons.Clear();
        _order.Clear();
        _separators = 0;

        bool any = false;
        foreach (var group in new[] { Models.ShortcutGroup.Game, Models.ShortcutGroup.Launcher, Models.ShortcutGroup.Browser })
        {
            var inGroup = items.Where(i => i.Group == group).ToList();
            if (inGroup.Count == 0) continue;
            if (any)
            {
                _items.Children.Add(Separator());
                _separators++;
            }
            any = true;
            foreach (var item in inGroup)
            {
                var button = MakeItem(item);
                _buttons[item.Id] = (button, item);
                _order.Add(item.Id);
                _items.Children.Add(button);
            }
        }
        // The gear sits behind its own separator - unless there is nothing before it to separate from.
        _gearSeparatorHost.Child = any ? Separator() : null;
    }

    public void SetExpanded(bool expanded)
    {
        Expanded = expanded;
        ApplyState();
    }

    /// <summary>The chip: the game in front (its icon, its name as tooltip) or - without one - the WKI logo.</summary>
    public void SetGame(string? name, string? icon)
    {
        _chipShowsGame = !string.IsNullOrWhiteSpace(name);
        _chipHost.Child = _chipShowsGame ? BarIcons.Tile(name!, icon, IconSize) : WkiLogo();
        _chip.ToolTip = _chipShowsGame ? name : "WKI Clipper";
    }

    private void ApplyState()
    {
        _collapsed.Visibility = Expanded ? Visibility.Collapsed : Visibility.Visible;
        _expandedRow.Visibility = Expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- pieces ----

    private BarButton MakeItem(BarItem item)
    {
        var button = new BarButton(BarIcons.Tile(item.Name, item.IconPng, IconSize))
        {
            ToolTip = item.Missing ? $"{item.Name} - {L.T("nicht gefunden", "not found")}" : item.Name,
            Opacity = item.Missing ? 0.38 : 1.0,   // a greyed-out entry: its program is gone, but it can still be tried or removed
        };
        var id = item.Id;
        button.Clicked += () => ItemClicked?.Invoke(id);
        return button;
    }

    /// <summary>The same accent disc with a "W" the sidebar's header wears.</summary>
    private static FrameworkElement WkiLogo()
    {
        var grid = new Grid { Width = IconSize, Height = IconSize, IsHitTestVisible = false };
        grid.Children.Add(new Ellipse { Fill = (Brush)Application.Current.FindResource("AccentGradientBrush") });
        grid.Children.Add(new TextBlock
        {
            Text = "W", FontSize = 16, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        return grid;
    }

    private static FrameworkElement Separator() => new Border
    {
        Width = 1, Height = 28, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = (Brush)Application.Current.FindResource("LineBrush"),
    };

    /// <summary>
    /// The width the content wants, in DIPs, with all changes made so far included. A change of visibility or tiles only marks the
    /// layout dirty; asking for DesiredSize right away would return the old answer, so the pending layout runs first.
    /// </summary>
    internal double NaturalWidthDip()
    {
        UpdateLayout();
        Measure(new Size(double.PositiveInfinity, BarLayout.HeightDip));
        return DesiredSize.Width;
    }

    // ---- test seams: what is on screen, and the clicks without a mouse ----

    /// <summary>The tiles currently shown, in order (none while collapsed).</summary>
    internal IReadOnlyList<string> VisibleItemIds => Expanded ? _order.ToList() : new List<string>();
    internal int GroupSeparatorCount => _separators;
    internal bool ChipShown => !Expanded;
    internal bool ChipShowsGame => _chipShowsGame;
    internal string? ChipTooltip => _chip.ToolTip as string;
    internal bool ExpandButtonShown => !Expanded;
    internal bool CollapseButtonShown => Expanded;
    internal bool EditButtonShown => Expanded;
    internal double ItemOpacity(string id) => _buttons[id].Button.Opacity;
    internal string? ItemTooltip(string id) => _buttons[id].Button.ToolTip as string;
    internal bool ItemShowsImage(string id) => (_buttons[id].Button.Child as Image) != null;
    internal string? ItemLetter(string id)
        => (_buttons[id].Button.Child as Border)?.Child is TextBlock t ? t.Text : null;

    internal void ClickItem(string id) { if (Expanded && _buttons.TryGetValue(id, out var b)) b.Button.RaiseClick(); }
    internal void ClickEdit() { if (Expanded) _editButton.RaiseClick(); }
    internal void ClickCollapse() { if (Expanded) _collapseButton.RaiseClick(); }
    internal void ClickExpand() { if (!Expanded) _expandButton.RaiseClick(); }
    internal void ClickChip() { if (!Expanded) _chip.RaiseClick(); }
}

/// <summary>Loads the cached icon PNGs for the bar and the edit dialog. A file that is gone or unreadable is simply "no icon" (a letter tile is shown).</summary>
internal static class BarIcons
{
    /// <summary>The picture of an entry: its icon, or - when there is none (yet) or its cache file is gone - the first letter on a soft tile.</summary>
    public static FrameworkElement Tile(string name, string? pngPath, double size)
    {
        var source = Load(pngPath);
        return source != null ? Picture(source, size) : Letter(name, size);
    }

    public static FrameworkElement Picture(BitmapSource source, double size)
    {
        var image = new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    public static FrameworkElement Letter(string name, double size)
    {
        var trimmed = (name ?? "").Trim();
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(Math.Max(4, size / 4)), IsHitTestVisible = false,
            Background = (Brush)Application.Current.FindResource("SoftFillHoverBrush"),
            Child = new TextBlock
            {
                Text = trimmed.Length > 0 ? trimmed[..1].ToUpperInvariant() : "?",
                FontSize = size / 2, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    // The cache file name carries a hash of the source file's timestamp: the same path is always the same picture.
    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Cache.TryGetValue(path, out var hit) && (hit != null || !File.Exists(path))) return hit;
        try
        {
            if (!File.Exists(path)) { Cache[path] = null; return null; }
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;   // read now: the file may be replaced later
            image.DecodePixelWidth = 96;                    // 32 DIP is up to 96 px at 300 %
            image.EndInit();
            image.Freeze();
            Cache[path] = image;
            return image;
        }
        catch (Exception)
        {
            Cache[path] = null;
            return null;
        }
    }
}

/// <summary>
/// The shortcut bar: a glass strip at the top centre of the board's monitor, shown and hidden with the board by
/// <see cref="WidgetHost"/>. It never takes the keyboard on show (like the widgets) and stays above them. It wraps
/// <see cref="ShortcutBarView"/> and connects it to <see cref="ShortcutService"/>: tiles launch, the arrows fold the bar (and the
/// state is remembered), the gear asks the host for the editor, and changes of the service - on whatever thread they happen -
/// refresh it on the UI thread.
/// </summary>
public sealed class ShortcutBarWindow : Window
{
    private readonly ShortcutService _service;
    private readonly Func<GameInfo?> _currentGame;
    private WinForms.Screen? _screen;
    private LayoutRect? _rect;

    public ShortcutBarView View { get; }

    /// <summary>The gear was clicked: open the edit dialog.</summary>
    public event Action? EditRequested;

    /// <summary>The bar's rectangle changed (it folded or unfolded, items came or went): widgets under it have to be looked at again.</summary>
    public event Action? RectChanged;

    /// <summary>Where the bar is (physical pixels), computed from the monitor and the content - null before the first placement.</summary>
    public LayoutRect? Rect => _rect;

    public ShortcutBarWindow(ShortcutService service, Func<GameInfo?> currentGame)
    {
        _service = service;
        _currentGame = currentGame;

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
        Height = BarLayout.HeightDip;

        View = new ShortcutBarView(service.Expanded);
        View.ItemClicked += id => _service.Launch(id);
        View.ExpandedChanged += expanded =>
        {
            _service.SetExpanded(expanded);
            View.SetExpanded(expanded);
            Place();
        };
        View.EditClicked += () => EditRequested?.Invoke();
        Content = View;

        _service.ItemsChanged += OnItemsChanged;
        Closed += (_, _) => _service.ItemsChanged -= OnItemsChanged;
    }

    // The service raises this on the thread that changed something (a pool thread for a scan or a game change).
    private void OnItemsChanged(object? sender, EventArgs e)
    {
        try { Dispatcher.BeginInvoke(new Action(Refresh)); }
        catch (InvalidOperationException) { /* the dispatcher is shutting down */ }
    }

    /// <summary>Reads the service again: tiles, folded state, the game chip. Cheap; every icon is cached.</summary>
    public void Refresh()
    {
        View.SetItems(_service.VisibleItems());
        View.SetExpanded(_service.Expanded);

        var game = _currentGame();
        if (game is null)
        {
            View.SetGame(null, null);
        }
        else
        {
            var exe = GameContext.ImagePath(game.Pid);   // query-only rights, like the game folders; null for an elevated or exited game
            View.SetGame(game.DisplayName, _service.IconFor(exe, "gamechip_" + ShortcutPaths.Fold(game.ExeName)));
        }
        if (_screen != null && IsVisible) Place();
    }

    /// <summary>Top centre of the screen's work area; the width follows the content.</summary>
    public void ShowOn(WinForms.Screen screen)
    {
        _screen = screen;
        Refresh();
        Place();
        Show();
        Place();   // again with the real HWND
    }

    public void Reposition(WinForms.Screen screen)
    {
        _screen = screen;
        Place();
    }

    public new void Hide() => base.Hide();

    private void Place()
    {
        if (_screen is null) return;
        var area = DisplayGeometry.WorkArea(_screen);
        double scale = DisplayGeometry.ScaleOf(_screen);

        var rect = BarLayout.BarRect(area, View.NaturalWidthDip(), scale);
        Width = rect.W / scale;
        Height = rect.H / scale;
        DisplayGeometry.MoveTo(this, rect.X, rect.Y);

        bool changed = _rect != rect;
        _rect = rect;
        if (changed) RectChanged?.Invoke();
    }
}
