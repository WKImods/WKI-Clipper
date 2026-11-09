using System;
using System.Threading;

namespace WKI_Clipper.Services;

/// <summary>
/// Serves the <see cref="DisplayTopology"/> without re-reading it when nothing changed. A full read creates a D3D11
/// device and queries CCD; the capture view asks every second on the UI thread, so <see cref="Get"/> only runs the cheap
/// quick key (the monitor list) and re-reads when that key changed or after <see cref="Invalidate"/> (the display
/// watcher calls it on every display event). Thread-safe: reads are serialized; <see cref="Invalidate"/> never waits for
/// a running read, and a read that was already running when it came is not taken as fresh.
/// </summary>
public sealed class DisplayTopologyCache
{
    /// <summary>The app-wide cache over the real Windows topology.</summary>
    public static DisplayTopologyCache Shared { get; } = new(DisplayTopologyReader.Read, DisplayTopologyReader.QuickKey);

    private readonly Func<DisplayTopology> _read;
    private readonly Func<string> _quickKey;
    private readonly object _gate = new();
    private DisplayTopology? _topology;
    private string? _key;
    private int _readGeneration;
    private int _generation;   // bumped by Invalidate, outside the lock

    public DisplayTopologyCache(Func<DisplayTopology> read, Func<string> quickKey)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _quickKey = quickKey ?? throw new ArgumentNullException(nameof(quickKey));
    }

    public DisplayTopology Get()
    {
        lock (_gate)
        {
            int generation = Volatile.Read(ref _generation);
            string key = QuickKeyOrEmpty();
            if (_topology is not null && generation == _readGeneration && key == _key)
                return _topology;

            DisplayTopology fresh;
            try
            {
                fresh = _read();
            }
            catch (Exception ex)
            {
                // Not cached: the next Get reads again.
                return DisplayTopology.Empty($"topology read failed: {ex.GetType().Name}: {ex.Message}");
            }
            _topology = fresh;
            _key = key;
            _readGeneration = generation;
            return fresh;
        }
    }

    /// <summary>The next <see cref="Get"/> reads again, whatever the quick key says.</summary>
    public void Invalidate() => Interlocked.Increment(ref _generation);

    private string QuickKeyOrEmpty()
    {
        try { return _quickKey() ?? ""; }
        catch { return ""; }
    }
}
