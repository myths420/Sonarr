using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Core.Download
{
    // Pass-through write stream that reports the running byte total to a
    // callback after each write. IHttpClient streams a response body
    // straight into HttpRequest.ResponseStream, so wrapping the file stream
    // in this is the only way to observe an in-progress download's size --
    // used by the in-process download clients/services (DirectHttp, Sites).
    public sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _onProgress;
        private long _written;

        public ProgressStream(Stream inner, Action<long> onProgress)
        {
            _inner = inner;
            _onProgress = onProgress;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            _written += count;
            _onProgress(_written);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken);
            _written += buffer.Length;
            _onProgress(_written);
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
