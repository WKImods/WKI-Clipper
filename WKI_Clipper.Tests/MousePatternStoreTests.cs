using System;
using System.IO;
using WKI_Clipper.Services;
using WKI_Clipper.Services.AutoClicker;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>Mouse-patterns design 5.2: one sample file per pattern under the app data folder.</summary>
public sealed class MousePatternStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_patterns_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static PatternData Data(int dx) => new(
        new PatternTrack(new[] { 0, 5 }, new[] { dx, 1 }, new[] { -1, 2 }),
        new PatternTrack(new[] { 3 }, new[] { dx * 2 }, new[] { 4 }),
        5);

    private string FileOf(string id) => Path.Combine(_dir, MousePatternStore.FolderName, id + MousePatternStore.Extension);

    [Fact]
    public void Store_round_trips_and_survives_corrupt_file()
    {
        var store = new MousePatternStore(_dir);
        string id = NewId();

        Assert.False(store.TryLoad(id, out var missing));   // nothing saved yet, the folder does not even exist
        Assert.Null(missing);

        store.Save(id, Data(7));
        Assert.True(File.Exists(FileOf(id)));
        Assert.StartsWith(PatternCodec.Prefix, File.ReadAllText(FileOf(id)));
        Assert.True(new MousePatternStore(_dir).TryLoad(id, out var loaded));
        Assert.Equal(new[] { 7, 1 }, loaded!.Raw.Dx);
        Assert.Equal(new[] { 14 }, loaded.Pointer.Dx);
        Assert.Equal(5, loaded.DurationMs);

        store.Save(id, Data(9));   // replacing an existing file
        Assert.True(store.TryLoad(id, out loaded));
        Assert.Equal(new[] { 9, 1 }, loaded!.Raw.Dx);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, MousePatternStore.FolderName), "*.tmp"));

        File.WriteAllText(FileOf(id), "p1:garbage!!");
        Assert.False(store.TryLoad(id, out var corrupt));
        Assert.Null(corrupt);
        File.WriteAllBytes(FileOf(id), Array.Empty<byte>());
        Assert.False(store.TryLoad(id, out _));

        store.Delete(id);
        Assert.False(File.Exists(FileOf(id)));
        Assert.False(store.TryLoad(id, out _));
        store.Delete(id);   // already gone: no throw
    }

    [Fact]
    public void A_locked_file_reads_as_missing_without_throwing()
    {
        var store = new MousePatternStore(_dir);
        string id = NewId();
        store.Save(id, Data(1));

        using (new FileStream(FileOf(id), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(store.TryLoad(id, out _));

        Assert.True(store.TryLoad(id, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(@"..\..\settings")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    public void Invalid_ids_never_touch_the_disk(string? id)
    {
        var store = new MousePatternStore(_dir);
        Assert.False(store.TryLoad(id!, out var data));
        Assert.Null(data);
        Assert.Throws<ArgumentException>(() => store.Save(id!, Data(1)));
        store.Delete(id!);   // no throw
        Assert.False(Directory.Exists(Path.Combine(_dir, MousePatternStore.FolderName)) &&
                     Directory.GetFiles(Path.Combine(_dir, MousePatternStore.FolderName)).Length > 0);
    }

    [Fact]
    public void The_store_is_the_interface_the_controller_takes()
    {
        IMousePatternStore store = new MousePatternStore(_dir);
        string id = NewId();
        store.Save(id, Data(3));
        Assert.True(store.TryLoad(id, out _));
        Assert.Equal(Path.Combine(_dir, MousePatternStore.FolderName), ((MousePatternStore)store).Directory);
    }
}
