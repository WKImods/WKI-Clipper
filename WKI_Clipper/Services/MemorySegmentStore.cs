using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace WKI_Clipper.Services;

/// <summary>
/// Fixed-size byte blocks for the RAM ring. 256 KiB blocks are large-object-heap arrays, so they are kept and handed out
/// again instead of being dropped: in a full ring every evicted segment frees about as many blocks as the next one needs,
/// and the steady state allocates nothing at all (no LOH churn, no gen-2 collections caused by the buffer).
/// </summary>
internal sealed class MemoryBlockPool
{
    public const int DefaultBlockSize = 256 * 1024;

    private readonly Stack<byte[]> _free = new();
    private readonly int _maxFree;

    public MemoryBlockPool(int blockSize = DefaultBlockSize, int maxFreeBlocks = 64)
    {
        BlockSize = Math.Max(MpegTs.PacketSize, blockSize);
        _maxFree = Math.Max(0, maxFreeBlocks);
    }

    public int BlockSize { get; }

    /// <summary>Blocks waiting for reuse (they still occupy memory).</summary>
    public int FreeBlocks { get { lock (_free) return _free.Count; } }

    public byte[] Rent()
    {
        lock (_free)
        {
            if (_free.Count > 0) return _free.Pop();
        }
        return GC.AllocateUninitializedArray<byte>(BlockSize);
    }

    public void Return(byte[] block)
    {
        if (block.Length != BlockSize) return;
        lock (_free)
        {
            if (_free.Count < _maxFree) _free.Push(block);
        }
    }

    /// <summary>Lets go of every cached block (stop/dispose: the memory goes back with the next collection).</summary>
    public void Trim()
    {
        lock (_free) _free.Clear();
    }
}

/// <summary>
/// One closed piece of the RAM ring: whole TS packets from a PAT/PMT + video key frame up to the next key frame.
/// Immutable once complete; its blocks return to the pool only when it is evicted AND no save holds a pin on it.
/// </summary>
internal sealed class MemorySegment
{
    internal readonly List<byte[]> Blocks = new();
    internal int LastBlockUsed;
    internal int Pins;
    internal bool Evicted;
    internal bool Released;

    internal MemorySegment(int generation, long sequence, long startPts, byte[] psi, bool startsWithPat)
    {
        Generation = generation;
        Sequence = sequence;
        StartPts = startPts;
        Psi = psi;
        StartsWithPat = startsWithPat;
    }

    public int Generation { get; }
    /// <summary>Store-wide counter, assigned when the segment opened: consecutive numbers of one generation = gap-free.</summary>
    public long Sequence { get; }
    /// <summary>Unwrapped 90 kHz PTS of the key frame that opens the segment.</summary>
    public long StartPts { get; }
    public long EndPts { get; internal set; }
    public TimeSpan Duration { get; internal set; }
    /// <summary>When the segment was closed (wall clock) - the equivalent of a segment file's last write time.</summary>
    public DateTime EndUtc { get; internal set; }
    public long Bytes { get; internal set; }
    /// <summary>The latest PAT+PMT packets at the segment start (prepended when the segment does not carry its own).</summary>
    public byte[] Psi { get; }
    public bool StartsWithPat { get; }

    /// <summary>The bytes in stream order, block by block. Only for complete segments a caller has pinned.</summary>
    public IEnumerable<ReadOnlyMemory<byte>> Chunks()
    {
        for (int i = 0; i < Blocks.Count; i++)
        {
            int len = i == Blocks.Count - 1 ? LastBlockUsed : Blocks[i].Length;
            if (len > 0) yield return new ReadOnlyMemory<byte>(Blocks[i], 0, len);
        }
    }
}

/// <summary>Releases the pins of a save (idempotent).</summary>
internal sealed class MemoryPin : IDisposable
{
    private MemorySegmentStore? _store;
    private readonly List<MemorySegment> _segments;

    internal MemoryPin(MemorySegmentStore store, List<MemorySegment> segments)
    {
        _store = store;
        _segments = segments;
    }

    public IReadOnlyList<MemorySegment> Segments => _segments;

    public void Dispose()
    {
        var store = Interlocked.Exchange(ref _store, null);
        store?.Unpin(_segments);
    }
}

/// <summary>
/// The replay ring in RAM: complete segments of every generation of the session, oldest first, bounded by an age limit
/// (the same window the disk prune keeps) and by a hard byte budget that counts every block held, the open segment and
/// pinned-but-evicted segments included. When the budget is reached the oldest unpinned segment goes; when nothing can go
/// (a save pins the rest) the open segment is dropped instead - the ring never grows past the budget.
/// A save pins what it picked while it copies the bytes out, so recording can go on meanwhile without evicting them.
/// </summary>
internal sealed class MemorySegmentStore : IDisposable
{
    private readonly object _lock = new();
    private readonly LinkedList<MemorySegment> _segments = new();
    private readonly MemoryBlockPool _pool;
    private readonly Func<DateTime> _clock;
    private long _budget;
    private long _heldBlocks;
    private long _payload;
    private long _nextSequence;
    private TimeSpan? _ageLimit;
    private bool _disposed;
    private bool _budgetWarned;

    public MemorySegmentStore(long budgetBytes, MemoryBlockPool? pool = null, Func<DateTime>? clock = null)
    {
        _pool = pool ?? new MemoryBlockPool();
        _clock = clock ?? (() => DateTime.UtcNow);
        _budget = Math.Max(_pool.BlockSize * 4L, budgetBytes);
    }

    public int BlockSize => _pool.BlockSize;
    public long BudgetBytes { get { lock (_lock) return _budget; } }
    /// <summary>Memory the ring holds right now: every block of every segment (open, complete, pinned).</summary>
    public long BytesInUse { get { lock (_lock) return _heldBlocks * _pool.BlockSize; } }
    /// <summary>Recorded bytes of the complete segments.</summary>
    public long PayloadBytes { get { lock (_lock) return _payload; } }
    public int Count { get { lock (_lock) return _segments.Count; } }
    internal DateTime Now => _clock();

    /// <summary>A new byte budget; a smaller one evicts the oldest unpinned segments at once. Returns the bytes freed.</summary>
    public long SetBudget(long budgetBytes)
    {
        lock (_lock)
        {
            long before = _heldBlocks;
            _budget = Math.Max(_pool.BlockSize * 4L, budgetBytes);
            while (_heldBlocks * _pool.BlockSize > _budget && EvictOldestLocked()) { }
            return (before - _heldBlocks) * _pool.BlockSize;
        }
    }

    /// <summary>Segments that closed longer ago than this are evicted as new ones arrive (null = no age limit).</summary>
    public void SetAgeLimit(TimeSpan? limit)
    {
        lock (_lock)
        {
            _ageLimit = limit;
            EvictAgedLocked(_clock());
        }
    }

    /// <summary>The writer of one ffmpeg run. Only one writer appends at a time (runs are sequential).</summary>
    public MemoryGenerationWriter OpenWriter(int generation) => new(this, generation);

    /// <summary>Complete segments, oldest first.</summary>
    public IReadOnlyList<MemorySegment> Snapshot()
    {
        lock (_lock) return _segments.ToList();
    }

    /// <summary>A snapshot whose segments are all pinned in the same step (nothing can be evicted in between).</summary>
    public MemoryPin SnapshotPinned()
    {
        lock (_lock)
        {
            var list = _segments.ToList();
            foreach (var s in list) s.Pins++;
            return new MemoryPin(this, list);
        }
    }

    /// <summary>
    /// Pins a subset of segments that the caller already holds pinned (a narrower pin replaces a broad one). Segments
    /// that are released already are skipped.
    /// </summary>
    public MemoryPin Pin(IEnumerable<MemorySegment> segments)
    {
        lock (_lock)
        {
            var list = segments.Where(s => !s.Released).Distinct().ToList();
            foreach (var s in list) s.Pins++;
            return new MemoryPin(this, list);
        }
    }

    internal void Unpin(IEnumerable<MemorySegment> segments)
    {
        lock (_lock)
        {
            foreach (var s in segments)
            {
                if (s.Pins > 0) s.Pins--;
                if (s.Pins == 0 && s.Evicted) ReleaseLocked(s);
            }
        }
    }

    /// <summary>Drops every complete segment the predicate selects (pinned ones once their save is done). Returns how many.</summary>
    public int Prune(Func<MemorySegment, bool> shouldDrop)
    {
        lock (_lock)
        {
            int n = 0;
            var node = _segments.First;
            while (node != null)
            {
                var next = node.Next;
                if (shouldDrop(node.Value))
                {
                    EvictLocked(node);
                    n++;
                }
                node = next;
            }
            return n;
        }
    }

    /// <summary>Empties the ring (cold start, switch to disk, buffer off). Pinned segments go when their save lets go.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            while (_segments.First is { } first) EvictLocked(first);
        }
        _pool.Trim();
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        Clear();
    }

    // ---- writer side ----

    internal long NextSequence()
    {
        lock (_lock) return _nextSequence++;
    }

    /// <summary>
    /// A block for the open segment, or null when the budget is reached and nothing can be evicted. Counted only once the
    /// allocation succeeded (an OutOfMemoryException must not leave a phantom block in the budget). Only the single
    /// writer adds blocks, so the room made under the lock is still there after it.
    /// </summary>
    internal byte[]? RentForWriter()
    {
        lock (_lock)
        {
            if (_disposed) return null;
            while ((_heldBlocks + 1) * _pool.BlockSize > _budget)
            {
                if (EvictOldestLocked()) continue;
                if (!_budgetWarned)
                {
                    _budgetWarned = true;
                    Logger.Warn($"RAM buffer: budget of {_budget / (1024 * 1024)} MiB reached with every older segment pinned by a save - dropping new footage until it fits");
                }
                return null;
            }
        }
        var block = _pool.Rent();
        lock (_lock) _heldBlocks++;
        return block;
    }

    /// <summary>Drops the cached free blocks (after a shrink they are memory nobody needs).</summary>
    public void TrimPool() => _pool.Trim();

    /// <summary>Gives back the blocks of an open segment that is dropped.</summary>
    internal void ReturnBlocks(List<byte[]> blocks)
    {
        lock (_lock) _heldBlocks -= blocks.Count;
        foreach (var b in blocks) _pool.Return(b);
        blocks.Clear();
    }

    internal void AddComplete(MemorySegment segment)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                _heldBlocks -= segment.Blocks.Count;
                segment.Released = true;
                foreach (var b in segment.Blocks) _pool.Return(b);
                segment.Blocks.Clear();
                return;
            }
            _segments.AddLast(segment);
            _payload += segment.Bytes;
            EvictAgedLocked(segment.EndUtc);
            while (_heldBlocks * _pool.BlockSize > _budget && EvictOldestLocked()) { }
        }
    }

    // ---- eviction (all under _lock) ----

    private void EvictAgedLocked(DateTime nowUtc)
    {
        if (_ageLimit is not { } limit) return;
        var cutoff = nowUtc - limit;
        var node = _segments.First;
        while (node != null && node.Value.EndUtc < cutoff)
        {
            var next = node.Next;
            if (node.Value.Pins == 0) EvictLocked(node);
            node = next;
        }
    }

    /// <summary>The oldest segment without a pin; false when there is none.</summary>
    private bool EvictOldestLocked()
    {
        for (var node = _segments.First; node != null; node = node.Next)
        {
            if (node.Value.Pins > 0) continue;
            EvictLocked(node);
            return true;
        }
        return false;
    }

    private void EvictLocked(LinkedListNode<MemorySegment> node)
    {
        var s = node.Value;
        _segments.Remove(node);
        _payload -= s.Bytes;
        s.Evicted = true;
        if (s.Pins == 0) ReleaseLocked(s);
    }

    private void ReleaseLocked(MemorySegment s)
    {
        if (s.Released) return;
        s.Released = true;
        _heldBlocks -= s.Blocks.Count;
        foreach (var b in s.Blocks) _pool.Return(b);
        s.Blocks.Clear();
    }
}

/// <summary>Builds the segments of one ffmpeg run from the segmenter's callbacks (runs on the reader thread).</summary>
internal sealed class MemoryGenerationWriter : ITsSegmentSink
{
    private readonly MemorySegmentStore _store;
    private MemorySegment? _open;
    private bool _dropping;

    internal MemoryGenerationWriter(MemorySegmentStore store, int generation)
    {
        _store = store;
        Generation = generation;
    }

    public int Generation { get; }
    /// <summary>Segments this run completed.</summary>
    public int Completed { get; private set; }
    /// <summary>Segments dropped because the budget was full.</summary>
    public int Dropped { get; private set; }

    public void SegmentStarted(long startPts, byte[] psi, bool startsWithPat)
    {
        DiscardOpen();
        _open = new MemorySegment(Generation, _store.NextSequence(), startPts, psi, startsWithPat);
        _dropping = false;
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        var s = _open;
        if (s is null || _dropping) return;
        while (bytes.Length > 0)
        {
            if (s.Blocks.Count == 0 || s.LastBlockUsed == s.Blocks[^1].Length)
            {
                var block = _store.RentForWriter();
                if (block is null)
                {
                    // Over budget with everything else pinned: this segment is lost (a gap), the next one starts clean.
                    _dropping = true;
                    Dropped++;
                    _store.ReturnBlocks(s.Blocks);
                    s.LastBlockUsed = 0;
                    s.Bytes = 0;
                    return;
                }
                s.Blocks.Add(block);
                s.LastBlockUsed = 0;
            }
            var target = s.Blocks[^1].AsSpan(s.LastBlockUsed);
            int n = Math.Min(target.Length, bytes.Length);
            bytes[..n].CopyTo(target);
            s.LastBlockUsed += n;
            s.Bytes += n;
            bytes = bytes[n..];
        }
    }

    public void SegmentEnded(long endPts)
    {
        var s = _open;
        _open = null;
        if (s is null) return;
        if (_dropping || endPts <= s.StartPts || s.Bytes == 0)
        {
            _store.ReturnBlocks(s.Blocks);
            return;
        }
        s.EndPts = endPts;
        s.Duration = TimeSpan.FromTicks((endPts - s.StartPts) * TimeSpan.TicksPerSecond / MpegTs.ClockRate);
        s.EndUtc = _store.Now;
        _store.AddComplete(s);
        Completed++;
    }

    /// <summary>
    /// End of the stream. After a requested stop ffmpeg flushed everything, so the open segment is complete up to its
    /// last frame and is kept (the next generation's clip can still reach it). After a crash its tail may be cut off: dropped.
    /// </summary>
    public void Finish(bool graceful, long? estimatedEndPts)
    {
        if (graceful && estimatedEndPts is { } end && _open is { } s && end > s.StartPts) SegmentEnded(end);
        else DiscardOpen();
    }

    private void DiscardOpen()
    {
        if (_open is { } s) _store.ReturnBlocks(s.Blocks);
        _open = null;
    }
}

/// <summary>
/// Drains ffmpeg's stdout on a dedicated thread and feeds the segmenter. ffmpeg blocks when its pipe is full, which would
/// stall capture and encoding, so this loop does nothing but read, split and copy - no I/O, no locks held while reading.
/// </summary>
internal sealed class MemoryBufferFeed : IDisposable
{
    private readonly Stream _stdout;
    private readonly MemoryGenerationWriter _writer;
    private readonly MpegTsSegmenter _segmenter;
    private readonly Func<bool> _stopRequested;
    private readonly Thread _thread;
    private long _cpuTicks;

    private readonly Action<string>? _onFailed;

    /// <param name="onFailed">
    /// Called once (on the reader thread) when reading or splitting fails while no stop was requested - the run records
    /// nothing usable any more and must be restarted. The reader keeps draining the pipe afterwards, so ffmpeg never blocks
    /// on a full stdout and a graceful stop still works.
    /// </param>
    public MemoryBufferFeed(Stream stdout, MemoryGenerationWriter writer, int segmentSeconds, double? deltaSeconds,
        Func<bool> stopRequested, Action<string>? onFailed = null)
    {
        _stdout = stdout;
        _writer = writer;
        _segmenter = new MpegTsSegmenter(writer, segmentSeconds, deltaSeconds);
        _stopRequested = stopRequested;
        _onFailed = onFailed;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WKI RAM buffer reader",
            Priority = ThreadPriority.AboveNormal
        };
    }

    public long BytesRead { get; private set; }
    public byte VideoStreamType => _segmenter.VideoStreamType;
    /// <summary>CPU time (user + kernel) of the reader thread, set when it ends.</summary>
    public TimeSpan CpuTime => TimeSpan.FromTicks(Interlocked.Read(ref _cpuTicks));
    public MemoryGenerationWriter Writer => _writer;

    public void Start() => _thread.Start();

    /// <summary>Waits for the reader to reach the end of the stream (after ffmpeg exited). False on timeout.</summary>
    public bool WaitForEnd(TimeSpan timeout) => !_thread.IsAlive || _thread.Join(timeout);

    /// <summary>True once splitting failed: the rest of the stream is read and thrown away.</summary>
    public bool Failed { get; private set; }

    private void Run()
    {
        var buffer = new byte[128 * 1024];
        bool readFailed = false;
        try
        {
            int n;
            while ((n = _stdout.Read(buffer, 0, buffer.Length)) > 0)
            {
                BytesRead += n;
                if (Failed) continue;   // draining: ffmpeg must never block on a full pipe
                try { _segmenter.Feed(buffer.AsSpan(0, n)); }
                catch (Exception ex)
                {
                    // Out of memory for a block, a parser bug: this run produces nothing usable any more.
                    Failed = true;
                    try { _writer.Finish(graceful: false, null); } catch { }
                    Fail($"splitting the stream failed ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }
        catch (Exception ex)
        {
            readFailed = true;
            if (!_stopRequested()) Fail($"reading ffmpeg's output failed ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            if (!Failed)
            {
                try { _writer.Finish(!readFailed && _stopRequested(), _segmenter.EstimatedEndPts); }
                catch (Exception ex) { Logger.Warn("RAM buffer reader finish failed: " + ex.Message); }
            }
            Interlocked.Exchange(ref _cpuTicks, ThreadCpu.Current().Ticks);
        }
    }

    private void Fail(string cause)
    {
        Logger.Warn("RAM buffer reader: " + cause + " - the buffer is restarted");
        try { _onFailed?.Invoke(cause); }
        catch (Exception ex) { Logger.Warn("RAM buffer failure handler threw: " + ex.Message); }
    }

    public void Dispose()
    {
        try { _stdout.Dispose(); } catch { }
    }
}

/// <summary>CPU time of the calling thread.</summary>
internal static class ThreadCpu
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    public static TimeSpan Current()
    {
        try
        {
            return GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user)
                ? TimeSpan.FromTicks(kernel + user)   // FILETIME units = 100 ns = TimeSpan ticks
                : TimeSpan.Zero;
        }
        catch { return TimeSpan.Zero; }
    }
}
