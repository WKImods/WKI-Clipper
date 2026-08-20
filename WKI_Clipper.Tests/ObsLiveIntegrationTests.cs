using System;
using System.Linq;
using System.Threading.Tasks;
using WKI_Clipper.Models;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// END-TO-END test against a real, running OBS instance (WebSocket server on
/// 127.0.0.1:4455). Only active when OBS_LIVE=1 is set, so normal CI/test runs
/// stay hermetic. Proves the full chain: connect → initial status pull → scene
/// catalog → SetScene request → CurrentProgramSceneChanged event → restore.
/// </summary>
public sealed class ObsLiveIntegrationTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("OBS_LIVE") == "1";

    [Fact]
    public async Task Connect_list_scenes_switch_and_receive_event()
    {
        if (!Enabled) return;

        var settings = new SettingsService();
        settings.Load();   // defaults: 127.0.0.1:4455, no password

        using var svc = new ObsWebSocketService(settings);

        var connected = new TaskCompletionSource();
        svc.StatusChanged += () => { if (svc.IsConnected) connected.TrySetResult(); };
        svc.Enable();
        await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(svc.IsConnected, "OBS at 127.0.0.1:4455 not reachable — is OBS running with the WebSocket server enabled?");

        // Initial status must be populated by the connect-time pull.
        Assert.False(string.IsNullOrEmpty(svc.Status.CurrentScene));

        var scenes = svc.ListScenes();
        Assert.NotEmpty(scenes);

        var original = svc.Status.CurrentScene!;
        var target = scenes.FirstOrDefault(s => !string.Equals(s, original, StringComparison.OrdinalIgnoreCase));
        Assert.False(target is null, "OBS needs at least two scenes for this test.");

        // Switch via the exact code path a button/hotkey uses, then wait for the
        // change to come back as an EVENT (not by re-polling).
        var eventArrived = new TaskCompletionSource();
        svc.StatusChanged += () =>
        {
            if (string.Equals(svc.Status.CurrentScene, target, StringComparison.OrdinalIgnoreCase))
                eventArrived.TrySetResult();
        };
        await svc.ExecuteAsync(new StreamButtonConfig { Action = StreamAction.SetScene, Param = target });
        var done = await Task.WhenAny(eventArrived.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(done == eventArrived.Task, $"CurrentProgramSceneChanged event for '{target}' did not arrive.");

        // Inputs are part of the catalog path used by the config dialog.
        var inputs = svc.ListInputNames();
        Assert.NotEmpty(inputs);

        // --- mixer: fader roundtrip on a real audio input (no stream is ever started) ---
        var audioInputs = svc.ListAudioInputNames();
        Assert.NotEmpty(audioInputs);
        var mixTarget = audioInputs[0];
        float originalVol = svc.Status.InputVolume[mixTarget];
        float testVol = originalVol > 0.5f ? 0.25f : 0.75f;

        var volEvent = new TaskCompletionSource();
        svc.StatusChanged += () =>
        {
            if (svc.Status.InputVolume.TryGetValue(mixTarget, out float v) && Math.Abs(v - testVol) < 0.02f)
                volEvent.TrySetResult();
        };
        await svc.SetInputVolumeMulAsync(mixTarget, testVol);
        var volDone = await Task.WhenAny(volEvent.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(volDone == volEvent.Task, $"InputVolumeChanged for '{mixTarget}' did not arrive.");

        await svc.SetInputVolumeMulAsync(mixTarget, originalVol);   // restore
        await Task.Delay(400);

        // Restore the original scene — also event-based: the user's OBS uses a
        // stinger transition, so the scene change lands well after the request.
        var restored = new TaskCompletionSource();
        svc.StatusChanged += () =>
        {
            if (string.Equals(svc.Status.CurrentScene, original, StringComparison.OrdinalIgnoreCase))
                restored.TrySetResult();
        };
        await svc.ExecuteAsync(new StreamButtonConfig { Action = StreamAction.SetScene, Param = original });
        var restoredDone = await Task.WhenAny(restored.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.True(restoredDone == restored.Task, "restore scene change event did not arrive");
    }

    /// <summary>
    /// Sources widget path: list the current scene's items with their visibility, toggle
    /// one off and on again, and require BOTH changes to come back as events (that is what
    /// keeps the widget in sync when something is toggled in OBS itself). No stream runs.
    /// </summary>
    [Fact]
    public async Task Lists_scene_items_and_roundtrips_a_visibility_toggle()
    {
        if (!Enabled) return;

        var settings = new SettingsService();
        settings.Load();
        using var svc = new ObsWebSocketService(settings);

        var connected = new TaskCompletionSource();
        svc.StatusChanged += () => { if (svc.IsConnected) connected.TrySetResult(); };
        svc.Enable();
        await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(svc.IsConnected, "OBS at 127.0.0.1:4455 not reachable.");

        var scene = svc.Status.CurrentScene;
        Assert.False(string.IsNullOrEmpty(scene));

        var items = svc.ListSceneItemsDetailed(scene!);
        Assert.NotEmpty(items);
        var target = items[0];

        async Task<bool> ToggleAndAwaitEvent(bool enable)
        {
            var evt = new TaskCompletionSource();
            Action h = () => evt.TrySetResult();
            svc.SceneItemsChanged += h;
            try
            {
                await svc.SetSceneItemEnabledAsync(scene!, target.ItemId, enable);
                var done = await Task.WhenAny(evt.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                return done == evt.Task;
            }
            finally { svc.SceneItemsChanged -= h; }
        }

        try
        {
            Assert.True(await ToggleAndAwaitEvent(!target.Enabled),
                "SceneItemsChanged event did not arrive after the first toggle.");
            var after = svc.ListSceneItemsDetailed(scene!).First(i => i.ItemId == target.ItemId);
            Assert.Equal(!target.Enabled, after.Enabled);
        }
        finally
        {
            // Restore, also event-confirmed, so the user's scene is untouched afterwards.
            await ToggleAndAwaitEvent(target.Enabled);
        }

        var restored = svc.ListSceneItemsDetailed(scene!).First(i => i.ItemId == target.ItemId);
        Assert.Equal(target.Enabled, restored.Enabled);
    }

    /// <summary>
    /// The preflight's free-space check is only worth anything if it looks at the drive OBS
    /// actually writes to. That path comes from GetRecordDirectory on connect — this proves
    /// the request works and returns a usable, existing directory. No stream is started.
    /// </summary>
    [Fact]
    public async Task Reports_the_folder_obs_records_into()
    {
        if (!Enabled) return;

        var settings = new SettingsService();
        settings.Load();
        using var svc = new ObsWebSocketService(settings);

        var connected = new TaskCompletionSource();
        svc.StatusChanged += () => { if (svc.IsConnected) connected.TrySetResult(); };
        svc.Enable();
        await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(svc.IsConnected, "OBS at 127.0.0.1:4455 not reachable.");

        var dir = svc.Status.RecordDirectory;
        Assert.False(string.IsNullOrWhiteSpace(dir), "GetRecordDirectory returned nothing");
        Assert.True(System.IO.Directory.Exists(dir), $"OBS reports a recording folder that does not exist: {dir}");

        // The free-space lookup the preflight performs must succeed on that path.
        var root = System.IO.Path.GetPathRoot(dir);
        Assert.False(string.IsNullOrEmpty(root));
        var drive = new System.IO.DriveInfo(root!);
        Assert.True(drive.IsReady);
        Assert.True(drive.AvailableFreeSpace > 0);

        // Not streaming, so there must be no health reading pretending otherwise.
        Assert.False(svc.Status.Streaming);
        Assert.Null(svc.Status.Health);
    }
}
