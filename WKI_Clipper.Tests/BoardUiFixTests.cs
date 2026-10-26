using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;
using SystemColors = System.Windows.SystemColors;
using TextBox = System.Windows.Controls.TextBox;
using ToolTip = System.Windows.Controls.ToolTip;
using Color = System.Windows.Media.Color;
using ListBox = System.Windows.Controls.ListBox;
using Size = System.Windows.Size;

namespace WKI_Clipper.Tests;

/// <summary>
/// Found in the live test of 0.17.0: a list that was white (no theme style for ListBox), a colour panel that let the widgets behind
/// shine through, a performance overlay over the sidebar's footer and Esc closing the board together with the colour picker.
/// Everything runs offscreen - windows are built and rendered to pictures, never shown.
/// </summary>
[Collection("WpfApplication")]   // only one System.Windows.Application per process
public sealed class BoardUiFixTests
{
    private static T OnSta<T>(Func<T> body) => WpfTestSupport.OnSta(() => { WpfTestSupport.EnsureTheme(); return body(); });

    private static Color SolidOf(Brush? brush) => ((SolidColorBrush)brush!).Color;

    private static ResourceDictionary LoadTheme()
        => (ResourceDictionary)Application.LoadComponent(new Uri("/WKI_Clipper;component/Views/Theme.xaml", UriKind.Relative));

    private static GameChoice Choice(string name, bool added = false)
        => new(name, new LaunchTarget(ShortcutKind.SteamApp, "1", null), null, added);

    /// <summary>Renders the element to pixels (Pbgra32) after laying it out at its natural size.</summary>
    private static (int W, int H, byte[] Pixels) Render(FrameworkElement root)
    {
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var pixels = new byte[w * h * 4];
        bmp.CopyPixels(pixels, w * 4, 0);
        return (w, h, pixels);
    }

    private static double RelativeLuminance(byte r, byte g, byte b)
    {
        static double Lin(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    // ---- 1. the library picker ----

    [Fact]
    public void The_game_list_of_the_shortcut_editor_is_drawn_in_the_board_colours()
    {
        var r = OnSta(() =>
        {
            var dialog = new GameChoiceDialog("Steam", new[] { Choice("Arma Reforger"), Choice("World of Tanks"), Choice("Old Game", added: true) }, new Window());
            try
            {
                var list = dialog.ChoiceList;
                var content = (FrameworkElement)dialog.Content;
                var picture = Render(content);
                var item = (ListBoxItem)list.Items[0];

                // the empty part of the list (below the three rows) is the list's own background
                var spot = list.TranslatePoint(new Point(list.ActualWidth - 40, list.ActualHeight - 30), content);
                int x = (int)spot.X, y = (int)spot.Y;
                int at = (y * picture.W + x) * 4;
                var seen = (R: picture.Pixels[at + 2], G: picture.Pixels[at + 1], B: picture.Pixels[at]);
                return (background: SolidOf(list.Background), text: SolidOf(item.Foreground), seen);
            }
            finally { dialog.Close(); }
        });

        var panel = ThemePaletteBuilder.Build(new ThemeSettings()).Colors["PanelColor"];
        Assert.Equal(panel, r.background);                                               // the panel colour of the theme ...
        Assert.NotEqual(Colors.White, r.background);
        Assert.NotEqual(SystemColors.WindowColor, r.background);                          // ... and not what an unstyled list gets
        Assert.True(ThemePaletteBuilder.Contrast(r.text, r.background) >= 4.5, "the text reads on the list");
        Assert.True(RelativeLuminance(r.seen.R, r.seen.G, r.seen.B) < 0.1, $"the list is dark where it is empty: #{r.seen.R:X2}{r.seen.G:X2}{r.seen.B:X2}");
    }

    [Fact]
    public void A_selected_row_of_the_list_gets_the_active_wash_and_text_stays_readable_on_it()
    {
        var r = OnSta(() =>
        {
            var dialog = new GameChoiceDialog("Epic", new[] { Choice("Fortnite"), Choice("Rocket League") }, new Window());
            try
            {
                var list = dialog.ChoiceList;
                var picture = Render((FrameworkElement)dialog.Content);
                var item = (ListBoxItem)list.Items[1];
                item.IsSelected = true;
                picture = Render((FrameworkElement)dialog.Content);
                var bd = (Border)item.Template.FindName("Bd", item);
                var wash = ((LinearGradientBrush)bd.Background).GradientStops.Select(s => s.Color).ToArray();
                return (wash, text: SolidOf(item.Foreground), selected: list.SelectedItems.Count);
            }
            finally { dialog.Close(); }
        });

        var accent = ThemePaletteBuilder.Build(new ThemeSettings()).GradientStops["ActiveBrush"];
        Assert.Equal(accent, r.wash);                       // ActiveBrush, like the sidebar rows and the tabs
        Assert.Equal(1, r.selected);
        // the wash is translucent over the panel: the text still reads on the worst of the two stops laid over the panel
        var panel = ThemePaletteBuilder.Build(new ThemeSettings()).Colors["PanelColor"];
        foreach (var stop in r.wash)
        {
            double a = stop.A / 255.0;
            var over = Color.FromRgb((byte)(stop.R * a + panel.R * (1 - a)), (byte)(stop.G * a + panel.G * (1 - a)), (byte)(stop.B * a + panel.B * (1 - a)));
            Assert.True(ThemePaletteBuilder.Contrast(r.text, over) >= 4.5, $"text on {over}");
        }
    }

    [Fact]
    public void The_list_follows_a_theme_switch()
    {
        var r = OnSta(() =>
        {
            var dict = LoadTheme();
            var list = new ListBox();
            list.Items.Add(new ListBoxItem { Content = "x" });
            var host = new Grid { Resources = dict };
            host.Children.Add(list);

            var before = SolidOf(list.Background);
            ThemeService.ApplyTo(dict, ThemePaletteBuilder.Build(new ThemeSettings { GlassPreset = "graphite" }));
            return (before, after: SolidOf(list.Background));
        });
        Assert.Equal(ThemePaletteBuilder.Build(new ThemeSettings()).Colors["PanelColor"], r.before);
        Assert.Equal(ThemePaletteBuilder.Build(new ThemeSettings { GlassPreset = "graphite" }).Colors["PanelColor"], r.after);
    }

    [Fact]
    public void Every_selection_control_the_board_uses_has_a_theme_style()
    {
        var unstyled = OnSta(() =>
        {
            var types = new[]
            {
                typeof(ListBox), typeof(ListBoxItem), typeof(ComboBox), typeof(ComboBoxItem), typeof(TextBox), typeof(CheckBox),
                typeof(System.Windows.Controls.RadioButton), typeof(Button), typeof(ToolTip), typeof(ContextMenu), typeof(MenuItem),
                typeof(Slider), typeof(System.Windows.Controls.Primitives.ScrollBar),
            };
            return types.Where(t => Application.Current.TryFindResource(t) is not Style).Select(t => t.Name).ToArray();
        });
        Assert.Empty(unstyled);
    }

    [Fact]
    public void No_view_builds_a_control_that_the_theme_does_not_style()
    {
        // Controls the default (white, system coloured) look would leak through. A new one needs a theme style first.
        // (The one PasswordBox of the streaming widget and the one Expander of the preflight widget are left out: both are coloured
        // at their use site - background, foreground and border from the palette - and are rebuilt with the widget on a theme switch.)
        string dir = FindViewsDir();
        var unthemed = new Regex(@"(new\s+|<)(ListView|ListViewItem|DataGrid|TreeView|TreeViewItem|TabControl|TabItem|GroupBox|DatePicker|Calendar|Menu|StatusBar|ToolBar|RichTextBox)\b");
        var found = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml"))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => unthemed.IsMatch(x.line) && !x.line.TrimStart().StartsWith("//") && !x.line.TrimStart().StartsWith("///"))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToArray();
        Assert.True(found.Length == 0, "controls without a theme style:\n" + string.Join("\n", found));
    }

    private static string FindViewsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "WKI_Clipper", "Views");
            if (File.Exists(Path.Combine(dir.FullName, "WKI_Clipper", "WKI_Clipper.csproj"))) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Views folder not found above " + AppContext.BaseDirectory);
    }

    // ---- 2. the colour panel ----

    [Fact]
    public void The_colour_panel_is_opaque_so_the_widgets_behind_do_not_shine_through()
    {
        var r = OnSta(() =>
        {
            var state = new ThemePickerState(new ThemeSettings(), _ => { }, () => { });
            var window = new ThemePickerWindow(state);
            try
            {
                var surface = (Border)window.Content;
                var picture = Render(surface);
                // inside the rounded surface, away from the corners and the edge: the panel itself, between the title and the tabs
                int x = picture.W / 2, y = 6;
                int at = (y * picture.W + x) * 4;
                return (background: surface.Background, alpha: picture.Pixels[at + 3], alphaLow: picture.Pixels[((picture.H - 6) * picture.W + picture.W / 2) * 4 + 3]);
            }
            finally { window.Close(); }
        });

        Assert.Equal(255, r.alpha);
        Assert.Equal(255, r.alphaLow);
        Assert.Equal(ThemePaletteBuilder.Build(new ThemeSettings()).Colors["SurfaceSolidColor"], SolidOf(r.background));   // the solid glass, not the see-through one
    }

    // ---- 3. the sidebar above the overlay ----

    [Fact]
    public void The_sidebar_and_the_bar_are_raised_above_the_overlay_and_the_picker_stays_on_top()
    {
        var all = BoardZOrder.RaisedAboveOverlay(sidebarShown: true, barShown: true, pickerShown: true);
        Assert.Equal(new[] { BoardLayer.Sidebar, BoardLayer.Bar, BoardLayer.Picker }, all);         // bottom to top

        // what is not on screen is not raised
        Assert.Equal(new[] { BoardLayer.Sidebar, BoardLayer.Bar }, BoardZOrder.RaisedAboveOverlay(true, true, false));
        Assert.Equal(new[] { BoardLayer.Sidebar }, BoardZOrder.RaisedAboveOverlay(true, false, false));
        Assert.Equal(new[] { BoardLayer.Bar }, BoardZOrder.RaisedAboveOverlay(false, true, false));
        Assert.Empty(BoardZOrder.RaisedAboveOverlay(false, false, false));
    }

    // ---- 4. Esc ----

    [Fact]
    public void Esc_closes_only_the_picker_while_it_is_open_and_the_board_the_next_time()
    {
        Assert.Equal(EscapeTarget.ColourPicker, BoardEscape.Target(pickerOpen: true));
        Assert.Equal(EscapeTarget.Board, BoardEscape.Target(pickerOpen: false));
    }

    [Fact]
    public void The_backdrop_hands_Esc_to_the_host_and_marks_the_key_handled()
    {
        var r = OnSta(() =>
        {
            var backdrop = new WidgetBackdropWindow();
            try
            {
                int escapes = 0, dismissals = 0;
                backdrop.EscapePressed += () => escapes++;
                backdrop.Dismissed += () => dismissals++;

                bool esc = backdrop.HandleKey(Key.Escape);
                bool other = backdrop.HandleKey(Key.A);
                return (esc, other, escapes, dismissals);
            }
            finally { backdrop.Close(); }
        });

        Assert.True(r.esc);                 // handled: it goes no further, the board stays open
        Assert.False(r.other);
        Assert.Equal((1, 0), (r.escapes, r.dismissals));   // the host decides (picker first); the backdrop does not close the board by itself
    }

    [Fact]
    public void Without_a_host_listener_Esc_still_dismisses_the_board_as_before()
    {
        var dismissals = OnSta(() =>
        {
            var backdrop = new WidgetBackdropWindow();
            try
            {
                int n = 0;
                backdrop.Dismissed += () => n++;
                backdrop.HandleKey(Key.Escape);
                return n;
            }
            finally { backdrop.Close(); }
        });
        Assert.Equal(1, dismissals);
    }
}
