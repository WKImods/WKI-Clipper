using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// Reads the <see cref="DisplayTopology"/> from Windows: EnumDisplayMonitors + GetMonitorInfo (HMONITOR, GDI name, rect,
/// primary), CCD QueryDisplayConfig + DisplayConfigGetDeviceInfo (device path, friendly name, connector, EDID ids;
/// joined by the GDI name) and DXGI (adapters and their outputs; joined by the HMONITOR). The default adapter is the
/// one <c>D3D11CreateDevice(null, Hardware)</c> picks - what ffmpeg's ddagrab does without a device argument, so its
/// <c>output_idx</c> counts the outputs of exactly that adapter.
/// <para>Everything runs under a per-monitor-v2 DPI context, so the rects are physical pixels on any calling thread.
/// Never throws: a part that fails stays null and is named in <see cref="DisplayTopology.Problem"/>. Creates a D3D11
/// device - callers go through <see cref="DisplayTopologyCache"/>.</para>
/// </summary>
public static class DisplayTopologyReader
{
    public static DisplayTopology Read()
    {
        IntPtr previous = DisplayConfig.SetThreadDpiAwarenessContext(DisplayConfig.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            return ReadCore();
        }
        catch (Exception ex)
        {
            return DisplayTopology.Empty($"topology read failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (previous != IntPtr.Zero) DisplayConfig.SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>
    /// The cheap change detector for the cache: HMONITOR, GDI name, rect and primary flag of every monitor
    /// (EnumDisplayMonitors), plus the monitor devices on its source (EnumDisplayDevices) - no DXGI, no D3D11, no CCD.
    /// A mode change, a new primary, a monitor that comes or goes all change it; so does a DisplayPort monitor that
    /// falls asleep or wakes up while its desktop stays (its device path is only readable while it is awake).
    /// Empty when the enumeration fails.
    /// </summary>
    public static string QuickKey()
    {
        IntPtr previous = DisplayConfig.SetThreadDpiAwarenessContext(DisplayConfig.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            var sb = new StringBuilder();
            foreach (var m in EnumerateMonitors(problems: null))
            {
                var r = m.Rect;
                sb.Append($"{(long)m.HMonitor:X}|{m.GdiName}|{r.Left},{r.Top},{r.Right},{r.Bottom}|{(m.IsPrimary ? "P" : "-")}|");
                for (uint i = 0; i < 8; i++)
                {
                    var device = new DisplayConfig.DISPLAY_DEVICE { cb = Marshal.SizeOf<DisplayConfig.DISPLAY_DEVICE>() };
                    if (!DisplayConfig.EnumDisplayDevices(m.GdiName, i, ref device, 0)) break;
                    sb.Append($"{device.DeviceID}#{device.StateFlags:X},");
                }
                sb.Append(';');
            }
            return sb.ToString();
        }
        catch
        {
            return "";
        }
        finally
        {
            if (previous != IntPtr.Zero) DisplayConfig.SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>Short connector text of a CCD <c>outputTechnology</c> for labels; null when it says nothing useful.</summary>
    internal static string? ConnectorText(uint outputTechnology) => outputTechnology switch
    {
        10 => "DP",                                // DISPLAYPORT_EXTERNAL
        5 => "HDMI",
        4 => "DVI",
        18 => "USB-C",                             // DISPLAYPORT_USB_TUNNEL
        6 or 11 or 13 or 0x80000000 => "intern",   // LVDS, DISPLAYPORT_EMBEDDED, UDI_EMBEDDED, INTERNAL
        _ => null,
    };

    private readonly record struct MonitorEntry(IntPtr HMonitor, string GdiName, User32.RECT Rect, bool IsPrimary);
    private readonly record struct OutputEntry(IntPtr HMonitor, int Adapter, int Output);
    private sealed record CcdTarget(string? DevicePath, string? FriendlyName, string? Connector, ushort? EdidMfg, ushort? EdidProduct);

    private static DisplayTopology ReadCore()
    {
        var problems = new List<string>();

        var monitors = EnumerateMonitors(problems);
        if (monitors.Count == 0) problems.Add("EnumDisplayMonitors found no monitor");

        var targets = ReadCcdTargets(problems);
        var (adapters, outputs) = ReadDxgi(problems);
        int defaultAdapter = ReadDefaultAdapterIndex(adapters, problems);

        var displays = new List<DisplayInfo>(monitors.Count);
        foreach (var m in monitors)
        {
            targets.TryGetValue(m.GdiName, out var t);
            if (t is null) problems.Add($"{m.GdiName}: no display-config path");
            var output = PickOutput(outputs, m.HMonitor, defaultAdapter);
            if (output is null) problems.Add($"{m.GdiName}: no DXGI output");

            displays.Add(new DisplayInfo(m.HMonitor, m.GdiName, t?.DevicePath, t?.FriendlyName,
                m.Rect.Left, m.Rect.Top, m.Rect.Width, m.Rect.Height, m.IsPrimary,
                output?.Adapter, output?.Output, t?.Connector, t?.EdidMfg, t?.EdidProduct));
        }

        return new DisplayTopology(adapters, displays, defaultAdapter, problems.Count == 0 ? null : string.Join("; ", problems));
    }

    private static List<MonitorEntry> EnumerateMonitors(List<string>? problems)
    {
        var handles = new List<IntPtr>();
        if (!User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref User32.RECT _, IntPtr _) =>
            {
                handles.Add(h);
                return true;
            }, IntPtr.Zero))
            problems?.Add("EnumDisplayMonitors failed");

        var monitors = new List<MonitorEntry>(handles.Count);
        foreach (var h in handles)
        {
            var info = new DisplayConfig.MONITORINFOEX { cbSize = Marshal.SizeOf<DisplayConfig.MONITORINFOEX>(), szDevice = "" };
            if (!DisplayConfig.GetMonitorInfo(h, ref info))
            {
                problems?.Add($"GetMonitorInfo failed for hmon=0x{(long)h:X}");   // gone between the two calls
                continue;
            }
            monitors.Add(new MonitorEntry(h, info.szDevice ?? "", info.rcMonitor, (info.dwFlags & DisplayConfig.MONITORINFOF_PRIMARY) != 0));
        }
        return monitors;
    }

    /// <summary>CCD names of every active path, keyed by the GDI name of its source.</summary>
    private static Dictionary<string, CcdTarget> ReadCcdTargets(List<string> problems)
    {
        var targets = new Dictionary<string, CcdTarget>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int rc = DisplayConfig.QueryActivePaths(out var paths);
            if (rc != DisplayConfig.ERROR_SUCCESS)
            {
                problems.Add($"QueryDisplayConfig failed ({rc})");
                return targets;
            }

            foreach (var path in paths)
            {
                var source = new DisplayConfig.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DisplayConfig.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)Marshal.SizeOf<DisplayConfig.DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                int rcSource = DisplayConfig.DisplayConfigGetDeviceInfo(ref source);
                string? gdi = rcSource == DisplayConfig.ERROR_SUCCESS ? NullIfEmpty(source.viewGdiDeviceName) : null;
                if (gdi is null)
                {
                    problems.Add($"display-config source {path.sourceInfo.id}: no GDI name ({rcSource})");
                    continue;
                }
                // Clone mode: one source drives several monitors; the first path names it.
                if (targets.ContainsKey(gdi)) continue;

                var target = new DisplayConfig.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = DisplayConfig.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)Marshal.SizeOf<DisplayConfig.DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                target.header.adapterId = path.targetInfo.adapterId;
                target.header.id = path.targetInfo.id;
                int rcTarget = DisplayConfig.DisplayConfigGetDeviceInfo(ref target);
                if (rcTarget != DisplayConfig.ERROR_SUCCESS)
                {
                    problems.Add($"{gdi}: no monitor name ({rcTarget})");
                    targets[gdi] = new CcdTarget(null, null, ConnectorText(path.targetInfo.outputTechnology), null, null);
                    continue;
                }

                string? devicePath = NullIfEmpty(target.monitorDevicePath);
                // Seen live: a sleeping DisplayPort monitor keeps its CCD path, name and EDID ids, but not its device path.
                if (devicePath is null) problems.Add($"{gdi}: no device path (monitor asleep or its device gone)");

                bool edidValid = (target.flags & DisplayConfig.TARGET_NAME_FLAG_EDID_IDS_VALID) != 0;
                targets[gdi] = new CcdTarget(
                    devicePath,
                    NullIfEmpty(target.monitorFriendlyDeviceName),
                    ConnectorText(target.outputTechnology),
                    edidValid ? target.edidManufactureId : null,
                    edidValid ? target.edidProductCodeId : null);
            }
        }
        catch (Exception ex)
        {
            problems.Add($"display config: {ex.GetType().Name}: {ex.Message}");
        }
        return targets;
    }

    private static (List<DisplayAdapterInfo> Adapters, List<OutputEntry> Outputs) ReadDxgi(List<string> problems)
    {
        var adapters = new List<DisplayAdapterInfo>();
        var outputs = new List<OutputEntry>();
        try
        {
            var hr = DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory);
            if (hr.Failure || factory is null)
            {
                problems.Add($"CreateDXGIFactory1 failed (0x{hr.Code:X8})");
                return (adapters, outputs);
            }

            using (factory)
            {
                for (uint a = 0; ; a++)
                {
                    if (factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Failure || adapter is null) break;
                    using (adapter)
                    {
                        var desc = adapter.Description1;
                        adapters.Add(new DisplayAdapterInfo((int)a, desc.Luid, (desc.Description ?? "").TrimEnd('\0'),
                                                            (desc.Flags & AdapterFlags.Software) != 0));
                        for (uint o = 0; ; o++)
                        {
                            if (adapter.EnumOutputs(o, out IDXGIOutput? output).Failure || output is null) break;
                            using (output)
                                outputs.Add(new OutputEntry(output.Description.Monitor, (int)a, (int)o));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            problems.Add($"DXGI: {ex.GetType().Name}: {ex.Message}");
        }
        return (adapters, outputs);
    }

    /// <summary>
    /// The DXGI index of the adapter <c>D3D11CreateDevice(null, Hardware)</c> uses, by LUID. When that cannot be told,
    /// adapter 0 - the documented default ("the first adapter enumerated by EnumAdapters") - with a problem note.
    /// </summary>
    private static int ReadDefaultAdapterIndex(List<DisplayAdapterInfo> adapters, List<string> problems)
    {
        long? luid = null;
        try
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, null, out ID3D11Device? device);
            if (hr.Failure || device is null)
            {
                problems.Add($"D3D11CreateDevice failed (0x{hr.Code:X8})");
            }
            else
            {
                using (device)
                using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
                {
                    var hrAdapter = dxgiDevice.GetAdapter(out IDXGIAdapter? adapter);
                    if (hrAdapter.Failure || adapter is null)
                        problems.Add($"IDXGIDevice.GetAdapter failed (0x{hrAdapter.Code:X8})");
                    else
                        using (adapter) luid = adapter.Description.Luid;
                }
            }
        }
        catch (Exception ex)
        {
            problems.Add($"default adapter: {ex.GetType().Name}: {ex.Message}");
        }

        if (luid is long l)
        {
            var match = adapters.FirstOrDefault(a => a.Luid == l);
            if (match is not null) return match.DxgiIndex;
            problems.Add($"default adapter luid {DisplayTopology.LuidText(l)} is not a DXGI adapter");
        }
        if (adapters.Count == 0) return -1;
        problems.Add("default adapter assumed to be adapter 0");
        return 0;
    }

    /// <summary>The output of the monitor on the default adapter, else on the first adapter that lists it.</summary>
    private static OutputEntry? PickOutput(List<OutputEntry> outputs, IntPtr hMonitor, int defaultAdapter)
    {
        OutputEntry? first = null;
        foreach (var o in outputs)
        {
            if (o.HMonitor != hMonitor) continue;
            if (o.Adapter == defaultAdapter) return o;
            first ??= o;
        }
        return first;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
