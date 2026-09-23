using System.Buffers;
using System.IO.Compression;

namespace ProxmoxClient.Core.Vnc;

/// <summary>Tight 의 zlib 스트림 — 연결 동안 이어지는 압축 해제 상태.</summary>
public sealed partial class RfbClient
{
    /// <summary>
    ///     RFB zlib 연속 스트림 해제기 — 청크 큐 방식. 지속 DeflateStream 이 슬라이딩 윈도우를 유지한 채 순차로 읽는다.
    ///     청크 버퍼는 ArrayPool 소유권을 넘겨받아, 모두 소비되거나 Reset/Dispose 될 때 반환한다.
    /// </summary>
    private sealed class ZlibContinuousInflate : IDisposable
    {
        private const int ZlibHeaderLength = 2;

        private readonly Queue<(byte[] Buffer, int Length)> _chunks = new();
        private int _chunkOffset;
        private bool _headerSkipped;
        private DeflateStream? _inflate;

        public void Dispose()
        {
            Reset();
        }

        public void Reset()
        {
            _inflate?.Dispose();
            _inflate = null;
            _headerSkipped = false;
            while (_chunks.TryDequeue(out var chunk)) ArrayPool<byte>.Shared.Return(chunk.Buffer);

            _chunkOffset = 0;
        }

        /// <summary>ArrayPool 에서 빌린 버퍼의 소유권을 넘겨받는다(호출자는 반환하지 않는다).</summary>
        public void AddCompressedChunk(byte[] rentedBuffer, int length)
        {
            _chunks.Enqueue((rentedBuffer, length));
        }

        public int Decompress(byte[] output, int offset, int count)
        {
            if (_inflate is null)
            {
                if (!_headerSkipped)
                {
                    _chunkOffset = ZlibHeaderLength; // 스트림 최초 2바이트 zlib 헤더 건너뜀(DeflateStream 은 raw deflate)
                    _headerSkipped = true;
                }

                _inflate = new DeflateStream(
                    new ChunkReadStream(this), CompressionMode.Decompress, true);
            }

            var read = 0;
            while (read < count)
            {
                var n = _inflate.Read(output, offset + read, count - read);
                if (n == 0) break;

                read += n;
            }

            return read;
        }

        private sealed class ChunkReadStream(ZlibContinuousInflate owner) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <summary>DeflateStream 은 Span 오버로드를 호출한다 — 재정의하지 않으면 기본 구현이 임시 배열로 한 번 더 복사한다.</summary>
            public override int Read(Span<byte> destination)
            {
                var totalRead = 0;
                while (totalRead < destination.Length && owner._chunks.TryPeek(out var chunk))
                {
                    var available = chunk.Length - owner._chunkOffset;
                    if (available <= 0)
                    {
                        ArrayPool<byte>.Shared.Return(owner._chunks.Dequeue().Buffer);
                        owner._chunkOffset = 0;
                        continue;
                    }

                    var toRead = Math.Min(destination.Length - totalRead, available);
                    chunk.Buffer.AsSpan(owner._chunkOffset, toRead).CopyTo(destination[totalRead..]);
                    owner._chunkOffset += toRead;
                    totalRead += toRead;
                }

                return totalRead;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return Read(buffer.AsSpan(offset, count));
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
