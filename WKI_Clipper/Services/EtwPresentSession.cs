using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// The real-time ETW session behind <see cref="FrameTimeMonitor"/> — the same mechanism AMD
/// Adrenalin's bundled PresentMon and the Xbox Game Bar use. Passive: Windows' own graphics
/// runtimes log their Present calls; we only consume that log. No injection, no hooks, no handle
/// to any process, no driver. Rights: admin or "Performance Log Users" (S-1-5-32-559); an ordinary
/// real-time session, not the NT Kernel Logger, so SeSystemProfilePrivilege is not involved.
///
/// Providers (enabled with an event-id filter so only the present events are ever emitted to us):
/// <list type="bullet">
/// <item>Microsoft-Windows-DXGI: Present_Start (42), PresentMultiplaneOverlay_Start (55)</item>
/// <item>Microsoft-Windows-D3D9: Present_Start (1)</item>
/// <item>Microsoft-Windows-DxgKrnl: PresentHistory_Start (0xAB), Present_Info (0xB8),
///   PresentHistoryDetailed_Start (0xD7) — only as the Vulkan/OpenGL fallback (see PresentTracker).
///   Measured on Windows 11 (Oct 2026): every present, whatever the API, yields one 0xD7 and one 0xB8
///   from the presenting process (0xAB was not seen) — so D3D11/12 would triple count without the
///   tracker's runtime-over-kernel rule.</item>
/// </list>
/// Lifecycle: a same-named orphan (crashed clipper) is stopped before starting; ProcessTrace runs on
/// its own background thread; the native callback delegate is rooted in a field; Dispose stops the
/// session, closes the consumer and joins the thread.
/// </summary>
internal sealed class EtwPresentSession : IPresentSession
{
    /// <summary>Which providers to enable. Production always uses <see cref="All"/>; tests switch the
    /// runtimes off to drive the kernel fallback end to end (what a Vulkan/OpenGL game looks like).</summary>
    [Flags]
    internal enum Providers { Runtime = 1, Kernel = 2, All = Runtime | Kernel }

    public const string SessionNamePrefix = "WKI-Clipper-FPS-";

    private const uint BufferKb = 64;
    private const uint MinBuffers = 4;
    private const uint MaxBuffers = 32;
    private const uint FlushTimerSeconds = 1;
    private const int UserDataCopyBytes = 32;   // enough for every payload field we read

    // Serializes start and orphan cleanup across the process: the startup cleanup must never stop
    // a session this process has just started.
    private static readonly object s_nameGate = new();
    private static int s_liveSessions;

    private readonly string _name;
    private readonly Providers _providers;
    private readonly object _gate = new();

    private ulong _sessionHandle;
    private ulong _consumerHandle = Etw.INVALID_PROCESSTRACE_HANDLE;
    private IntPtr _loggerName, _logfile;
    private Thread? _thread;
    private bool _started, _disposed;

    // Rooted for the lifetime of the session: ETW holds only a raw function pointer.
    private Etw.EventRecordCallback? _callback;
    private Action<PresentEvent>? _onPresent;
    private Action<long>? _onLost;

    // Consumer-thread scratch (ProcessTrace calls back on one thread only).
    private readonly byte[] _record = new byte[EtwRecordLayout.EventRecordSize];
    private readonly byte[] _userData = new byte[UserDataCopyBytes];
    private int _callbackErrors;

    private volatile int _targetPid;

    public EtwPresentSession(string sessionName) : this(sessionName, Providers.All) { }

    internal EtwPresentSession(string sessionName, Providers providers)
    {
        _name = sessionName;
        _providers = providers;
    }

    public string Name => _name;

    public int TargetProcessId
    {
        get => _targetPid;
        set => _targetPid = value;
    }

    /// <summary>Native id of the ProcessTrace thread (for CPU measurements); 0 until it runs.</summary>
    public int ConsumerThreadId { get; private set; }

    // ---------------- naming ----------------

    /// <summary>
    /// "WKI-Clipper-FPS-&lt;SID&gt;": per user, so two accounts on one PC never stop each other's
    /// session, and the uninstaller can find it by prefix. Falls back to the user name.
    /// </summary>
    internal static string SessionNameForUser(string? sid, string? fallbackUserName)
    {
        string raw = !string.IsNullOrWhiteSpace(sid) ? sid
                   : !string.IsNullOrWhiteSpace(fallbackUserName) ? fallbackUserName
                   : "user";
        var sb = new StringBuilder(SessionNamePrefix.Length + raw.Length);
        sb.Append(SessionNamePrefix);
        foreach (char c in raw)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_');
        return sb.Length > 200 ? sb.ToString(0, 200) : sb.ToString();
    }

    public static string SessionNameForCurrentUser()
    {
        string? sid = null;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            sid = id.User?.Value;
        }
        catch { /* fall back to the name */ }
        return SessionNameForUser(sid, Environment.UserName);
    }

    // ---------------- static session control ----------------

    /// <summary>
    /// Stops a leftover session of this user (a crashed clipper keeps the providers enabled until
    /// reboot). Skipped while this process runs its own. True if one was stopped.
    /// </summary>
    public static bool StopOrphanedSession(string? sessionName = null)
    {
        string name = sessionName ?? SessionNameForCurrentUser();
        lock (s_nameGate)
        {
            if (s_liveSessions > 0 && sessionName is null) return false;
            uint rc = StopByName(name);
            if (rc == Etw.ERROR_SUCCESS)
            {
                Logger.Info($"FPS: stopped orphaned ETW session '{name}'.");
                return true;
            }
            return false;
        }
    }

    internal static bool SessionExists(string name)
    {
        IntPtr props = Etw.AllocControlProperties();
        try { return Etw.ControlTrace(0, name, props, Etw.EVENT_TRACE_CONTROL_QUERY) == Etw.ERROR_SUCCESS; }
        finally { Marshal.FreeHGlobal(props); }
    }

    private static uint StopByName(string name)
    {
        IntPtr props = Etw.AllocControlProperties();
        try { return Etw.ControlTrace(0, name, props, Etw.EVENT_TRACE_CONTROL_STOP); }
        finally { Marshal.FreeHGlobal(props); }
    }

    /// <summary>StartTrace only (no providers, no consumer). Tests use it to fake an orphan.</summary>
    internal static uint StartBareSession(string name, out ulong handle)
    {
        IntPtr props = Etw.AllocSessionProperties(BufferKb, MinBuffers, MaxBuffers,
            Etw.EVENT_TRACE_REAL_TIME_MODE | Etw.EVENT_TRACE_NO_PER_PROCESSOR_BUFFERING, FlushTimerSeconds);
        try { return Etw.StartTrace(out handle, name, props); }
        finally { Marshal.FreeHGlobal(props); }
    }

    // ---------------- lifecycle ----------------

    public PresentSessionStart Start(Action<PresentEvent> onPresent, Action<long> onLost)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("Session already started.");
            _started = true;
            _onPresent = onPresent;
            _onLost = onLost;

            lock (s_nameGate)
            {
                uint rc = StartBareSession(_name, out _sessionHandle);
                if (rc == Etw.ERROR_ALREADY_EXISTS)
                {
                    uint stop = StopByName(_name);
                    Logger.Info($"FPS: orphaned ETW session '{_name}' found — stop returned {stop}, restarting.");
                    rc = StartBareSession(_name, out _sessionHandle);
                }
                if (rc != Etw.ERROR_SUCCESS)
                {
                    _sessionHandle = 0;
                    if (rc == Etw.ERROR_ACCESS_DENIED) return PresentSessionStart.AccessDenied;
                    Logger.Warn($"FPS: StartTrace failed ({rc}).");
                    return PresentSessionStart.Failed;
                }
                s_liveSessions++;
            }

            var result = EnableProvidersLocked();
            if (result != PresentSessionStart.Started)
            {
                StopLocked();
                return result;
            }

            result = OpenConsumerLocked();
            if (result != PresentSessionStart.Started)
            {
                StopLocked();
                return result;
            }

            Logger.Info($"FPS: ETW session '{_name}' running.");
            return PresentSessionStart.Started;
        }
    }

    private PresentSessionStart EnableProvidersLocked()
    {
        bool runtime = _providers.HasFlag(Providers.Runtime);
        uint dxgi = Etw.ERROR_SUCCESS, d3d9 = Etw.ERROR_SUCCESS, krnl = Etw.ERROR_SUCCESS;
        if (runtime)
        {
            dxgi = EnableProvider(PresentEventParser.DxgiProvider, PresentEventParser.DxgiEventsKeyword,
                new[] { PresentEventParser.DxgiPresentStart, PresentEventParser.DxgiPresentMpoStart }, allowUnfiltered: true);
            if (dxgi == Etw.ERROR_ACCESS_DENIED) return PresentSessionStart.AccessDenied;

            d3d9 = EnableProvider(PresentEventParser.D3D9Provider, PresentEventParser.D3D9EventsKeyword,
                new[] { PresentEventParser.D3D9PresentStart }, allowUnfiltered: true);
        }

        if (_providers.HasFlag(Providers.Kernel))
        {
            // The kernel provider is busy; never enable it without the event-id filter.
            krnl = EnableProvider(PresentEventParser.DxgKrnlProvider, PresentEventParser.DxgKrnlPresentKeyword,
                new[] { PresentEventParser.KernelPresentHistoryStart, PresentEventParser.KernelPresentInfo,
                        PresentEventParser.KernelPresentHistoryDetailedStart }, allowUnfiltered: false);
            if (krnl == Etw.ERROR_ACCESS_DENIED) return PresentSessionStart.AccessDenied;
            if (krnl != Etw.ERROR_SUCCESS)
                Logger.Warn($"FPS: DxgKrnl provider not enabled ({krnl}) — Vulkan/OpenGL games will show no FPS.");
            if (!runtime && krnl != Etw.ERROR_SUCCESS) return PresentSessionStart.Failed;
        }

        if (runtime && dxgi != Etw.ERROR_SUCCESS && d3d9 != Etw.ERROR_SUCCESS)
        {
            Logger.Warn($"FPS: neither DXGI ({dxgi}) nor D3D9 ({d3d9}) could be enabled.");
            return PresentSessionStart.Failed;
        }
        return PresentSessionStart.Started;
    }

    private uint EnableProvider(Guid provider, ulong keywords, ushort[] eventIds, bool allowUnfiltered)
    {
        // EVENT_FILTER_EVENT_ID: FilterIn (BOOLEAN), Reserved (UCHAR), Count (USHORT), Events[Count] (USHORT)
        int filterSize = 4 + 2 * eventIds.Length;
        IntPtr filter = Etw.AllocZeroed(filterSize);
        IntPtr desc = Etw.AllocZeroed(Marshal.SizeOf<Etw.EVENT_FILTER_DESCRIPTOR>());
        try
        {
            Marshal.WriteByte(filter, 0, 1);
            Marshal.WriteInt16(filter, 2, (short)eventIds.Length);
            for (int i = 0; i < eventIds.Length; i++) Marshal.WriteInt16(filter, 4 + 2 * i, unchecked((short)eventIds[i]));

            Marshal.StructureToPtr(new Etw.EVENT_FILTER_DESCRIPTOR
            {
                Ptr = (ulong)filter.ToInt64(),
                Size = (uint)filterSize,
                Type = Etw.EVENT_FILTER_TYPE_EVENT_ID,
            }, desc, false);

            var parameters = new Etw.ENABLE_TRACE_PARAMETERS
            {
                Version = Etw.ENABLE_TRACE_PARAMETERS_VERSION_2,
                EnableFilterDesc = desc,
                FilterDescCount = 1,
            };
            Guid id = provider;
            uint rc = Etw.EnableTraceEx2(_sessionHandle, ref id, Etw.EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                Etw.TRACE_LEVEL_INFORMATION, keywords, 0, 0, ref parameters);
            if (rc == Etw.ERROR_SUCCESS || rc == Etw.ERROR_ACCESS_DENIED) return rc;

            Logger.Warn($"FPS: event-id filter for {provider} rejected ({rc}).");
            if (!allowUnfiltered) return rc;
            return Etw.EnableTraceEx2(_sessionHandle, ref id, Etw.EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                Etw.TRACE_LEVEL_INFORMATION, keywords, 0, 0, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(desc);
            Marshal.FreeHGlobal(filter);
        }
    }

    private PresentSessionStart OpenConsumerLocked()
    {
        _callback = OnEventRecord;
        _loggerName = Marshal.StringToHGlobalUni(_name);
        _logfile = Etw.AllocRealTimeLogfile(_loggerName, Marshal.GetFunctionPointerForDelegate(_callback));

        ulong handle = Etw.OpenTrace(_logfile);
        if (handle == Etw.INVALID_PROCESSTRACE_HANDLE)
        {
            int err = Marshal.GetLastWin32Error();
            Logger.Warn($"FPS: OpenTrace failed ({err}).");
            return err == (int)Etw.ERROR_ACCESS_DENIED ? PresentSessionStart.AccessDenied : PresentSessionStart.Failed;
        }
        _consumerHandle = handle;

        long perfFreq = Etw.ReadPerfFreq(_logfile);
        if (perfFreq > 0 && perfFreq != System.Diagnostics.Stopwatch.Frequency)
            Logger.Warn($"FPS: ETW clock {perfFreq} Hz differs from QPC {System.Diagnostics.Stopwatch.Frequency} Hz.");

        var thread = new Thread(ConsumerLoop) { IsBackground = true, Name = "ETW frame times" };
        _thread = thread;
        thread.Start(handle);
        return PresentSessionStart.Started;
    }

    private void ConsumerLoop(object? state)
    {
        ConsumerThreadId = (int)Etw.GetCurrentThreadId();
        ulong handle = (ulong)state!;
        uint rc = Etw.ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero);
        if (rc != Etw.ERROR_SUCCESS && rc != Etw.ERROR_CANCELLED)
            Logger.Warn($"FPS: ProcessTrace ended with {rc}.");
        GC.KeepAlive(_callback);
    }

    /// <summary>Hot path, ETW consumer thread. Must never throw into native code.</summary>
    private void OnEventRecord(IntPtr record)
    {
        try
        {
            Marshal.Copy(record, _record, 0, EtwRecordLayout.EventRecordSize);
            var header = EtwRecordLayout.ReadHeader(_record);
            int target = _targetPid;
            if (header.ProcessId != target && header.ProviderId != PresentEventParser.LostEventProvider) return;

            int len = Math.Min(EtwRecordLayout.UserDataLength(_record), UserDataCopyBytes);
            IntPtr data = EtwRecordLayout.UserDataPointer(_record);
            if (len > 0 && data != IntPtr.Zero) Marshal.Copy(data, _userData, 0, len);
            else len = 0;

            switch (PresentEventParser.Classify(header, _userData.AsSpan(0, len), target, out var present))
            {
                case EtwEventKind.Present:
                    _onPresent?.Invoke(present);
                    break;
                case EtwEventKind.Lost:
                    _onLost?.Invoke(present.Timestamp);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _callbackErrors) <= 3)
                Logger.Warn("FPS: event callback failed: " + ex.Message);
        }
    }

    public long QueryLostCount()
    {
        lock (_gate)
        {
            if (_sessionHandle == 0) return -1;
            IntPtr props = Etw.AllocControlProperties();
            try
            {
                uint rc = Etw.ControlTrace(_sessionHandle, null, props, Etw.EVENT_TRACE_CONTROL_QUERY);
                return rc == Etw.ERROR_SUCCESS ? Etw.ReadLostCounters(props) : -1;
            }
            finally { Marshal.FreeHGlobal(props); }
        }
    }

    private void StopLocked()
    {
        if (_sessionHandle != 0)
        {
            IntPtr props = Etw.AllocControlProperties();
            try
            {
                uint rc = Etw.ControlTrace(_sessionHandle, null, props, Etw.EVENT_TRACE_CONTROL_STOP);
                if (rc != Etw.ERROR_SUCCESS && rc != Etw.ERROR_WMI_INSTANCE_NOT_FOUND)
                    Logger.Warn($"FPS: stopping ETW session failed ({rc}).");
            }
            finally { Marshal.FreeHGlobal(props); }
            _sessionHandle = 0;
            lock (s_nameGate) s_liveSessions--;
        }

        if (_consumerHandle != Etw.INVALID_PROCESSTRACE_HANDLE)
        {
            Etw.CloseTrace(_consumerHandle);   // ends ProcessTrace if the stop didn't already
            _consumerHandle = Etw.INVALID_PROCESSTRACE_HANDLE;
        }

        bool consumerGone = _thread is null || _thread.Join(TimeSpan.FromSeconds(3));
        if (!consumerGone)
        {
            // ETW may still read the logfile struct and call the delegate: leak rather than free.
            Logger.Warn("FPS: ETW consumer thread did not exit in time.");
            return;
        }
        _thread = null;
        if (_logfile != IntPtr.Zero) { Marshal.FreeHGlobal(_logfile); _logfile = IntPtr.Zero; }
        if (_loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerName); _loggerName = IntPtr.Zero; }
        _callback = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopLocked();
        }
    }
}
