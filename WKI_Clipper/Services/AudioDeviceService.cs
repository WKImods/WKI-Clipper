using System.Collections.Generic;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;

namespace WKI_Clipper.Services;

public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceKind Kind);

public enum AudioDeviceKind { Capture, Render }

[SupportedOSPlatform("windows")]
public sealed class AudioDeviceService
{
    public IReadOnlyList<AudioDeviceInfo> ListMicrophones()
    {
        var result = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, AudioDeviceKind.Capture));
        }
        return result;
    }

    public IReadOnlyList<AudioDeviceInfo> ListRenderDevices()
    {
        var result = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, AudioDeviceKind.Render));
        }
        return result;
    }

    public AudioDeviceInfo? GetDefaultMicrophone()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return new AudioDeviceInfo(dev.ID, dev.FriendlyName, AudioDeviceKind.Capture);
        }
        catch { return null; }
    }

    public AudioDeviceInfo? GetDefaultRenderDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new AudioDeviceInfo(dev.ID, dev.FriendlyName, AudioDeviceKind.Render);
        }
        catch { return null; }
    }
}
