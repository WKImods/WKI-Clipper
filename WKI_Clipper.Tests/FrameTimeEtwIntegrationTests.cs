using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WKI_Clipper.Native;
using WKI_Clipper.Services;
using Xunit;
using Xunit.Abstractions;

namespace WKI_Clipper.Tests;

/// <summary>
/// Runs alone: these tests measure presents of the test process itself, and a WPF window from a
/// parallel test would be a competing present stream.
/// </summary>
[CollectionDefinition("ETW", DisableParallelization = true)]
public sealed class EtwCollection { }

/// <summary>
/// The REAL ETW session against presents made by this test process. Returns early (passes) when the
/// account may not start ETW sessions (needs admin or "Performance Log Users"). Each test uses its
/// own session name, so a running clipper's session is never touched.
/// </summary>
[Trait("Category", "Integration")]
[Collection("ETW")]
public sealed class FrameTimeEtwIntegrationTests
{
    private readonly ITestOutputHelper _out;
    public FrameTimeEtwIntegrationTests(ITestOutputHelper output) => _out = output;

    private static string TestSessionName() => "WKI-Clipper-FPS-Test-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>False when this account cannot start ETW sessions at all.</summary>
    internal static bool CanTrace()
    {
        string name = TestSessionName();
        uint rc = EtwPresentSession.StartBareSession(name, out _);
        if (rc == Etw.ERROR_SUCCESS) EtwPresentSession.StopOrphanedSession(name);
        return rc == Etw.ERROR_SUCCESS;
    }

    private static FrameTimeMonitor NewMonitor(string name, Action<EtwPresentSession>? created = null)
        => new(() =>
        {
            var s = new EtwPresentSession(name);
            created?.Invoke(s);
            return s;
        }, allowOwnProcess: true, clock: null, Stopwatch.Frequency, autoTick: false);

    /// <summary>Ticks until <paramref name="done"/> holds or the timeout passes; returns the last snapshot.</summary>
    private static FrameMetricsSnapshot WaitFor(FrameTimeMonitor m, Func<FrameMetricsSnapshot, bool> done, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            m.Tick();
            var s = m.Current;
            if (done(s) || sw.Elapsed > timeout) return s;
            Thread.Sleep(250);
        }
    }

    [Fact]
    public void Dxgi_presents_of_the_target_are_measured_and_test_presents_are_not()
    {
        if (!CanTrace()) { _out.WriteLine("No ETW permission — skipped."); return; }

        using var monitor = NewMonitor(TestSessionName());
        monitor.SetTargetProcess(Environment.ProcessId);
        using var lease = monitor.Subscribe();
        Assert.NotEqual(FrameMetricsStatus.NoPermission, monitor.Current.Status);
        Assert.NotEqual(FrameMetricsStatus.Unavailable, monitor.Current.Status);

        // 400 Hz: above any monitor refresh, so a stray WPF window could never be the dominant stream.
        using var presenter = new PacedDxgiPresenter(rateHz: 400, testPresentsPerFrame: 2);
        presenter.Start();
        var snap = WaitFor(monitor, s => s.Status == FrameMetricsStatus.Ok && s.Low1 is not null, TimeSpan.FromSeconds(8));
        double actual = presenter.ActualRate;
        presenter.Stop();

        _out.WriteLine($"status={snap.Status} api={snap.Api} fps={snap.Fps:0.0} (presenter {actual:0.0}/s) " +
                       $"avg={snap.AvgFrameTimeMs:0.00} ms low1={snap.Low1:0.0} low01={snap.Low01:0.0} stutter={snap.StutterPercent:0.00} %");
        Assert.Equal(FrameMetricsStatus.Ok, snap.Status);
        Assert.Equal(PresentApi.Dxgi, snap.Api);
        Assert.Equal(Environment.ProcessId, snap.ProcessId);
        // Two DXGI_PRESENT_TEST calls per frame: counting them would read ~3x the real rate.
        Assert.InRange(snap.Fps!.Value, actual * 0.85, actual * 1.15);
        Assert.NotNull(snap.Low1);
        Assert.True(snap.Low1 <= snap.Fps * 1.05);
    }

    /// <summary>
    /// What a Vulkan/OpenGL game looks like: no runtime events, only the kernel's. Switching the
    /// runtime providers off drives the fallback end to end. On this Windows build every present
    /// yields one PresentHistoryDetailed_Start and one Present_Info — counted once, not twice.
    /// </summary>
    [Fact]
    public void Without_runtime_events_the_kernel_fallback_measures_the_frame_rate()
    {
        if (!CanTrace()) { _out.WriteLine("No ETW permission — skipped."); return; }

        string name = TestSessionName();
        using var monitor = new FrameTimeMonitor(() => new EtwPresentSession(name, EtwPresentSession.Providers.Kernel),
            allowOwnProcess: true, clock: null, Stopwatch.Frequency, autoTick: false);
        monitor.SetTargetProcess(Environment.ProcessId);
        using var lease = monitor.Subscribe();

        using var presenter = new PacedDxgiPresenter(rateHz: 400, testPresentsPerFrame: 2);
        presenter.Start();
        var snap = WaitFor(monitor, s => s.Status == FrameMetricsStatus.Ok && s.Low1 is not null, TimeSpan.FromSeconds(8));
        double actual = presenter.ActualRate;
        presenter.Stop();

        _out.WriteLine($"status={snap.Status} api={snap.Api} fps={snap.Fps:0.0} (presenter {actual:0.0}/s) low1={snap.Low1:0.0}");
        Assert.Equal(FrameMetricsStatus.Ok, snap.Status);
        Assert.Equal(PresentApi.Other, snap.Api);
        Assert.InRange(snap.Fps!.Value, actual * 0.85, actual * 1.15);
    }

    [Fact]
    public void A_wpf_window_with_an_animation_is_measured_through_d3d9()
    {
        if (!CanTrace()) { _out.WriteLine("No ETW permission — skipped."); return; }
        if ((System.Windows.Media.RenderCapability.Tier >> 16) == 0) { _out.WriteLine("WPF renders in software — skipped."); return; }

        using var monitor = NewMonitor(TestSessionName());
        monitor.SetTargetProcess(Environment.ProcessId);
        using var lease = monitor.Subscribe();

        using var window = AnimatedWpfWindow.Show();
        var snap = WaitFor(monitor, s => s.Status == FrameMetricsStatus.Ok, TimeSpan.FromSeconds(8));

        _out.WriteLine($"status={snap.Status} api={snap.Api} fps={snap.Fps:0.0} avg={snap.AvgFrameTimeMs:0.00} ms");
        Assert.Equal(FrameMetricsStatus.Ok, snap.Status);
        Assert.Equal(PresentApi.D3D9, snap.Api);
        Assert.InRange(snap.Fps!.Value, 5, 1000);
    }

    [Fact]
    public void Another_process_is_not_counted()
    {
        if (!CanTrace()) { _out.WriteLine("No ETW permission — skipped."); return; }

        // A console process that never presents anything is the target; our own 400 Hz stream
        // must not show up under its pid.
        using var idle = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 8 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        try
        {
            using var monitor = NewMonitor(TestSessionName());
            monitor.SetTargetProcess(idle.Id);
            using var lease = monitor.Subscribe();
            using var presenter = new PacedDxgiPresenter(rateHz: 400, testPresentsPerFrame: 0);
            presenter.Start();
            Thread.Sleep(2500);
            monitor.Tick();
            presenter.Stop();

            Assert.Equal(FrameMetricsStatus.NoData, monitor.Current.Status);
            Assert.Equal(idle.Id, monitor.Current.ProcessId);
            Assert.Null(monitor.Current.Fps);
        }
        finally
        {
            try { idle.Kill(entireProcessTree: true); } catch { }
        }
    }

    [Fact]
    public void An_orphaned_session_with_the_same_name_is_stopped_and_replaced()
    {
        string name = TestSessionName();
        uint rc = EtwPresentSession.StartBareSession(name, out _);   // what a crashed clipper leaves behind
        if (rc == Etw.ERROR_ACCESS_DENIED) { _out.WriteLine("No ETW permission — skipped."); return; }
        Assert.Equal(Etw.ERROR_SUCCESS, rc);
        try
        {
            Assert.True(EtwPresentSession.SessionExists(name));

            var session = new EtwPresentSession(name);
            Assert.Equal(PresentSessionStart.Started, session.Start(_ => { }, _ => { }));
            Assert.True(EtwPresentSession.SessionExists(name));
            Assert.True(session.QueryLostCount() >= 0);

            session.Dispose();
            Assert.False(EtwPresentSession.SessionExists(name), "Dispose left the session running");
        }
        finally
        {
            EtwPresentSession.StopOrphanedSession(name);
        }
    }

    [Fact]
    public void The_startup_cleanup_stops_an_orphan_and_is_quiet_otherwise()
    {
        string name = TestSessionName();
        if (EtwPresentSession.StartBareSession(name, out _) != Etw.ERROR_SUCCESS) { _out.WriteLine("No ETW permission — skipped."); return; }

        Assert.True(EtwPresentSession.StopOrphanedSession(name));
        Assert.False(EtwPresentSession.SessionExists(name));
        Assert.False(EtwPresentSession.StopOrphanedSession(name));
    }

    [Fact]
    public void Unsubscribing_stops_the_session()
    {
        if (!CanTrace()) { _out.WriteLine("No ETW permission — skipped."); return; }
        string name = TestSessionName();
        using var monitor = NewMonitor(name);

        Assert.False(EtwPresentSession.SessionExists(name));   // nothing before the first subscriber
        var lease = monitor.Subscribe();
        Assert.True(EtwPresentSession.SessionExists(name));
        lease.Dispose();
        Assert.False(EtwPresentSession.SessionExists(name));
    }

    /// <summary>
    /// CPU cost of the running session. Gated behind FPS_CPU_LIVE=1 (takes ~2 min). Each load runs
    /// once WITHOUT a session (control) and once with it, so the session's share is the difference:
    /// the consumer thread itself, the rest of the process, and the presenting thread's cost per
    /// present (= what the provider side adds inside a game).
    /// </summary>
    [Fact]
    public void Measure_cpu_cost_of_the_session()
    {
        if (Environment.GetEnvironmentVariable("FPS_CPU_LIVE") != "1") return;
        if (!CanTrace()) { _out.WriteLine("No ETW permission — skipped."); return; }

        foreach (var (label, rate, seconds) in new[] { ("uncapped busy present loop", 0, 30), ("game-like 240 fps", 240, 20) })
        {
            using (var control = new PacedDxgiPresenter(rate, 0))
            {
                control.Start();
                Thread.Sleep(1000);
                Measure($"{label}, NO session (control)", null, null, control, seconds);
                control.Stop();
            }

            EtwPresentSession? session = null;
            using var monitor = new FrameTimeMonitor(() => session = new EtwPresentSession(TestSessionName()),
                allowOwnProcess: true, clock: null, Stopwatch.Frequency, autoTick: true);
            monitor.SetTargetProcess(Environment.ProcessId);
            using var lease = monitor.Subscribe();
            using var loaded = new PacedDxgiPresenter(rate, 0);
            loaded.Start();
            Thread.Sleep(1500);
            Measure($"{label}, WITH session", monitor, session, loaded, seconds);
            loaded.Stop();
        }

        EtwPresentSession? idleSession = null;
        using var idleMonitor = new FrameTimeMonitor(() => idleSession = new EtwPresentSession(TestSessionName()),
            allowOwnProcess: true, clock: null, Stopwatch.Frequency, autoTick: true);
        idleMonitor.SetTargetProcess(Environment.ProcessId);
        using var idleLease = idleMonitor.Subscribe();
        Thread.Sleep(1000);
        Measure("session running, target not presenting (other apps present as usual)", idleMonitor, idleSession, null, 10);
    }

    private void Measure(string label, FrameTimeMonitor? monitor, EtwPresentSession? session, PacedDxgiPresenter? presenter, int seconds)
    {
        var proc = Process.GetCurrentProcess();
        TimeSpan CpuOf(int id)
        {
            if (id == 0) return TimeSpan.Zero;
            proc.Refresh();
            foreach (ProcessThread t in proc.Threads) if (t.Id == id) return t.TotalProcessorTime;
            return TimeSpan.Zero;
        }

        int consumerId = session?.ConsumerThreadId ?? 0;
        int presenterId = presenter?.NativeThreadId ?? 0;
        proc.Refresh();
        var p0 = proc.TotalProcessorTime;
        var c0 = CpuOf(consumerId);
        var s0 = CpuOf(presenterId);
        long n0 = presenter?.Presents ?? 0;
        var sw = Stopwatch.StartNew();

        Thread.Sleep(TimeSpan.FromSeconds(seconds));

        sw.Stop();
        proc.Refresh();
        var p1 = proc.TotalProcessorTime;
        var c1 = CpuOf(consumerId);
        var s1 = CpuOf(presenterId);
        long n1 = presenter?.Presents ?? 0;
        double wall = sw.Elapsed.TotalMilliseconds;

        double consumerPct = (c1 - c0).TotalMilliseconds / wall * 100;
        double restPct = ((p1 - p0) - (s1 - s0)).TotalMilliseconds / wall * 100;
        double rate = (n1 - n0) / sw.Elapsed.TotalSeconds;
        double usPerPresent = n1 > n0 ? (s1 - s0).TotalMilliseconds * 1000 / (n1 - n0) : 0;
        var snap = monitor?.Current;
        _out.WriteLine($"[{label}] {seconds} s: presents {rate:0}/s" +
                       (snap is null ? "" : $", measured {snap.Status} {snap.Fps:0} fps") +
                       $" | consumer thread {consumerPct:0.000} % of one core | process minus presenter {restPct:0.000} % of one core" +
                       $" | presenter {usPerPresent:0.00} us/present");
    }
}

/// <summary>
/// A D3D11 composition swap chain presenting on its own thread — real DXGI Present_Start events
/// from this process without showing a window. rateHz 0 = as fast as possible.
/// </summary>
internal sealed class PacedDxgiPresenter : IDisposable
{
    private readonly int _rateHz;
    private readonly int _testPresentsPerFrame;
    private readonly ID3D11Device _device;
    private readonly IDXGISwapChain1 _swapChain;
    private Thread? _thread;
    private volatile bool _stop;
    private long _presents;
    private long _startTicks, _endTicks;

    public PacedDxgiPresenter(int rateHz, int testPresentsPerFrame)
    {
        _rateHz = rateHz;
        _testPresentsPerFrame = testPresentsPerFrame;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device).CheckError();
        _device = device!;
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        _swapChain = factory.CreateSwapChainForComposition(_device, new SwapChainDescription1
        {
            Width = 64,
            Height = 64,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipSequential,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Premultiplied,
        });
    }

    public long Presents => Interlocked.Read(ref _presents);
    public int NativeThreadId { get; private set; }

    /// <summary>Real presents per second since <see cref="Start"/>.</summary>
    public double ActualRate
    {
        get
        {
            long end = _endTicks != 0 ? _endTicks : Stopwatch.GetTimestamp();
            return Presents / ((end - _startTicks) / (double)Stopwatch.Frequency);
        }
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "test presenter" };
        _thread.Start();
        while (NativeThreadId == 0) Thread.Sleep(1);
    }

    private void Loop()
    {
        NativeThreadId = (int)Etw.GetCurrentThreadId();
        long interval = _rateHz > 0 ? Stopwatch.Frequency / _rateHz : 0;
        _startTicks = Stopwatch.GetTimestamp();
        long next = _startTicks;
        while (!_stop)
        {
            if (interval > 0)
            {
                while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(20);
                next += interval;
            }
            _swapChain.Present(0, PresentFlags.None);
            Interlocked.Increment(ref _presents);
            for (int i = 0; i < _testPresentsPerFrame; i++) _swapChain.Present(0, PresentFlags.Test);
        }
        _endTicks = Stopwatch.GetTimestamp();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join();
    }

    public void Dispose()
    {
        Stop();
        _swapChain.Dispose();
        _device.Dispose();
    }
}

/// <summary>A small always-animating WPF window on its own STA thread (WPF presents through D3D9).</summary>
internal sealed class AnimatedWpfWindow : IDisposable
{
    private System.Windows.Threading.Dispatcher? _dispatcher;
    private System.Windows.Window? _window;
    private Thread? _thread;

    public static AnimatedWpfWindow Show()
    {
        var w = new AnimatedWpfWindow();
        using var ready = new ManualResetEventSlim();
        w._thread = new Thread(() =>
        {
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = 80,
                Height = 80,
                Fill = System.Windows.Media.Brushes.OrangeRed,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            };
            var rotate = new System.Windows.Media.RotateTransform();
            rect.RenderTransform = rotate;
            w._window = new System.Windows.Window
            {
                Title = "WKI FPS test",
                Width = 160,
                Height = 160,
                Left = 0,
                Top = 0,
                WindowStyle = System.Windows.WindowStyle.ToolWindow,
                ShowActivated = false,
                ShowInTaskbar = false,
                Content = rect,
            };
            w._window.Show();
            rotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                });
            w._dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        });
        w._thread.SetApartmentState(ApartmentState.STA);
        w._thread.IsBackground = true;
        w._thread.Start();
        ready.Wait(TimeSpan.FromSeconds(10));
        return w;
    }

    public void Dispose()
    {
        try
        {
            _dispatcher?.Invoke(() => _window?.Close());
            _dispatcher?.InvokeShutdown();
            _thread?.Join(TimeSpan.FromSeconds(5));
        }
        catch { }
    }
}
