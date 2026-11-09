using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class DisplayTopologyCacheTests
{
    /// <summary>Counts the reads and key queries; <see cref="Key"/> plays the monitor list.</summary>
    private sealed class FakeSource
    {
        private int _reads, _keyCalls, _inside, _maxInside;
        public volatile string Key = "hmon=0x10001 DISPLAY1 0,0,3440x1440";
        public int ReadDelayMs;
        public ManualResetEventSlim? Gate;
        public bool Throw;

        public int Reads => Volatile.Read(ref _reads);
        public int KeyCalls => Volatile.Read(ref _keyCalls);
        public int MaxConcurrentReads => Volatile.Read(ref _maxInside);

        public DisplayTopology Read()
        {
            int inside = Interlocked.Increment(ref _inside);
            int max;
            while (inside > (max = Volatile.Read(ref _maxInside)))
                Interlocked.CompareExchange(ref _maxInside, inside, max);
            try
            {
                int n = Interlocked.Increment(ref _reads);
                Gate?.Wait(TimeSpan.FromSeconds(10));
                if (ReadDelayMs > 0) Thread.Sleep(ReadDelayMs);
                if (Throw) throw new InvalidOperationException("driver reset");
                return DisplayTopology.Empty($"fake read {n}");
            }
            finally { Interlocked.Decrement(ref _inside); }
        }

        public string QuickKey()
        {
            Interlocked.Increment(ref _keyCalls);
            return Key;
        }
    }

    [Fact]
    public void Cache_reads_once_while_the_quick_key_is_unchanged()
    {
        var source = new FakeSource();
        var cache = new DisplayTopologyCache(source.Read, source.QuickKey);

        var first = cache.Get();
        for (int i = 0; i < 10; i++)
            Assert.Same(first, cache.Get());

        Assert.Equal(1, source.Reads);
        Assert.Equal(11, source.KeyCalls);   // the cheap check runs every time, the read once
    }

    [Fact]
    public void Cache_rereads_after_the_quick_key_changes()
    {
        var source = new FakeSource();
        var cache = new DisplayTopologyCache(source.Read, source.QuickKey);

        var before = cache.Get();
        source.Key = "hmon=0x10001 DISPLAY1 0,0,1920x1080";   // a mode change
        var after = cache.Get();

        Assert.NotSame(before, after);
        Assert.Equal(2, source.Reads);
        Assert.Same(after, cache.Get());
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void Cache_rereads_after_Invalidate()
    {
        var source = new FakeSource();
        var cache = new DisplayTopologyCache(source.Read, source.QuickKey);

        var before = cache.Get();
        cache.Invalidate();
        var after = cache.Get();

        Assert.NotSame(before, after);
        Assert.Equal(2, source.Reads);
        Assert.Same(after, cache.Get());
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task Invalidate_during_a_read_does_not_block_and_causes_another_read()
    {
        using var gate = new ManualResetEventSlim(false);
        var source = new FakeSource { Gate = gate };
        var cache = new DisplayTopologyCache(source.Read, source.QuickKey);

        var reader = Task.Run(cache.Get);
        Assert.True(SpinWait.SpinUntil(() => source.Reads == 1, TimeSpan.FromSeconds(5)));

        // The display event arrives while the slow read still runs: it must not wait for it ...
        await Task.Run(cache.Invalidate).WaitAsync(TimeSpan.FromSeconds(2));
        gate.Set();
        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        // ... and the read that started before the event must not count as fresh.
        source.Gate = null;
        cache.Get();
        Assert.Equal(2, source.Reads);
        cache.Get();
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void A_failing_read_returns_a_problem_and_is_retried()
    {
        var source = new FakeSource { Throw = true };
        var cache = new DisplayTopologyCache(source.Read, source.QuickKey);

        var failed = cache.Get();
        Assert.Empty(failed.Displays);
        Assert.Contains("driver reset", failed.Problem);

        source.Throw = false;
        var ok = cache.Get();
        Assert.Equal(2, source.Reads);
        Assert.Equal("fake read 2", ok.Problem);
        Assert.Same(ok, cache.Get());
    }

    [Fact]
    public async Task Cache_is_safe_from_several_threads()
    {
        var source = new FakeSource { ReadDelayMs = 30 };
        var cache = new DisplayTopologyCache(source.Read, source.QuickKey);
        const int threads = 8;
        using var start = new Barrier(threads);
        var seen = new System.Collections.Concurrent.ConcurrentBag<DisplayTopology>();

        var workers = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 20; i++) seen.Add(cache.Get());
        }, TaskCreationOptions.LongRunning)).ToArray();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, source.Reads);
        Assert.Single(seen.Distinct());

        // Gets, key changes and invalidations from several threads at once: never two reads at the same time,
        // and afterwards the cache is quiet again.
        source.ReadDelayMs = 2;
        int key = 0;
        var mixed = Enumerable.Range(0, threads).Select(t => Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < 30; i++)
            {
                if (t % 4 == 0) cache.Invalidate();
                else if (t % 4 == 1) source.Key = "key " + Interlocked.Increment(ref key);
                else cache.Get();
            }
        }, TaskCreationOptions.LongRunning)).ToArray();
        await Task.WhenAll(mixed).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, source.MaxConcurrentReads);
        var settled = cache.Get();
        int reads = source.Reads;
        Assert.Same(settled, cache.Get());
        Assert.Equal(reads, source.Reads);
    }
}
