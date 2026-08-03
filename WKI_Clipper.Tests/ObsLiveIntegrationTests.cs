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
}
