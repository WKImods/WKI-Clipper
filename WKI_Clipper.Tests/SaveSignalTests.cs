using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The save paths tell the app when a clip/GIF save starts and when it is refused or fails, so the
/// sound cues (and later a controller) react to the key press instead of parsing toast texts.
/// Only the guards that need no ffmpeg can be exercised here; the rest is verified by hand.
/// </summary>
public sealed class SaveSignalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wki_signals_" + Guid.NewGuid().ToString("N"));
    private readonly ReplayBufferService _buffer;
    private readonly List<SaveKind> _started = new();
    private readonly List<SaveFailure> _failed = new();

    public SaveSignalTests()
    {
        _buffer = new ReplayBufferService(new SettingsService(_dir));
        _buffer.SaveStarted += (_, kind) => _started.Add(kind);
        _buffer.SaveFailed += (_, failure) => _failed.Add(failure);
    }

    public void Dispose()
    {
        _buffer.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task A_clip_save_without_a_running_buffer_is_refused_and_never_started()
    {
        var path = await _buffer.SaveLastAsync();

        Assert.Null(path);
        Assert.Empty(_started);
        Assert.Equal(new[] { new SaveFailure(SaveKind.Clip, SaveFailureReason.NotRunning) }, _failed);
    }

    [Fact]
    public async Task A_gif_save_without_a_running_buffer_is_refused_and_never_started()
    {
        var path = await _buffer.SaveGifAsync();

        Assert.Null(path);
        Assert.Empty(_started);
        Assert.Equal(new[] { new SaveFailure(SaveKind.Gif, SaveFailureReason.NotRunning) }, _failed);
    }

    [Fact]
    public async Task A_refusal_still_raises_the_user_facing_notice()
    {
        string? info = null;
        _buffer.BufferInfo += (_, msg) => info = msg;

        await _buffer.SaveLastAsync();

        Assert.False(string.IsNullOrEmpty(info));
    }

    [Fact]
    public async Task A_throwing_subscriber_cannot_break_the_save_path()
    {
        _buffer.SaveFailed += (_, _) => throw new InvalidOperationException("bad handler");

        var path = await _buffer.SaveLastAsync();

        Assert.Null(path);
        Assert.Single(_failed);   // the first subscriber still saw it
    }
}
