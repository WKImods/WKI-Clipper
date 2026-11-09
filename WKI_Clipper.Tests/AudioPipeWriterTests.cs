using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The audio pipe's writer when ffmpeg ends first (a run ended on a mode switch): the write fails because the reader is gone.
/// That is expected and logged as such, not as an error with a stack trace on every mode switch.
/// </summary>
public sealed class AudioPipeWriterTests
{
    [Fact]
    public async Task Writing_into_a_pipe_whose_reader_closed_counts_as_the_reader_gone()
    {
        string name = "wki_test_audio_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.In);
        var connect = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await connect;
        client.Dispose();   // ffmpeg ended

        var ex = await Record.ExceptionAsync(async () =>
        {
            var chunk = new byte[64 * 1024];
            for (int i = 0; i < 16; i++) await server.WriteAsync(chunk);
        });

        Assert.NotNull(ex);
        Assert.True(AudioPipeService.IsReaderGone(ex!), ex!.GetType().Name + ": " + ex.Message);
    }

    [Fact]
    public void Other_failures_are_not()
    {
        Assert.False(AudioPipeService.IsReaderGone(new InvalidOperationException("pipe not connected yet")));
        Assert.False(AudioPipeService.IsReaderGone(new ObjectDisposedException("pipe")));
    }
}
