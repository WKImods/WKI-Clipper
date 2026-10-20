using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using WKI_Clipper.Views;
using Xunit;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace WKI_Clipper.Tests;

/// <summary>
/// The shortcut bar's content and window on a real STA thread with the app theme. Nothing is ever shown: the view is measured,
/// arranged and rendered to a picture, the window is only constructed (no handle, no screen) - and no input is simulated.
/// </summary>
[Collection("WpfApplication")]   // only one System.Windows.Application per process
public sealed class ShortcutBarWindowTests
{
    private static BarItem Item(string id, ShortcutGroup group, string name, string? icon = null, bool missing = false)
        => new(id, group, name, icon, missing);

    private static readonly BarItem[] Everything =
    {
        Item("game.a", ShortcutGroup.Game, "Arma Reforger"),
        Item("game.b", ShortcutGroup.Game, "World of Tanks"),
        Item("launcher:steam", ShortcutGroup.Launcher, "Steam"),
        Item("launcher:epic", ShortcutGroup.Launcher, "Epic Games"),
        Item("launcher:xbox", ShortcutGroup.Launcher, "Xbox"),
        Item("browser:default", ShortcutGroup.Browser, "Standard-Browser"),
        Item("browser:chrome", ShortcutGroup.Browser, "Google Chrome"),
    };

    private static T OnSta<T>(Func<T> body)
        => WpfTestSupport.OnSta(() => { WpfTestSupport.EnsureTheme(); return body(); });

    private static double WidthOf(ShortcutBarView view) => view.NaturalWidthDip();

    // ---- what the bar draws ----

    [Fact]
    public void Collapsed_bar_renders_chip_only()
    {
        var r = OnSta(() =>
        {
            var view = new ShortcutBarView(expanded: false);
            view.SetItems(Everything);
            view.SetGame("Arma Reforger", icon: null);
            return (ids: view.VisibleItemIds, chip: view.ChipShown, expand: view.ExpandButtonShown, collapse: view.CollapseButtonShown,
                    edit: view.EditButtonShown, seps: view.GroupSeparatorCount, width: WidthOf(view));
        });

        Assert.Empty(r.ids);            // none of the shortcuts
        Assert.True(r.chip);            // the game chip ...
        Assert.True(r.expand);          // ... and the way to open the bar
        Assert.False(r.collapse);
        Assert.False(r.edit);           // the gear belongs to the open bar
        Assert.InRange(r.width, 60, 160);
    }

    [Fact]
    public void Expanded_bar_renders_groups_and_separators()
    {
        var r = OnSta(() =>
        {
            var view = new ShortcutBarView(expanded: true);
            view.SetItems(Everything);
            return (ids: view.VisibleItemIds, chip: view.ChipShown, expand: view.ExpandButtonShown, collapse: view.CollapseButtonShown,
                    edit: view.EditButtonShown, seps: view.GroupSeparatorCount, width: WidthOf(view));
        });

        Assert.Equal(Everything.Select(i => i.Id).ToArray(), r.ids);   // games, launchers, browsers in the given order
        Assert.Equal(2, r.seps);                                       // one between each two groups
        Assert.True(r.collapse);
        Assert.True(r.edit);
        Assert.False(r.chip);
        Assert.False(r.expand);
        Assert.True(r.width > 7 * 36);                                 // a real row of 32 px icons
    }

    [Fact]
    public void Empty_group_has_no_separator()
    {
        var r = OnSta(() =>
        {
            var view = new ShortcutBarView(expanded: true);
            // No launchers at all: games and browsers only.
            view.SetItems(Everything.Where(i => i.Group != ShortcutGroup.Launcher).ToArray());
            int gamesAndBrowsers = view.GroupSeparatorCount;

            view.SetItems(Everything.Where(i => i.Group == ShortcutGroup.Browser).ToArray());
            int onlyBrowsers = view.GroupSeparatorCount;

            view.SetItems(Array.Empty<BarItem>());
            return (gamesAndBrowsers, onlyBrowsers, nothing: view.GroupSeparatorCount, edit: view.EditButtonShown);
        });

        Assert.Equal(1, r.gamesAndBrowsers);   // not two: the empty group leaves no double separator behind
        Assert.Equal(0, r.onlyBrowsers);
        Assert.Equal(0, r.nothing);
        Assert.True(r.edit);                   // the gear is always reachable, even for an empty bar
    }

    [Fact]
    public void Collapsing_and_expanding_swaps_the_content_without_losing_the_items()
    {
        var r = OnSta(() =>
        {
            var view = new ShortcutBarView(expanded: true);
            view.SetItems(Everything);
            double open = WidthOf(view);

            view.SetExpanded(false);
            double closed = WidthOf(view);
            var idsClosed = view.VisibleItemIds;

            view.SetExpanded(true);
            return (open, closed, idsClosed, idsOpen: view.VisibleItemIds, expanded: view.Expanded);
        });

        Assert.True(r.closed < r.open);
        Assert.Empty(r.idsClosed);
        Assert.Equal(Everything.Length, r.idsOpen.Count);
        Assert.True(r.expanded);
    }

    [Fact]
    public void A_missing_entry_is_dimmed_and_says_so_in_its_tooltip()
    {
        var r = OnSta(() =>
        {
            var view = new ShortcutBarView(expanded: true);
            view.SetItems(new[]
            {
                Item("launcher:steam", ShortcutGroup.Launcher, "Steam"),
                Item("launcher:rockstar", ShortcutGroup.Launcher, "Rockstar Games", missing: true),
            });
            return (steam: view.ItemOpacity("launcher:steam"), rockstar: view.ItemOpacity("launcher:rockstar"),
                    tipSteam: view.ItemTooltip("launcher:steam"), tipRockstar: view.ItemTooltip("launcher:rockstar"));
        });

        Assert.Equal(1.0, r.steam);
        Assert.True(r.rockstar < 0.6);
        Assert.Equal("Steam", r.tipSteam);
        Assert.StartsWith("Rockstar Games", r.tipRockstar);
        Assert.True(r.tipRockstar!.Length > "Rockstar Games".Length);
    }

    [Fact]
    public void An_item_shows_its_icon_or_a_letter_tile_when_there_is_none()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wki_bar_icons_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = Path.Combine(dir, "a.png");
            WritePng(png, Colors.OrangeRed);
            var r = OnSta(() =>
            {
                var view = new ShortcutBarView(expanded: true);
                view.SetItems(new[]
                {
                    Item("game.a", ShortcutGroup.Game, "Arma", icon: png),
                    Item("game.b", ShortcutGroup.Game, "World of Tanks", icon: null),
                    Item("game.c", ShortcutGroup.Game, "Gone", icon: Path.Combine(dir, "missing.png")),   // a cache file that vanished
                });
                return (a: view.ItemShowsImage("game.a"), b: view.ItemShowsImage("game.b"), c: view.ItemShowsImage("game.c"),
                        letterB: view.ItemLetter("game.b"), letterC: view.ItemLetter("game.c"));
            });

            Assert.True(r.a);
            Assert.False(r.b);
            Assert.False(r.c);
            Assert.Equal("W", r.letterB);
            Assert.Equal("G", r.letterC);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- what a click does ----

    [Fact]
    public void Clicks_raise_the_matching_event_with_the_id()
    {
        var r = OnSta(() =>
        {
            var view = new ShortcutBarView(expanded: true);
            view.SetItems(Everything);
            var clicked = new System.Collections.Generic.List<string>();
            var toggled = new System.Collections.Generic.List<bool>();
            int edits = 0;
            view.ItemClicked += clicked.Add;
            view.ExpandedChanged += toggled.Add;
            view.EditClicked += () => edits++;

            view.ClickItem("launcher:epic");
            view.ClickItem("game.a");
            view.ClickEdit();
            view.ClickCollapse();
            view.SetExpanded(false);
            view.ClickExpand();
            return (clicked, toggled, edits);
        });

        Assert.Equal(new[] { "launcher:epic", "game.a" }, r.clicked.ToArray());
        Assert.Equal(1, r.edits);
        Assert.Equal(new[] { false, true }, r.toggled.ToArray());   // collapse, then expand again
    }

    [Fact]
    public void The_game_chip_opens_the_bar_too()
    {
        var toggled = OnSta(() =>
        {
            var view = new ShortcutBarView(expanded: false);
            var list = new System.Collections.Generic.List<bool>();
            view.ExpandedChanged += list.Add;
            view.ClickChip();
            return list;
        });

        Assert.Equal(new[] { true }, toggled.ToArray());
    }

    [Fact]
    public void Setting_the_state_from_outside_raises_nothing()
    {
        var raised = OnSta(() =>
        {
            var view = new ShortcutBarView(expanded: true);
            int n = 0;
            view.ExpandedChanged += _ => n++;
            view.SetExpanded(false);
            view.SetExpanded(true);
            return n;
        });

        Assert.Equal(0, raised);   // only a click is a user's decision
    }

    // ---- the picture ----

    [Fact]
    public void The_bar_lays_out_with_the_theme_and_renders_to_pictures()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wki_clipper_ui");
        Directory.CreateDirectory(dir);
        var iconDir = Path.Combine(Path.GetTempPath(), "wki_bar_render_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(iconDir);
        try
        {
            string Icon(string name, Color c) { var p = Path.Combine(iconDir, name + ".png"); WritePng(p, c); return p; }
            var items = new[]
            {
                Item("game.a", ShortcutGroup.Game, "Arma Reforger", Icon("a", Colors.DarkOliveGreen)),
                Item("game.b", ShortcutGroup.Game, "World of Tanks", Icon("b", Colors.SlateGray)),
                Item("game.c", ShortcutGroup.Game, "DayZ"),
                Item("launcher:steam", ShortcutGroup.Launcher, "Steam", Icon("s", Colors.SteelBlue)),
                Item("launcher:epic", ShortcutGroup.Launcher, "Epic Games", Icon("e", Colors.Black)),
                Item("launcher:rockstar", ShortcutGroup.Launcher, "Rockstar Games", Icon("r", Colors.Gold), missing: true),
                Item("browser:default", ShortcutGroup.Browser, "Standard-Browser"),
                Item("browser:chrome", ShortcutGroup.Browser, "Google Chrome", Icon("c", Colors.Crimson)),
            };

            var sizes = OnSta(() =>
            {
                var result = new System.Collections.Generic.List<Size>();
                foreach (var (expanded, file) in new[] { (true, "shortcut_bar_open.png"), (false, "shortcut_bar_collapsed.png") })
                {
                    var view = new ShortcutBarView(expanded);
                    view.SetItems(items);
                    view.SetGame("Arma Reforger", Icon("g", Colors.OrangeRed));
                    var host = new System.Windows.Controls.Border
                    {
                        Padding = new Thickness(12),
                        Background = (Brush)Application.Current.FindResource("BgBrush"),
                        Child = view,
                    };
                    host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    host.Arrange(new Rect(0, 0, host.DesiredSize.Width, host.DesiredSize.Height));
                    host.UpdateLayout();

                    var bmp = new RenderTargetBitmap((int)Math.Ceiling(host.DesiredSize.Width), (int)Math.Ceiling(host.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(host);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var f = File.Create(Path.Combine(dir, file))) enc.Save(f);
                    result.Add(new Size(view.ActualWidth, view.ActualHeight));
                }
                return result;
            });

            Assert.Equal(BarLayout.HeightDip, sizes[0].Height);   // 56 DIP high, open or not
            Assert.Equal(BarLayout.HeightDip, sizes[1].Height);
            Assert.True(sizes[0].Width > sizes[1].Width);
        }
        finally { try { Directory.Delete(iconDir, true); } catch { } }
    }

    // ---- the window ----

    [Fact]
    public void The_window_is_a_topmost_no_activate_overlay_that_is_never_shown_here()
    {
        var r = OnSta(() =>
        {
            using var env = new ServiceEnv();
            var w = new ShortcutBarWindow(env.Service, () => null);
            try
            {
                return (w.Topmost, w.ShowActivated, w.ShowInTaskbar, w.AllowsTransparency, w.IsVisible, style: w.WindowStyle, height: w.Height);
            }
            finally { w.Close(); }
        });

        Assert.True(r.Topmost);
        Assert.False(r.ShowActivated);      // like the widgets: showing the bar never takes the keyboard
        Assert.False(r.ShowInTaskbar);
        Assert.True(r.AllowsTransparency);
        Assert.False(r.IsVisible);
        Assert.Equal(WindowStyle.None, r.style);
        Assert.Equal(BarLayout.HeightDip, r.height);
    }

    [Fact]
    public void The_window_shows_what_the_service_holds_and_follows_it_from_any_thread()
    {
        var r = OnSta(() =>
        {
            using var env = new ServiceEnv();
            env.Service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), "Arma Reforger", null);
            var w = new ShortcutBarWindow(env.Service, () => null);
            try
            {
                w.Refresh();
                var before = w.View.VisibleItemIds;

                // The service raises ItemsChanged on whatever thread changed something (a scan, the game detector).
                System.Threading.Tasks.Task.Run(() => env.Service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "221100", null), "DayZ", null)).Wait();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));   // let the posted refresh run

                return (before, after: w.View.VisibleItemIds, expanded: w.View.Expanded);
            }
            finally { w.Close(); }
        });

        Assert.Equal(2, r.before.Count);                       // the game and the standard browser
        Assert.Equal(3, r.after.Count);
        Assert.True(r.expanded);
    }

    [Fact]
    public void Collapsing_in_the_bar_is_remembered_by_the_service()
    {
        var r = OnSta(() =>
        {
            using var env = new ServiceEnv();
            var w = new ShortcutBarWindow(env.Service, () => null);
            try
            {
                w.Refresh();
                w.View.ClickCollapse();
                return (service: env.Service.Expanded, view: w.View.Expanded, ids: w.View.VisibleItemIds);
            }
            finally { w.Close(); }
        });

        Assert.False(r.service);
        Assert.False(r.view);
        Assert.Empty(r.ids);
    }

    [Fact]
    public void A_click_on_an_item_launches_it_through_the_service()
    {
        var launched = OnSta(() =>
        {
            using var env = new ServiceEnv();
            env.Service.AddGame(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), "Arma Reforger", null);
            var w = new ShortcutBarWindow(env.Service, () => null);
            try
            {
                w.Refresh();
                w.View.ClickItem(env.Service.VisibleItems().First(i => i.Group == ShortcutGroup.Game).Id);
                return env.Shell.Started.ToArray();
            }
            finally { w.Close(); }
        });

        Assert.Equal(new LaunchTarget(ShortcutKind.SteamApp, "1874880", null), Assert.Single(launched));
    }

    [Fact]
    public void The_gear_asks_the_host_for_the_editor()
    {
        var edits = OnSta(() =>
        {
            using var env = new ServiceEnv();
            var w = new ShortcutBarWindow(env.Service, () => null);
            try
            {
                int n = 0;
                w.EditRequested += () => n++;
                w.Refresh();
                w.View.ClickEdit();
                return n;
            }
            finally { w.Close(); }
        });

        Assert.Equal(1, edits);
    }

    [Fact]
    public void The_game_chip_shows_the_game_in_front()
    {
        var r = OnSta(() =>
        {
            using var env = new ServiceEnv();
            var w = new ShortcutBarWindow(env.Service, () => new GameInfo(0, "ArmaReforgerSteam.exe", "Arma Reforger"));
            try
            {
                w.View.SetExpanded(false);
                w.Refresh();
                return w.View.ChipTooltip;
            }
            finally { w.Close(); }
        });

        Assert.Equal("Arma Reforger", r);
    }

    [Fact]
    public void Without_a_game_the_chip_is_the_wki_logo()
    {
        var r = OnSta(() =>
        {
            var view = new ShortcutBarView(expanded: false);
            view.SetGame(null, null);
            return (view.ChipShown, tooltip: view.ChipTooltip, showsGame: view.ChipShowsGame);
        });

        Assert.True(r.ChipShown);
        Assert.False(r.showsGame);
        Assert.Contains("WKI", r.tooltip);
    }

    // ---- helpers ----

    private static void WritePng(string path, Color color)
    {
        OnSta<object?>(() =>
        {
            var pixels = new byte[32 * 32 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
            var bmp = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var f = File.Create(path);
            enc.Save(f);
            return null;
        });
    }

    /// <summary>A real service on fakes: nothing scans the PC and nothing is started.</summary>
    private sealed class ServiceEnv : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_bar_service_" + Guid.NewGuid().ToString("N"));
        public FakeShell Shell { get; } = new();
        public ShortcutService Service { get; }

        public ServiceEnv()
        {
            Directory.CreateDirectory(_dir);
            var settings = new SettingsService(_dir);
            settings.Current.SchemaVersion = SettingsService.CurrentSchemaVersion;
            Service = new ShortcutService(settings, Shell, new FakeFileSystem(), new FakeRegistry(), () => { }, _ => { });
        }

        public void Dispose()
        {
            Service.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }
    }
}
