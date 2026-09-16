using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Localization;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.Core.Terminal;

/// <summary>
///     Proxmox 터미널 콘솔(termproxy) 세션 — CT(또는 VM 시리얼) 에 서버 측 PTY 로 붙는다.
///     프로토콜(pve-xtermjs 와 동일):
///     연결 직후 "user:ticket\n" 전송 → 서버가 "OK" 로 응답하면 이후 수신 바이트는 터미널 출력.
///     입력 "0:{UTF-8 바이트 길이}:{데이터}", 크기 변경 "1:{cols}:{rows}:", keepalive "2"(30초).
///     세션은 1회용이다 — 재연결은 새 인스턴스를 만든다. 이벤트는 백그라운드 스레드에서 발생한다.
/// </summary>
public sealed class ProxmoxTerminalSession : IDisposable
{
    private const int ReceiveBufferSize = 16 * 1024;
    private const int DefaultColumns = 80;
    private const int DefaultRows = 24;
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly ProxmoxApiClient _api;

    /// <summary>세션 수명 — Dispose 시 취소되어 진행 중인 연결 시도까지 중단시킨다.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private int _closedRaised;

    // UI 스레드(Resize)와 수신 스레드(인증 직후 초기 크기 전송)가 함께 읽고 쓰므로 volatile
    private volatile int _columns = DefaultColumns;
    private CancellationTokenSource? _cts;
    private volatile bool _disposed;
    private volatile bool _isConnected;
    private volatile int _rows = DefaultRows;

    private ClientWebSocket? _socket;

    public ProxmoxTerminalSession(ProxmoxApiClient api)
    {
        _api = api;
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => _isConnected = value;
    }

    /// <summary>서버 인증 성공 응답 접두어.</summary>
    private static ReadOnlySpan<byte> AuthOk => "OK"u8;

    /// <summary>즉시 종료(닫기 핸드셰이크 없음) — UI 스레드를 막지 않는다.</summary>
    public void Dispose()
    {
        _disposed = true;
        _lifetimeCts.Cancel();
        AbandonSocket();
        IsConnected = false;
    }

    /// <summary>
    ///     터미널 출력(UTF-8 디코딩 완료, 멀티바이트 경계 보존). 수신 버퍼를 가리키는 Span 이라 호출 중에만 유효하다 —
    ///     청크마다 string 을 만들지 않으며, 구독자가 필요한 만큼 복사한다.
    /// </summary>
    public event Action<ReadOnlySpan<char>>? Output;

    public event Action<string>? StatusChanged;

    /// <summary>서버 인증("OK") 완료.</summary>
    public event Action? Connected;

    /// <summary>연결 종료. null=정상, 아니면 오류.</summary>
    public event Action<Exception?>? Closed;

    public async Task ConnectAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket is not null) throw new InvalidOperationException(Res.T("ProxmoxTerminalSession_02"));

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);

        StatusChanged?.Invoke(Res.T("ProxmoxTerminalSession_03"));
        var proxy = await _api.CreateTermProxyAsync(node, kind, vmid, connectCts.Token).ConfigureAwait(false);

        StatusChanged?.Invoke(Res.T("ProxmoxTerminalSession_04"));
        var socket = await ProxmoxConsoleSocket
            .ConnectAsync(_api, node, kind, vmid, proxy.Port, proxy.Ticket, connectCts.Token)
            .ConfigureAwait(false);

        // 연결을 기다리는 사이 창이 닫혔으면(Dispose) 소켓을 즉시 버린다 — 주인 없는 세션이 ping 으로 서버를 붙잡는 것 방지
        if (_disposed)
        {
            socket.Abort();
            socket.Dispose();
            throw new ObjectDisposedException(nameof(ProxmoxTerminalSession));
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _socket = socket;
        _cts = cts;

        // 위 확인과 대입 사이에 Dispose 가 끼면 Dispose 는 빈 필드를 보고 지나간다 — 대입 후 다시 확인해 정리
        if (_disposed)
        {
            AbandonSocket();
            throw new ObjectDisposedException(nameof(ProxmoxTerminalSession));
        }

        StatusChanged?.Invoke(Res.T("ProxmoxTerminalSession_05"));
        var user = proxy.User.Length > 0 ? proxy.User : _api.Profile.UserName;
        try
        {
            await SendTextAsync(socket, $"{user}:{proxy.Ticket}\n", connectCts.Token).ConfigureAwait(false);
        }
        catch
        {
            AbandonSocket(); // 인증 전송 실패 — 열린 소켓을 남기지 않는다
            throw;
        }

        _ = RunAsync(socket, cts.Token);
    }

    /// <summary>사용자 입력 전송(키 입력·붙여넣기).</summary>
    public void SendInput(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        // 길이는 문자 수가 아니라 UTF-8 바이트 수 — 한글 등 멀티바이트 입력 시 필수
        _outgoing.Writer.TryWrite($"0:{Encoding.UTF8.GetByteCount(data)}:{data}");
    }

    /// <summary>터미널 크기 변경 — 인증 전이면 저장해 두었다가 연결 직후 보낸다.</summary>
    public void Resize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0) return;

        _columns = columns;
        _rows = rows;
        if (IsConnected) _outgoing.Writer.TryWrite(ResizeMessage(columns, rows));
    }

    public async Task DisconnectAsync()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        var cts = Interlocked.Exchange(ref _cts, null);
        if (socket is { State: WebSocketState.Open })
        {
            using var closeCts = new CancellationTokenSource(CloseTimeout);
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when
                (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // 닫기 핸드셰이크 실패는 무시하고 강제 종료로 진행
            }
        }

        cts?.Cancel();
        socket?.Dispose();
        cts?.Dispose();
        IsConnected = false;
    }

    private static string ResizeMessage(int columns, int rows)
    {
        return $"1:{columns}:{rows}:";
    }

    /// <summary>핸드셰이크 없이 소켓·토큰을 즉시 정리(필드를 원자적으로 비워 중복 정리 방지).</summary>
    private void AbandonSocket()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        socket?.Abort();
        socket?.Dispose();
        cts?.Dispose();
    }

    private async Task RunAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? error = null;
        Task? receive = null;
        Task? send = null;
        Task? ping = null;
        try
        {
            receive = ReceiveLoopAsync(socket, loopCts.Token);
            send = SendLoopAsync(socket, loopCts.Token);
            ping = PingLoopAsync(loopCts.Token);
            var finished = await Task.WhenAny(receive, send, ping).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            loopCts.Cancel();
            _outgoing.Writer.TryComplete();
            IsConnected = false;
            RfbClient.ObserveRemaining(receive, send, ping);
        }

        if (Interlocked.Exchange(ref _closedRaised, 1) == 0) Closed?.Invoke(error);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(ReceiveBufferSize));
        try
        {
            await ReceiveIntoAsync(socket, buffer, chars, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private async Task ReceiveIntoAsync(ClientWebSocket socket, byte[] buffer, char[] chars, CancellationToken ct)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var authMatched = 0; // 확인한 "OK" 바이트 수 — 두 바이트가 서로 다른 프레임으로 나뉘어 올 수 있다

        while (!ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;

            var offset = 0;
            var count = result.Count;
            if (authMatched < AuthOk.Length)
            {
                while (authMatched < AuthOk.Length && offset < count)
                {
                    if (buffer[offset] != AuthOk[authMatched])
                        throw new IOException(Res.T("ProxmoxTerminalSession_06"));

                    authMatched++;
                    offset++;
                }

                if (authMatched < AuthOk.Length) continue; // 나머지 바이트는 다음 프레임에서 확인

                count -= offset;
                IsConnected = true;
                _outgoing.Writer.TryWrite(ResizeMessage(_columns, _rows));
                Connected?.Invoke();
            }

            if (count <= 0) continue;

            // 상태 보존 디코더 — 메시지 경계에서 잘린 UTF-8 멀티바이트 문자를 다음 메시지와 이어 붙인다
            var charCount = decoder.GetChars(buffer, offset, count, chars, 0, false);
            if (charCount > 0) Output?.Invoke(chars.AsSpan(0, charCount));
        }
    }

    private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        await foreach (var message in _outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            await SendTextAsync(socket, message, ct).ConfigureAwait(false);
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PingInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) _outgoing.Writer.TryWrite("2");
    }

    /// <summary>UTF-8 인코딩 버퍼를 풀에서 빌려 전송 — 키 입력마다 byte[] 를 새로 만들지 않는다.</summary>
    private static async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(text.Length));
        try
        {
            var length = Encoding.UTF8.GetBytes(text, buffer);
            await socket.SendAsync(buffer.AsMemory(0, length), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}