using System.Net.WebSockets;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     ClientWebSocket 을 바이트 스트림처럼 감싸는 어댑터 — VNC(RFB)·RDP 가 공유한다.
///     웹소켓은 동시에 한 번만 보낼 수 있으므로 보내기를 줄 세운다(RDP 는 수신 루프와 입력이 함께 보낸다).
/// </summary>
internal sealed class ConsoleWebSocketStream(ClientWebSocket websocket) : Stream
{
    /// <summary>이 크기 이상을 요청받고 남은 데이터가 없으면 내부 버퍼를 거치지 않고 호출자 버퍼로 바로 수신한다.</summary>
    private const int DirectReceiveThreshold = 4096;

    private readonly byte[] _receiveBuffer = new byte[64 * 1024];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ArraySegment<byte> _pending;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        // 큰 읽기(Raw 행·압축 청크·JPEG)는 호출자 버퍼에 직접 받아 복사 1회를 없앤다.
        // 작은 읽기(헤더 1~4바이트)는 내부 버퍼에 크게 받아 수신 호출 횟수를 줄인다.
        if (_pending.Count == 0 && buffer.Length >= DirectReceiveThreshold)
        {
            if (websocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return 0;

            var direct = await websocket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            return direct.MessageType == WebSocketMessageType.Close ? 0 : direct.Count;
        }

        while (_pending.Count == 0)
        {
            if (websocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return 0;

            var result = await websocket.ReceiveAsync(_receiveBuffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return 0;

            _pending = new ArraySegment<byte>(_receiveBuffer, 0, result.Count);
        }

        var take = Math.Min(buffer.Length, _pending.Count);
        _pending.AsSpan(0, take).CopyTo(buffer.Span);
        _pending = _pending.Slice(take);
        return take;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        return ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await websocket.SendAsync(buffer, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        return WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
}
