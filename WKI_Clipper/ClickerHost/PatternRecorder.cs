using System;
using System.Threading;
using WKI_Clipper.Services.AutoClicker;

namespace WKI_Clipper.ClickerHost;

/// <summary>
/// A fixed-size queue of mouse motion from ONE producer thread (an input callback) to one consumer at a time (the caller serializes
/// the reads). Writing never blocks, allocates or locks; a full queue drops the newest motion and counts it.
/// </summary>
internal sealed class MotionRing
{
    private readonly record struct Motion(double AtMs, int Dx, int Dy);

    private readonly Motion[] _items;
    private readonly long _mask;
    private long _head;      // next slot to write; written by the producer only
    private long _tail;      // next slot to read; written by the consumer only
    private long _dropped;   // written by the producer only

    /// <param name="capacity">A power of two.</param>
    public MotionRing(int capacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "A power of two is required.");
        _items = new Motion[capacity];
        _mask = capacity - 1;
    }

    /// <summary>How many writes found the queue full, since it was made.</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>The producer's side.</summary>
    public void Write(double atMs, int dx, int dy)
    {
        long head = Volatile.Read(ref _head);
        if (head - Volatile.Read(ref _tail) >= _items.Length)
        {
            Volatile.Write(ref _dropped, _dropped + 1);
            return;
        }
        _items[head & _mask] = new Motion(atMs, dx, dy);
        Volatile.Write(ref _head, head + 1);   // publishes the slot only now that it is complete
    }

    /// <summary>The consumer's side: the oldest motion, false when the queue is empty.</summary>
    public bool TryRead(out double atMs, out int dx, out int dy)
    {
        long tail = Volatile.Read(ref _tail);
        if (tail == Volatile.Read(ref _head))
        {
            (atMs, dx, dy) = (0, 0, 0);
            return false;
        }
        var motion = _items[tail & _mask];
        (atMs, dx, dy) = (motion.AtMs, motion.Dx, motion.Dy);
        Volatile.Write(ref _tail, tail + 1);   // the producer may reuse the slot only now
        return true;
    }

    /// <summary>The consumer's side: drops everything written so far.</summary>
    public void Clear() => Volatile.Write(ref _tail, Volatile.Read(ref _head));
}

/// <summary>
/// Records one mouse pattern at a time in the helper (mouse-patterns design 4.2, 5.1). The raw input monitor and the mouse hook call
/// <see cref="OnRawMouse"/> and <see cref="OnPointerMove"/> on their own threads; while a recording runs those only write into one
/// preallocated <see cref="MotionRing"/> per track, otherwise they do nothing. A drain - every <see cref="DrainMs"/> from a timer while
/// recording, and on <see cref="Stop"/> - moves the rings into a <see cref="PatternRecording"/>, which compacts them at the end.
/// <para>
/// The drain timer also watches the 60 s limit and raises <see cref="LimitReached"/> once per recording (on the timer thread, outside
/// the recorder's lock); stopping the recording is up to the handler. A full ring (the helper stalled for a long time) drops motion
/// and is logged once, when the recording ends.
/// </para>
/// </summary>
internal sealed class PatternRecorder : IRawMouseSink, IPointerSink, IDisposable
{
    /// <summary>How often the rings are drained while recording.</summary>
    public const int DrainMs = 20;

    /// <summary>Per track: one second of an 8 kHz mouse, against a drain every <see cref="DrainMs"/>.</summary>
    public const int RingCapacity = 8192;

    private readonly IClock _clock;
    private readonly MotionRing _raw = new(RingCapacity);
    private readonly MotionRing _pointer = new(RingCapacity);
    private readonly object _gate = new();   // the consumer side: start, drain, stop, cancel, dispose
    private readonly ITimer _timer;
    private PatternRecording? _recording;
    private long _rawDroppedBefore, _pointerDroppedBefore;   // the rings' drop counts when the recording started
    private bool _limitReported;
    private bool _disposed;
    private volatile bool _isRecording;
    private volatile string? _program;

    /// <param name="clock">The time base of the callbacks' timestamps (the raw input monitor's clock).</param>
    /// <param name="time">Runs the drain timer (tests drive it by hand).</param>
    public PatternRecorder(IClock clock, TimeProvider? time = null)
    {
        _clock = clock;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>A recording runs: the callbacks take motion. Any thread.</summary>
    public bool IsRecording => _isRecording;

    /// <summary>The exe file name of the foreground program the current or last recording started in; null when unknown.</summary>
    public string? Program => _program;

    /// <summary>The current recording reached <see cref="MousePatternRules.MaxDurationMs"/> after its first movement. Timer thread.</summary>
    public event Action? LimitReached;

    /// <summary>Starts a new recording; one that is still running is discarded, as is motion left over from an earlier one.</summary>
    public void Start(string? program)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _isRecording = false;
            _raw.Clear();
            _pointer.Clear();
            _rawDroppedBefore = _raw.Dropped;
            _pointerDroppedBefore = _pointer.Dropped;
            _recording = new PatternRecording(_clock.NowMs);
            _program = program;
            _limitReported = false;
            _isRecording = true;
            _timer.Change(TimeSpan.FromMilliseconds(DrainMs), TimeSpan.FromMilliseconds(DrainMs));
        }
    }

    /// <summary>
    /// Ends the recording and compacts it (<see cref="PatternRecording.Finish"/>: Ok, Cut or Empty). Without a running recording the
    /// result is <see cref="PatternRecordingOutcome.Cancelled"/> with no data.
    /// </summary>
    public PatternRecordingResult Stop()
    {
        lock (_gate)
        {
            if (_recording is not { } recording) return new PatternRecordingResult(PatternRecordingOutcome.Cancelled, null);
            End();
            Drain(recording);   // after End: what the callbacks wrote until they saw it is still taken
            ReportDrops();
            return recording.Finish();
        }
    }

    /// <summary>Ends the recording without a result (lock, suppression, disarm, shutdown). Nothing happens without one.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_recording is null) return;
            End();
        }
    }

    public void OnRawMouse(double atMs, int dx, int dy)
    {
        if (_isRecording) _raw.Write(atMs, dx, dy);
    }

    public void OnPointerMove(double atMs, int dx, int dy)
    {
        if (_isRecording) _pointer.Write(atMs, dx, dy);
    }

    private void OnTimer()
    {
        try
        {
            bool limit = false;
            lock (_gate)
            {
                if (_recording is not { } recording) return;
                Drain(recording);
                if (!_limitReported && recording.LimitReached(_clock.NowMs)) limit = _limitReported = true;
            }
            if (limit) LimitReached?.Invoke();
        }
        catch (Exception ex) { HostLog.Error("Pattern recording: draining the motion failed", ex); }
    }

    // Under _gate.
    private void End()
    {
        _isRecording = false;
        _recording = null;
        if (!_disposed) _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // Under _gate.
    private void Drain(PatternRecording recording)
    {
        while (_raw.TryRead(out double atMs, out int dx, out int dy)) recording.AddRaw(atMs, dx, dy);
        while (_pointer.TryRead(out double atMs, out int dx, out int dy)) recording.AddPointer(atMs, dx, dy);
    }

    // Under _gate.
    private void ReportDrops()
    {
        long raw = _raw.Dropped - _rawDroppedBefore, pointer = _pointer.Dropped - _pointerDroppedBefore;
        if (raw > 0 || pointer > 0)
            HostLog.Warn($"Pattern recording: the motion buffer was full ({raw} raw and {pointer} pointer move(s) dropped); the recorded path misses them.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            End();
            _disposed = true;
        }
        _timer.Dispose();
    }
}
