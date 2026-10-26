using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WKI_Clipper.Native;
using WKI_Clipper.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using RadioButton = System.Windows.Controls.RadioButton;
using Orientation = System.Windows.Controls.Orientation;
using WinForms = System.Windows.Forms;

namespace WKI_Clipper.Views;

/// <summary>
/// The colour picker of the board: two tabs, "Akzent" and "Glas", each with ready-made swatches, a custom colour (hue and
/// saturation sliders, a hex field, a preview) and "Standard". A click on a swatch goes on at once and is saved; a custom
/// colour goes on when the sliders rest or the hex field is left. All decisions are in <see cref="ThemePickerState"/>.
///
/// Board style like the widgets (glass surface, hairline edge), shown without taking the keyboard from the board; a click into
/// it activates it so the hex field can be typed in. The host closes it on an outside click, on <see cref="CloseRequested"/>
/// (Esc) and with the board. It is NOT rebuilt when the theme changes - it is the window you change the theme in - so
/// everything that is a palette colour here is a resource reference and follows the switch by itself.
/// </summary>
public sealed class ThemePickerWindow : Window
{
    private const double ContentWidth = 340;

    private readonly ThemePickerState _state;
    private readonly Dictionary<ThemeTab, TabView> _tabs = new();
    private readonly Dictionary<ThemeTab, RadioButton> _tabButtons = new();
    private ThemeTab _active = ThemeTab.Accent;
    private bool _closing;

    /// <summary>Esc was pressed in the picker: the host closes it.</summary>
    public event Action? CloseRequested;

    /// <summary>Everything one tab shows and the bits of it the picker has to update.</summary>
    private sealed class TabView
    {
        public StackPanel Root = new();
        public readonly Dictionary<string, ToggleButton> Swatches = new();
        public readonly List<string> SwatchNames = new();
        public Slider Hue = null!, Saturation = null!;
        public TextBox Hex = null!;
        public System.Windows.Shapes.Ellipse Preview = null!;
        public TextBlock Note = null!;
        public Button Standard = null!;
        public DispatcherTimer Rest = null!;
        public bool Syncing;
    }

    public ThemePickerWindow(ThemePickerState state)
    {
        _state = state;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = L.T("Farben", "Colours");

        var stack = new StackPanel { Width = ContentWidth };
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Farben", "Colours"), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 10)
        });

        var tabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        tabRow.Children.Add(MakeTabButton(ThemeTab.Accent, L.T("Akzent", "Accent")));
        tabRow.Children.Add(MakeTabButton(ThemeTab.Glass, L.T("Glas", "Glass")));
        stack.Children.Add(tabRow);

        foreach (var tab in new[] { ThemeTab.Accent, ThemeTab.Glass })
        {
            var view = BuildTab(tab);
            _tabs[tab] = view;
            stack.Children.Add(view.Root);
        }

        var surface = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderBrush = (Brush)Application.Current.FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Child = stack
        };
        surface.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        Content = surface;

        PreviewKeyDown += (_, e) => { if (HandleKey(e.Key)) e.Handled = true; };
        SelectTab(ThemeTab.Accent);
        foreach (var tab in _tabs.Keys) Refresh(tab);
    }

    // ---- building ----

    private RadioButton MakeTabButton(ThemeTab tab, string text)
    {
        var button = new RadioButton
        {
            Content = text,
            GroupName = "theme-picker-tabs",
            Style = (Style)Application.Current.FindResource("TabButtonStyle"),
            Focusable = false
        };
        button.Checked += (_, _) => SelectTab(tab);
        _tabButtons[tab] = button;
        return button;
    }

    private TabView BuildTab(ThemeTab tab)
    {
        var v = new TabView();
        var root = v.Root;

        // swatches: a circle and a name, two to a row
        var grid = new UniformGrid { Columns = 2 };
        foreach (var preset in _state.Presets(tab))
        {
            string name = L.T(preset.NameDe, preset.NameEn);
            var toggle = new ToggleButton
            {
                Template = (ControlTemplate)Application.Current.FindResource("LauncherToggleTemplate"),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = SwatchContent(tab, preset, name),
                ToolTip = name
            };
            toggle.Click += (_, _) =>
            {
                _state.ChoosePreset(tab, preset.Id);
                Refresh(tab);
            };
            v.Swatches[preset.Id] = toggle;
            v.SwatchNames.Add(name);
            grid.Children.Add(toggle);
        }
        root.Children.Add(grid);

        root.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 6, 0, 10),
            Background = (Brush)Application.Current.FindResource("LineBrush")
        });

        // custom colour
        root.Children.Add(new TextBlock
        {
            Text = L.T("Eigene Farbe", "Custom colour").ToUpperInvariant(),
            Style = (Style)Application.Current.FindResource("SectionStyle"),
            Margin = new Thickness(2, 0, 0, 6)
        });
        v.Hue = new Slider { Minimum = 0, Maximum = 360, Width = 200, VerticalAlignment = VerticalAlignment.Center };
        v.Saturation = new Slider { Minimum = 0, Maximum = 1, Width = 200, VerticalAlignment = VerticalAlignment.Center };
        root.Children.Add(SliderRow(L.T("Farbton", "Hue"), v.Hue));
        root.Children.Add(SliderRow(L.T("Sättigung", "Saturation"), v.Saturation));

        v.Hex = new TextBox { Width = 96, MaxLength = 9, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        v.Preview = new System.Windows.Shapes.Ellipse
        {
            Width = 26, Height = 26, Margin = new Thickness(12, 0, 0, 0), StrokeThickness = 1,
            Stroke = (Brush)Application.Current.FindResource("LineBrush"), VerticalAlignment = VerticalAlignment.Center
        };
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        hexRow.Children.Add(new TextBlock { Text = "Hex", Width = 76, VerticalAlignment = VerticalAlignment.Center });
        hexRow.Children.Add(v.Hex);
        hexRow.Children.Add(v.Preview);
        root.Children.Add(hexRow);

        v.Note = new TextBlock
        {
            Text = L.T("Kein gültiger Farbwert – erlaubt ist #RRGGBB.", "Not a valid colour - use #RRGGBB."),
            Foreground = (Brush)Application.Current.FindResource("DangerBrush"),
            FontSize = 12, Margin = new Thickness(76, 4, 0, 0), Visibility = Visibility.Collapsed
        };
        root.Children.Add(v.Note);

        v.Standard = new Button
        {
            Content = L.T("Standard", "Default"), HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 12, 0, 0)
        };
        v.Standard.Click += (_, _) =>
        {
            _state.Reset(tab);
            Refresh(tab);
        };
        root.Children.Add(v.Standard);

        // sliders: the hex and the preview follow while dragging; the theme goes on when they rest (a switch rebuilds the board)
        v.Rest = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        v.Rest.Tick += (_, _) =>
        {
            v.Rest.Stop();
            CommitHex(tab);
        };
        RoutedPropertyChangedEventHandler<double> moved = (_, _) =>
        {
            if (v.Syncing) return;
            string hex = _state.HexFromHueSaturation(tab, v.Hue.Value, v.Saturation.Value);
            SetHexText(v, hex);
            ShowPreview(v, hex);
            v.Note.Visibility = Visibility.Collapsed;
            v.Rest.Stop();
            v.Rest.Start();
        };
        v.Hue.ValueChanged += moved;
        v.Saturation.ValueChanged += moved;

        // hex field: valid text previews while typing; Enter or leaving the field applies it, bad text goes back
        v.Hex.TextChanged += (_, _) =>
        {
            if (v.Syncing) return;
            if (ThemePaletteBuilder.NormalizeHex(v.Hex.Text) is { } hex)
            {
                ShowPreview(v, hex);
                SyncSliders(tab, v, hex);
            }
            v.Note.Visibility = Visibility.Collapsed;
        };
        v.Hex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitHex(tab);
            e.Handled = true;
        };
        v.Hex.LostKeyboardFocus += (_, _) => HexLostFocus(tab);

        return v;
    }

    private static FrameworkElement SwatchContent(ThemeTab tab, ThemePreset preset, string name)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Width = 132 };   // a fixed width: the names line up
        row.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 22, Height = 22, Margin = new Thickness(0, 0, 9, 0), StrokeThickness = 1,
            Fill = new SolidColorBrush(SwatchColor(tab, preset)),
            Stroke = (Brush)Application.Current.FindResource("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 });
        return row;
    }

    /// <summary>
    /// The accent is what it says. A glass tint is almost black by nature, so its swatch shows the colour the glass gives its
    /// edges and hover rows - the same tint, light enough to tell the glasses apart.
    /// </summary>
    private static Color SwatchColor(ThemeTab tab, ThemePreset preset)
    {
        if (tab == ThemeTab.Accent) return ThemePaletteBuilder.ParseColor(preset.Hex) ?? Colors.Gray;
        var palette = ThemePaletteBuilder.Build(new Models.ThemeSettings { GlassPreset = preset.Id });
        return palette.Colors["BorderColor"];
    }

    private static FrameworkElement SliderRow(string label, Slider slider)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 76, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(slider);
        return row;
    }

    // ---- behaviour ----

    /// <summary>Shows one tab. The tab buttons follow.</summary>
    internal void SelectTab(ThemeTab tab)
    {
        _active = tab;
        foreach (var (t, view) in _tabs) view.Root.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        if (_tabButtons.TryGetValue(tab, out var button) && button.IsChecked != true) button.IsChecked = true;
    }

    /// <summary>Takes the state of a tab back into the controls: the ring on the chosen swatch, the hex, the preview, the sliders.</summary>
    private void Refresh(ThemeTab tab)
    {
        var v = _tabs[tab];
        string? selected = _state.SelectedPresetId(tab);
        foreach (var (id, toggle) in v.Swatches) toggle.IsChecked = id == selected;

        string hex = _state.CurrentHex(tab);
        SetHexText(v, hex);
        ShowPreview(v, hex);
        SyncSliders(tab, v, hex);
        v.Note.Visibility = Visibility.Collapsed;
    }

    /// <summary>Applies what the hex field says; a text that is no colour goes back to the colour in force, with a note.</summary>
    private void CommitHex(ThemeTab tab)
    {
        var v = _tabs[tab];
        v.Rest.Stop();
        bool ok = _state.ApplyHex(tab, v.Hex.Text, out string shown);
        Refresh(tab);
        v.Note.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        if (!ok) SetHexText(v, shown);
    }

    private static void SetHexText(TabView v, string hex)
    {
        bool was = v.Syncing;
        v.Syncing = true;
        try { if (v.Hex.Text != hex) v.Hex.Text = hex; }
        finally { v.Syncing = was; }
    }

    private static void ShowPreview(TabView v, string hex)
    {
        if (ThemePaletteBuilder.ParseColor(hex) is { } c) v.Preview.Fill = new SolidColorBrush(c);
    }

    private void SyncSliders(ThemeTab tab, TabView v, string hex)
    {
        var (hue, sat) = _state.HueSaturationOf(hex);
        bool was = v.Syncing;
        v.Syncing = true;
        try
        {
            v.Hue.Value = hue;
            v.Saturation.Value = sat;
        }
        finally { v.Syncing = was; }
    }

    /// <summary>Leaving the field applies what it says - unless the window itself is closing: that field loses the focus because the picker goes, and closing has already settled it.</summary>
    private void HexLostFocus(ThemeTab tab)
    {
        if (!_closing) CommitHex(tab);
    }

    /// <summary>
    /// Whatever the user chose and the picker has not yet applied (a slider that has not rested for 350 ms, a typed colour not yet
    /// confirmed) is applied now, as the picker closes - the colour they saw in the preview is not lost - and the timers are stopped,
    /// so nothing is applied after the picker is gone (over a game, say, when the board was closed by its hotkey).
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closing)
        {
            foreach (var (tab, v) in _tabs)
            {
                bool typed = ThemePaletteBuilder.NormalizeHex(v.Hex.Text) is { } hex && hex != _state.CurrentHex(tab);
                if (v.Rest.IsEnabled || typed) CommitHex(tab);   // stops the timer
            }
            _closing = true;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var v in _tabs.Values) v.Rest.Stop();
        base.OnClosed(e);
    }

    private bool HandleKey(Key key)
    {
        if (key != Key.Escape) return false;
        CloseRequested?.Invoke();
        return true;
    }

    // ---- placing ----

    /// <summary>Right of the anchor window (the sidebar), its bottom on the anchor's bottom, kept inside the monitor's work area.</summary>
    public void ShowNextTo(Window anchor, WinForms.Screen screen)
    {
        Place(anchor, screen);
        Show();
        UpdateLayout();
        Place(anchor, screen);   // again with the real size
    }

    private void Place(Window anchor, WinForms.Screen screen)
    {
        var area = DisplayGeometry.WorkArea(screen);
        double s = DisplayGeometry.ScaleOf(screen);
        double w = (ActualWidth > 0 ? ActualWidth : ContentWidth + 34) * s;
        double h = (ActualHeight > 0 ? ActualHeight : 480) * s;
        var a = DisplayGeometry.BoundsOf(anchor) ?? new LayoutRect(area.X, area.Y, 0, area.H);

        double x = Math.Min(a.Right + 12 * s, area.Right - w - 12 * s);
        double y = Math.Min(a.Bottom - h, area.Bottom - h - 12 * s);
        DisplayGeometry.MoveTo(this, Math.Max(area.X, x), Math.Max(area.Y, y));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Not in Alt-Tab; it may take the keyboard when clicked into (the hex field), but never when it appears.
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLong(hwnd, User32.GWL_EXSTYLE, ex | User32.WS_EX_TOOLWINDOW);
    }

    // ---- for tests ----

    internal ThemeTab ActiveTab => _active;
    internal IReadOnlyList<string> SwatchNames(ThemeTab tab) => _tabs[tab].SwatchNames;
    internal IReadOnlyList<string> TabNames() => new[] { ThemeTab.Accent, ThemeTab.Glass }.Select(t => (string)_tabButtons[t].Content).ToList();
    internal string? SelectedSwatch(ThemeTab tab) => _tabs[tab].Swatches.FirstOrDefault(kv => kv.Value.IsChecked == true).Key;
    internal string HexText(ThemeTab tab) => _tabs[tab].Hex.Text;
    internal bool HexNoteShown(ThemeTab tab) => _tabs[tab].Note.Visibility == Visibility.Visible;

    internal void ClickSwatch(ThemeTab tab, string id) => _tabs[tab].Swatches[id].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    internal void ClickStandard(ThemeTab tab) => _tabs[tab].Standard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    internal void TypeHex(ThemeTab tab, string text)
    {
        _tabs[tab].Hex.Text = text;
        CommitHex(tab);
    }
    internal void PressEscape() => HandleKey(Key.Escape);

    internal void MoveSliders(ThemeTab tab, double hue, double saturation)
    {
        _tabs[tab].Hue.Value = hue;
        _tabs[tab].Saturation.Value = saturation;
    }
    internal bool RestPending(ThemeTab tab) => _tabs[tab].Rest.IsEnabled;
    internal void SetHexWithoutCommit(ThemeTab tab, string text) => _tabs[tab].Hex.Text = text;
    internal void LoseHexFocus(ThemeTab tab) => HexLostFocus(tab);
}
