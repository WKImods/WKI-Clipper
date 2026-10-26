using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace WKI_Clipper.Tests;

/// <summary>
/// Switching the colour theme while the app runs. The palette keys of Theme.xaml are swapped for new instances and everything
/// that references them dynamically follows. Everything here runs on a STA thread against a private copy of Theme.xaml
/// (the application's own resources are touched by a single test, which restores them) - no window is ever shown.
/// </summary>
[Collection("WpfApplication")]   // only one System.Windows.Application per process
public sealed class ThemeServiceTests
{
    private static Color C(string hex) => (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    private static ThemePalette Palette(string accent = "wki-orange", string glass = "violet")
        => ThemePaletteBuilder.Build(new ThemeSettings { AccentPreset = accent, GlassPreset = glass });

    private static T OnSta<T>(Func<T> body) => WpfTestSupport.OnSta(() => { WpfTestSupport.EnsureTheme(); return body(); });

    private static ResourceDictionary LoadTheme()
        => (ResourceDictionary)Application.LoadComponent(new Uri("/WKI_Clipper;component/Views/Theme.xaml", UriKind.Relative));

    private static Color SolidOf(object? brush) => ((SolidColorBrush)brush!).Color;
    private static Color[] StopsOf(object? brush) => ((LinearGradientBrush)brush!).GradientStops.Select(s => s.Color).ToArray();

    // ---- the shipped Theme.xaml is the default palette ----

    [Fact]
    public void Theme_xaml_defines_every_palette_key_with_the_default_values()
    {
        var p = Palette();
        var r = OnSta(() =>
        {
            var dict = LoadTheme();
            return (colors: ThemePaletteKeys.ColorKeys.ToDictionary(k => k, k => (Color)dict[k]),
                    solid: ThemePaletteKeys.SolidBrushes.ToDictionary(b => b.Brush, b => SolidOf(dict[b.Brush])),
                    gradients: ThemePaletteKeys.Gradients.ToDictionary(g => g.Key, g => StopsOf(dict[g.Key])),
                    offsets: ThemePaletteKeys.Gradients.ToDictionary(g => g.Key, g => ((LinearGradientBrush)dict[g.Key]).GradientStops.Select(s => s.Offset).ToArray()),
                    points: ThemePaletteKeys.Gradients.ToDictionary(g => g.Key, g => (((LinearGradientBrush)dict[g.Key]).StartPoint, ((LinearGradientBrush)dict[g.Key]).EndPoint)));
        });

        foreach (var key in ThemePaletteKeys.ColorKeys) Assert.Equal(p.Colors[key], r.colors[key]);
        foreach (var (brush, colorKey) in ThemePaletteKeys.SolidBrushes) Assert.Equal(p.Colors[colorKey], r.solid[brush]);
        foreach (var g in ThemePaletteKeys.Gradients)
        {
            Assert.Equal(p.GradientStops[g.Key], r.gradients[g.Key]);
            Assert.Equal(g.Offsets, r.offsets[g.Key]);
            Assert.Equal((g.Start, g.End), r.points[g.Key]);
        }
    }

    // ---- a switch reaches styled controls ----

    [Fact]
    public void Styled_button_picks_up_new_accent_after_apply()
    {
        var r = OnSta(() =>
        {
            var dict = LoadTheme();
            var button = new Button { Style = (Style)dict["AccentButton"] };
            var muted = new TextBlock { Style = (Style)dict["MutedStyle"] };
            var host = new Grid { Resources = dict };      // the private "application": the controls look their resources up here
            host.Children.Add(button);
            host.Children.Add(muted);

            var before = (accent: StopsOf(button.Background)[1], muted: SolidOf(muted.Foreground));
            ThemeService.ApplyTo(dict, Palette("epic-blue", "bordeaux"));
            var after = (accent: StopsOf(button.Background)[1], muted: SolidOf(muted.Foreground));
            return (before, after);
        });

        Assert.Equal(C("#FF7A2C"), r.before.accent);
        Assert.Equal(C("#8E86A3"), r.before.muted);
        Assert.Equal(C("#4C7DFF"), r.after.accent);                      // the primary button follows the accent ...
        Assert.Equal(Palette("epic-blue", "bordeaux").Colors["MutedColor"], r.after.muted);   // ... the muted text the glass
    }

    [Fact]
    public void Controls_inside_templates_and_triggers_follow_too()
    {
        // The slider thumb's halo and the check box are styled through templates; their brushes come from DynamicResource setters.
        var r = OnSta(() =>
        {
            var dict = LoadTheme();
            var thumb = new System.Windows.Controls.Primitives.Thumb { Style = (Style)dict["SliderThumb"] };
            var host = new Grid { Resources = dict };
            host.Children.Add(thumb);
            host.Measure(new Size(100, 100));
            host.Arrange(new Rect(0, 0, 100, 100));
            host.UpdateLayout();

            Color Halo() => SolidOf(((System.Windows.Shapes.Ellipse)thumb.Template.FindName("Outer", thumb)).Fill);
            var before = Halo();
            ThemeService.ApplyTo(dict, Palette("emerald"));
            return (before, after: Halo());
        });

        Assert.Equal(C("#33FF7A2C"), r.before);
        Assert.Equal(C("#3314C98B"), r.after);
    }

    // ---- brushes are replaced, never changed ----

    [Fact]
    public void Gradient_brushes_are_rebuilt_with_new_stops()
    {
        var r = OnSta(() =>
        {
            var dict = LoadTheme();
            var oldBar = (LinearGradientBrush)dict["BarBrush"];
            var oldAccent = (SolidColorBrush)dict["AccentBrush"];
            var palette = Palette("crimson", "night-blue");
            ThemeService.ApplyTo(dict, palette);

            var newBar = (LinearGradientBrush)dict["BarBrush"];
            return (sameBar: ReferenceEquals(oldBar, newBar), sameAccent: ReferenceEquals(oldAccent, dict["AccentBrush"]),
                    oldBarStops: StopsOf(oldBar), oldAccentColor: oldAccent.Color,
                    newBarStops: StopsOf(newBar), offsets: newBar.GradientStops.Select(s => s.Offset).ToArray(),
                    start: newBar.StartPoint, end: newBar.EndPoint, frozen: newBar.IsFrozen && ((Freezable)dict["AccentBrush"]).IsFrozen,
                    surface: StopsOf(dict["SurfaceBrush"]), palette);
        });

        Assert.False(r.sameBar);
        Assert.False(r.sameAccent);
        Assert.Equal(new[] { C("#C8461A"), C("#FF7A2C"), C("#FF9F4A") }, r.oldBarStops);       // the old instances are untouched
        Assert.Equal(C("#FF7A2C"), r.oldAccentColor);
        Assert.Equal(r.palette.GradientStops["BarBrush"], r.newBarStops);
        Assert.NotEqual(r.oldBarStops, r.newBarStops);
        Assert.Equal(new[] { 0.0, 0.6, 1.0 }, r.offsets);                                       // shape and direction stay
        Assert.Equal((new Point(0, 0), new Point(1, 0)), (r.start, r.end));
        Assert.True(r.frozen);
        Assert.Equal(r.palette.GradientStops["SurfaceBrush"], r.surface);
    }

    [Fact]
    public void Static_non_palette_keys_unchanged()
    {
        var r = OnSta(() =>
        {
            var dict = LoadTheme();
            string[] keys = { "TextColor", "DangerColor", "SuccessColor", "TextBrush", "DangerBrush", "SuccessBrush", "LineBrush",
                              "SoftFillBrush", "SoftFillHoverBrush", "UiFont", "AccentButton", "MutedStyle", "HeadingStyle" };
            var before = keys.ToDictionary(k => k, k => dict[k]);
            ThemeService.ApplyTo(dict, Palette("magenta", "forest"));
            return keys.Where(k => !ReferenceEquals(before[k], dict[k]) && !before[k].Equals(dict[k])).ToArray();
        });
        Assert.Empty(r);
    }

    [Fact]
    public void Applying_the_default_palette_changes_no_value()
    {
        var r = OnSta(() =>
        {
            var dict = LoadTheme();
            var before = ThemePaletteKeys.All.ToDictionary(k => k, k => Describe(dict[k]));
            ThemeService.ApplyTo(dict, Palette());
            return ThemePaletteKeys.All.Where(k => before[k] != Describe(dict[k])).ToArray();
        });
        Assert.Empty(r);   // pixel-identical: replacing the entries with the default palette is a no-op for the eye
    }

    private static string Describe(object value) => value switch
    {
        Color c => c.ToString(),
        SolidColorBrush s => "S" + s.Color,
        LinearGradientBrush g => "G" + g.StartPoint + g.EndPoint + string.Join(";", g.GradientStops.Select(x => x.Color + "@" + x.Offset)),
        _ => value.ToString() ?? "",
    };

    [Fact]
    public void Windows_that_are_not_rebuilt_follow_through_resource_references()
    {
        var r = OnSta(() =>
        {
            var dict = LoadTheme();
            var host = new Grid { Resources = dict };

            // the clip editor's timeline paints with palette brushes taken by reference
            var timeline = new EditorTimeline();
            host.Children.Add(timeline);
            // the drag outline of the crosshair frame is the same idea; a web widget's status text, too
            var frame = new Border();
            frame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            host.Children.Add(frame);
            var status = new TextBlock();
            status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            host.Children.Add(status);

            Color Of(Brush? b) => ((SolidColorBrush)b!).Color;
            var before = (accent: Of(timeline.PaintBrushes.Accent), track: Of(timeline.PaintBrushes.Track), tick: Of(timeline.PaintBrushes.Tick),
                          frame: Of(frame.BorderBrush), status: Of(status.Foreground));
            ThemeService.ApplyTo(dict, Palette("ice-blue", "forest"));
            var after = (accent: Of(timeline.PaintBrushes.Accent), track: Of(timeline.PaintBrushes.Track), tick: Of(timeline.PaintBrushes.Tick),
                         frame: Of(frame.BorderBrush), status: Of(status.Foreground));
            return (before, after);
        });

        var p = Palette("ice-blue", "forest");
        Assert.Equal(C("#FF7A2C"), r.before.accent);
        Assert.Equal(p.Colors["AccentColor"], r.after.accent);
        Assert.Equal(p.Colors["PanelColor"], r.after.track);
        Assert.Equal(p.Colors["MutedColor"], r.after.tick);
        Assert.Equal(p.Colors["AccentColor"], r.after.frame);
        Assert.Equal(p.Colors["MutedColor"], r.after.status);
    }

    [Fact]
    public void Every_xaml_file_that_changed_to_dynamic_references_still_loads()
    {
        // The views and windows whose XAML had palette keys changed from static to dynamic; a typo or a property that cannot
        // take a dynamic value would only show when the file is loaded.
        var failures = OnSta(() =>
        {
            var failed = new List<string>();
            void Load(string name, Func<object> make)
            {
                try { make(); }
                catch (Exception e) { failed.Add(name + ": " + (e.InnerException ?? e).Message); }
            }
            Load("ChatView", () => new ChatView());
            Load("CrosshairView", () => new CrosshairView());
            Load("MusicView", () => new MusicView());
            Load("PreflightView", () => new PreflightView());
            Load("StreamingView", () => new StreamingView());
            Load("ToastNotificationWindow", () => new ToastNotificationWindow());
            Load("WidgetWindow", () => new WidgetWindow(WidgetId.Capture, "Test", new Border()));
            return failed;
        });
        Assert.Empty(failures);
    }

    // ---- the application's own resources ----

    [Fact]
    public void Apply_swaps_the_application_theme_and_raises_Changed_and_can_be_undone()
    {
        var service = new ThemeService();
        int changed = 0;
        service.Changed += () => changed++;

        var r = OnSta(() =>
        {
            try
            {
                service.Apply(Palette("gold", "graphite"));
                var gold = SolidOf(Application.Current.FindResource("AccentBrush"));
                service.Apply(Palette("emerald", "black"));       // a second switch replaces the first overlay, it does not pile up
                var emerald = SolidOf(Application.Current.FindResource("AccentBrush"));
                var layers = Application.Current.Resources.MergedDictionaries.Count;
                return (gold, emerald, layers);
            }
            finally
            {
                service.Apply(Palette());                          // leave the process' theme as it was
            }
        });

        Assert.Equal(C("#F5B700"), r.gold);
        Assert.Equal(C("#14C98B"), r.emerald);
        Assert.Equal(3, changed);                                    // two switches and the restore
        Assert.Equal(2, r.layers);                                  // Theme.xaml + the one overlay
        Assert.Equal(C("#FF7A2C"), OnSta(() => SolidOf(Application.Current.FindResource("AccentBrush"))));
        Assert.NotNull(service.Current);
    }

    // ---- nothing may keep a palette key static ----

    [Fact]
    public void No_palette_key_left_as_StaticResource()
    {
        string root = FindProjectDir();
        var keys = string.Join("|", ThemePaletteKeys.All);
        var reference = new Regex(@"\{StaticResource\s+(" + keys + @")\s*\}");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                              && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            string text = File.ReadAllText(file);
            // Theme.xaml builds its brushes from its colours right at the top (Color="{StaticResource ...}"): those definitions are
            // what ThemeService replaces. Everything after the first style is a USE of a palette key and must be dynamic.
            int usesFrom = Path.GetFileName(file) == "Theme.xaml" ? text.IndexOf("<FontFamily", StringComparison.Ordinal) : 0;
            Assert.True(usesFrom >= 0, "Theme.xaml: the end of the definitions block moved");

            foreach (Match m in reference.Matches(text, usesFrom))
            {
                int line = text.Take(m.Index).Count(ch => ch == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line} {m.Value}");
            }
        }

        Assert.True(offenders.Count == 0, "palette keys that would not follow a theme switch:\n" + string.Join("\n", offenders));
    }

    private static string FindProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "WKI_Clipper", "WKI_Clipper.csproj");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "WKI_Clipper");
            dir = dir.Parent;
        }
        throw new InvalidOperationException("WKI_Clipper project folder not found above " + AppContext.BaseDirectory);
    }

    // ---- rebuilding the board (the decisions of WidgetHost.RebuildUi) ----

    [Fact]
    public void Theme_rebuild_keeps_geometry_and_pins()
    {
        var windows = new[]
        {
            new WidgetWindowInfo(WidgetId.Capture, IsWeb: false),
            new WidgetWindowInfo(WidgetId.Performance, IsWeb: false),
            new WidgetWindowInfo(WidgetId.WhatsApp, IsWeb: true),
            new WidgetWindowInfo(WidgetId.Spotify, IsWeb: true),
            new WidgetWindowInfo(WidgetId.Gallery, IsWeb: false),
        };

        var closed = WidgetRebuildPlan.For(windows, boardWasOpen: false);
        var open = WidgetRebuildPlan.For(windows, boardWasOpen: true);

        foreach (var plan in new[] { closed, open })
        {
            // every window - rebuilt or kept - saves where it is BEFORE anything is closed
            Assert.Equal(windows.Select(w => w.Id), plan.CaptureGeometry);
            // nothing is lost and nothing is handled twice
            Assert.Equal(windows.Select(w => w.Id).OrderBy(i => i), plan.Keep.Concat(plan.Rebuild).OrderBy(i => i));
            Assert.Empty(plan.Keep.Intersect(plan.Rebuild));
        }
        Assert.False(closed.ReopenBoard);    // board closed: the pinned widgets come back through RestorePinned, as they were
        Assert.True(open.ReopenBoard);       // board open: it opens again with every visible widget where it was
    }

    [Fact]
    public void Web_widgets_are_not_rebuilt_on_theme_change()
    {
        var plan = WidgetRebuildPlan.For(new[]
        {
            new WidgetWindowInfo(WidgetId.Capture, false),
            new WidgetWindowInfo(WidgetId.WhatsApp, true),
            new WidgetWindowInfo(WidgetId.Spotify, true),
        }, boardWasOpen: true);

        Assert.Equal(new[] { WidgetId.WhatsApp, WidgetId.Spotify }, plan.Keep);   // their page - and the WhatsApp login with it - stays
        Assert.Equal(new[] { WidgetId.Capture }, plan.Rebuild);
    }

    [Fact]
    public void An_empty_board_rebuilds_nothing()
    {
        var plan = WidgetRebuildPlan.For(Array.Empty<WidgetWindowInfo>(), boardWasOpen: false);
        Assert.Empty(plan.CaptureGeometry);
        Assert.Empty(plan.Keep);
        Assert.Empty(plan.Rebuild);
    }

}
