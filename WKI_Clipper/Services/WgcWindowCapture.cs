using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// Occlusion-proof per-window capture via Windows.Graphics.Capture: the frames
/// keep showing the TARGET WINDOW even when another window covers it or it is
/// in exclusive fullscreen — "der Clip bleibt bei Arma, auch wenn Discord
/// drüberliegt". Frames arrive as D3D11 textures, are copied through a staging
/// texture and kept as one "latest frame" BGRA buffer that
/// <see cref="VideoPipeService"/> pumps to ffmpeg at a constant frame rate.
///
/// On window close or a content-size change (e.g. Workbench F5 → fullscreen)
/// <see cref="TargetInvalidated"/> fires ONCE — the consumer re-resolves and
/// restarts the pipeline (rawvideo needs a fixed WxH).
/// </summary>
public sealed class WgcWindowCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _staging;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _winrtDevice;

    private readonly object _frameLock = new();
    private readonly byte[] _latest;
    private bool _hasFrame;
    private bool _invalidated;
    private volatile bool _disposed;

    private readonly int _itemW;
    private readonly int _itemH;

    /// <summary>Frame width (cropped to even — required by yuv420 encoders).</summary>
    public int Width { get; }
    /// <summary>Frame height (cropped to even).</summary>
    public int Height { get; }
    public int FrameBytes => Width * Height * 4;

    /// <summary>Fired ONCE when the window closed or its content size changed.</summary>
    public event Action<string>? TargetInvalidated;

    public static bool IsSupported
    {
        get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
    }

    // Windows that turned out to deliver no frames (e.g. legacy exclusive
    // fullscreen). The resolver skips WGC for them so the pipeline falls back to
    // monitor capture instead of looping on a black feed. Session-scoped.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, byte> Blocked = new();

    public static bool IsBlocked(IntPtr hwnd) => Blocked.ContainsKey(hwnd);

    public WgcWindowCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd))
            throw new ArgumentException("Invalid window handle for WGC capture.");

        _item = WgcInterop.CreateItemForWindow(hwnd);
        _itemW = _item.Size.Width;
        _itemH = _item.Size.Height;
        Width = Math.Max(2, _itemW & ~1);
        Height = Math.Max(2, _itemH & ~1);
        _latest = new byte[FrameBytes];

        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device);
        if (result.Failure || device is null)
            throw new InvalidOperationException($"D3D11CreateDevice failed: {result.Code:X8}");
        _device = device;
        _context = _device.ImmediateContext;
        _winrtDevice = WgcInterop.CreateWinRtDevice(_device);

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_itemW,
            Height = (uint)_itemH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        });

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _framePool.FrameArrived += OnFrameArrived;

        _item.Closed += (_, _) => Invalidate("Fenster geschlossen");

        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        // No yellow capture border (Win11 / 10 21H2+). Older builds throw — capture stays functional.
        try { _session.IsBorderRequired = false; } catch { }
        _session.StartCapture();

        Logger.Info($"WGC capture started: '{_item.DisplayName}' {_itemW}×{_itemH} (frames {Width}×{Height})");

        // No-frame watchdog: if the window never produces a frame (legacy
        // exclusive fullscreen), block it for this session and invalidate so the
        // consumer falls back to monitor capture instead of recording black.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(5000).ConfigureAwait(false);
            if (_disposed) return;
            bool got;
            lock (_frameLock) { got = _hasFrame; }
            if (!got)
            {
                Blocked[hwnd] = 1;
                Invalidate("keine Frames — Fenster nicht capturebar, weiche auf Monitor aus");
            }
        });
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            var cs = frame.ContentSize;
            if (Math.Abs(cs.Width - _itemW) > 1 || Math.Abs(cs.Height - _itemH) > 1)
            {
                Invalidate($"Größe geändert ({_itemW}×{_itemH} → {cs.Width}×{cs.Height})");
                return;
            }

            using var tex = WgcInterop.GetTextureFromSurface(frame.Surface);
            _context.CopyResource(_staging, tex);
            var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowBytes = Width * 4;
                lock (_frameLock)
                {
                    for (int y = 0; y < Height; y++)
                        Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * (int)mapped.RowPitch),
                            _latest, y * rowBytes, rowBytes);
                    _hasFrame = true;
                }
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }
        catch (Exception ex)
        {
            if (!_disposed) Logger.Error("WGC frame processing failed", ex);
        }
    }

    /// <summary>Copies the newest frame into dest. False while no frame arrived yet.</summary>
    public bool TryCopyLatest(byte[] dest)
    {
        lock (_frameLock)
        {
            if (!_hasFrame) return false;
            Buffer.BlockCopy(_latest, 0, dest, 0, Math.Min(dest.Length, _latest.Length));
            return true;
        }
    }

    private void Invalidate(string reason)
    {
        if (_invalidated || _disposed) return;
        _invalidated = true;
        Logger.Info($"WGC target invalidated: {reason}");
        TargetInvalidated?.Invoke(reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _framePool.FrameArrived -= OnFrameArrived; } catch { }
        try { _session.Dispose(); } catch { }
        try { _framePool.Dispose(); } catch { }
        try { _winrtDevice.Dispose(); } catch { }
        try { _staging.Dispose(); } catch { }
        try { _context.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}

/// <summary>
/// The small COM/WinRT interop surface WGC needs: activating a
/// GraphicsCaptureItem from an HWND, wrapping a D3D11 device for WinRT, and
/// unwrapping a capture frame's surface back into a D3D11 texture.
/// </summary>
internal static class WgcInterop
{
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string src, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    // IID of Windows.Graphics.Capture.IGraphicsCaptureItem
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    internal static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var classId));
        try
        {
            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, ref interopIid, out var factoryPtr));
            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                var itemIid = GraphicsCaptureItemIid;
                var itemPtr = interop.CreateForWindow(hwnd, ref itemIid);
                try { return GraphicsCaptureItem.FromAbi(itemPtr); }
                finally { Marshal.Release(itemPtr); }
            }
            finally { Marshal.Release(factoryPtr); }
        }
        finally { WindowsDeleteString(classId); }
    }

    internal static IDirect3DDevice CreateWinRtDevice(ID3D11Device d3dDevice)
    {
        using var dxgi = d3dDevice.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }
    }

    internal static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        IntPtr unk = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(unk);
            var texIid = typeof(ID3D11Texture2D).GUID;
            IntPtr texPtr = access.GetInterface(ref texIid);
            return new ID3D11Texture2D(texPtr);
        }
        finally { Marshal.Release(unk); }
    }
}
