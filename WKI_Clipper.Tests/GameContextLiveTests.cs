using System;
using System.Diagnostics;
using System.Linq;
using WKI_Clipper.Services;
using Xunit;
using Xunit.Abstractions;

namespace WKI_Clipper.Tests;

/// <summary>
/// Runs the REAL game-folder resolution against every windowed process on this machine
/// and prints what each one would be filed under. Gated behind GAMEFOLDER_LIVE=1 — the
/// result depends on what is running. Read-only: only PROCESS_QUERY_LIMITED_INFORMATION
/// handles are opened, nothing is written.
/// </summary>
public sealed class GameContextLiveTests
{
    private readonly ITestOutputHelper _out;
    public GameContextLiveTests(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("GAMEFOLDER_LIVE") == "1";

    [Fact]
    public void Resolves_a_folder_for_every_windowed_process()
    {
        if (!Enabled) return;

        var ctx = new GameContext(() => null);
        var windowed = Process.GetProcesses()
            .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } })
            .ToList();
        Assert.NotEmpty(windowed);

        foreach (var p in windowed.OrderBy(p => p.ProcessName))
        {
            // A plan pinning this window drives the same path a WGC capture would.
            var plan = new CaptureTargetResolver.CapturePlan(
                0, "", 0, 0, default, null, p.ProcessName, p.MainWindowHandle, "", "", UseWgc: true);
            var folder = ctx.ResolveFolder(plan);

            _out.WriteLine($"{p.ProcessName,-34} -> {folder}");
            Assert.False(string.IsNullOrWhiteSpace(folder));
            Assert.Equal(folder, GameFolderNaming.Sanitize(folder));   // always path-safe
        }

        // (shell check below)
        AssertShellIsDesktop(ctx, windowed);
    }

    /// <summary>
    /// End to end through the REAL ScreenshotService: settings → game resolution from the
    /// actual foreground window → folder creation → ddagrab grab → file on disk. Writes only
    /// into a temp folder (in-memory settings change, nothing saved) and cleans up after.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task Screenshot_lands_in_the_foreground_apps_folder()
    {
        if (!Enabled) return;

        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wki_shot_e2e_" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService();
            settings.Load();
            settings.Current.Output.ScreenshotsFolder = temp;   // in memory only — never saved
            settings.Current.Output.SortByGame = true;

            var ctx = new GameContext(() => null);
            var svc = new ScreenshotService(settings) { GameFolderFor = ctx.ResolveFolder };

            var expected = ctx.ResolveFolder(null);
            var path = await svc.CaptureAsync();

            _out.WriteLine($"foreground folder: {expected}");
            _out.WriteLine($"saved to: {path}");
            Assert.NotNull(path);
            Assert.True(System.IO.File.Exists(path));
            Assert.Equal(System.IO.Path.Combine(temp, expected), System.IO.Path.GetDirectoryName(path));
        }
        finally
        {
            try { System.IO.Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void AssertShellIsDesktop(GameContext ctx, System.Collections.Generic.List<Process> windowed)
    {
        // The shell must never get a folder of its own.
        var explorer = windowed.FirstOrDefault(p => p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase));
        if (explorer != null)
        {
            var plan = new CaptureTargetResolver.CapturePlan(
                0, "", 0, 0, default, null, "explorer", explorer.MainWindowHandle, "", "", UseWgc: true);
            Assert.Equal(GameFolderNaming.DesktopFolder, ctx.ResolveFolder(plan));
        }
    }
}
