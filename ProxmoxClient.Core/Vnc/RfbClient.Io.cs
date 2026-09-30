using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Vnc;

/// <summary>저수준 읽기·쓰기 — 정해진 길이만큼 받기와 빅엔디언 정수.</summary>
public sealed partial class RfbClient
{
    private async ValueTask<uint> ReadU32Async(CancellationToken ct)
    {
        await ReadExactlyAsync(_scratch, 0, 4, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(_scratch);
    }
    private async ValueTask<ushort> ReadU16Async(CancellationToken ct)
    {
        await ReadExactlyAsync(_scratch, 0, 2, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16BigEndian(_scratch);
    }
    /// <summary>ValueTask — 수신 버퍼에 데이터가 있어 동기로 끝나면 할당이 없다(rect 헤더·Tight 길이 등 핫패스).</summary>
    private async ValueTask<byte> ReadByteAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_scratch, 0, 1, ct).ConfigureAwait(false);
        return _scratch[0];
    }
    private async Task<byte[]> ReadPixelFormatAsync(CancellationToken ct)
    {
        var format = new byte[16];
        await ReadExactlyAsync(format, 0, 16, ct).ConfigureAwait(false);
        return format;
    }
    private async Task SkipAsync(int count, CancellationToken ct)
    {
        const int SkipChunkSize = 4096;
        var sink = ArrayPool<byte>.Shared.Rent(Math.Min(count, SkipChunkSize));
        try
        {
            while (count > 0)
            {
                var take = Math.Min(count, sink.Length);
                await ReadExactlyAsync(sink, 0, take, ct).ConfigureAwait(false);
                count -= take;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sink);
        }
    }
    /// <summary>비동기·취소 가능 — 동기 대기(sync-over-async)로 핸드셰이크가 취소/타임아웃되지 않던 문제 제거.</summary>
    private async Task<string> ReadStringAsciiAsync(int length, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactlyAsync(buf, 0, length, ct).ConfigureAwait(false);
            return Encoding.Latin1.GetString(buf, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
    /// <summary>ValueTask — 동기 완료(수신 버퍼에서 바로 채움) 시 Task 할당 없음.</summary>
    private async ValueTask ReadExactlyAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(offset + read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException(Res.T("RfbClient_19")); // 서버의 정상 종료와 오류를 구분하기 위한 전용 예외

            read += n;
            Interlocked.Add(ref _bytesReceived, n);
        }
    }
    private async Task WriteAsync(byte[] payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
    private static int ParseVersionPart(string version, int offset)
    {
        return int.TryParse(version.AsSpan(offset, 3), out var v) ? v : 0;
    }
}
