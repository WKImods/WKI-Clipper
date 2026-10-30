using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WKI_Clipper.Tests;

/// <summary>Two connected byte streams in memory (a stand-in for the named pipe): what one writes the other reads. Disposing an end ends the other's reads.</summary>
internal static class InMemoryPipe
{
    public static (Stream A, Stream B) Create()
    {
        var ab = Channel.CreateUnbounded<byte[]>();
        var ba = Channel.CreateUnbounded<byte[]>();
        return (new End(ba.Reader, ab.Writer), new End(ab.Reader, ba.Writer));
    }

    private sealed class End : Stream
    {
        private readonly ChannelReader<byte[]> _in;
        private readonly ChannelWriter<byte[]> _out;
        private byte[] _left = Array.Empty<byte>();
        private int _leftPos;

        public End(ChannelReader<byte[]> input, ChannelWriter<byte[]> output)
        {
            _in = input;
            _out = output;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_leftPos >= _left.Length)
            {
                if (!await _in.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                if (_in.TryRead(out var chunk))
                {
                    _left = chunk;
                    _leftPos = 0;
                }
            }
            int n = Math.Min(buffer.Length, _left.Length - _leftPos);
            _left.AsMemory(_leftPos, n).CopyTo(buffer);
            _leftPos += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_out.TryWrite(buffer.ToArray())) throw new IOException("pipe closed");
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _out.TryComplete();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
