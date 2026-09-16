using System.Net.WebSockets;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Localization;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.Core.Vnc;

/// <summary>
///     Proxmox VM 콘솔 세션: vncproxy 생성 → vncwebsocket(웹소켓, binary 서브프로토콜) 연결 → RFB 협상.
///     이벤트는 백그라운드 스레드에서 발생하므로 UI에서 디스패처 마샬링이 필요하다.
/// </summary>
public sealed class ProxmoxVncSession : IDisposable
{
    private const int ScanControlL = 0x1D;
    private const int ScanAltL = 0x38;
    private const int ScanDelete = 0xE053;
    private const int KeysymControlL = 0xFFE3;
    private const int KeysymAltL = 0xFFE9;
    private const int KeysymDelete = 0xFFFF;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>웹소켓은 열렸지만 서버가 RFB 인사말을 보내지 않는 경우(티켓 불일치 등) 무한 대기 방지.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly ProxmoxApiClient _api;

    /// <summary>세션 수명 — Dispose 시 취소되어 진행 중인 연결 시도까지 중단시킨다.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private RfbClient? _rfb;
    private ClientWebSocket? _websocket;

    public ProxmoxVncSession(ProxmoxApiClient api)
    {
        _api = api;
    }

    public bool IsConnected { get; private set; }
    public int Width => _rfb?.FramebufferWidth ?? 0;
    public int Height => _rfb?.FramebufferHeight ?? 0;

    /// <summary>서버가 QEMU 확장 키 이벤트를 확인했는지(스캔코드 전송 가능 여부).</summary>
    public bool QemuExtendedKeySupported => _rfb?.QemuExtendedKeySupported ?? false;

    /// <summary>RFB 서버 프레임버퍼 원본.</summary>
    public byte[]? Framebuffer => _rfb?.Framebuffer;

    /// <summary>Tight 이미지 디코더 — 연결 전 설정하면 RFB 클라이언트로 전달된다.</summary>
    public TightImageDecoder? ImageDecoder { get; set; }

    /// <summary>인코딩·품질·키 입력 설정 — 연결(핸드셰이크) 시점에 적용된다.</summary>
    public ConsoleSettings? Settings { get; set; }

    /// <summary>
    ///     즉시 중단(닫기 핸드셰이크 없음) — 창 닫기·재연결 시 UI 스레드를 막지 않는다.
    ///     진행 중인 연결 시도도 취소되며, QEMU 는 클라이언트 종료 시 눌린 키를 자동으로 해제한다.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _lifetimeCts.Cancel();
        var websocket = Interlocked.Exchange(ref _websocket, null);
        websocket?.Abort();
        websocket?.Dispose();
        _rfb = null;
        IsConnected = false;
    }

    /// <summary>UI 표시용 상태 문자열.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>콘솔 연결 확립(ServerInit 완료). 인자: 가로, 세로.</summary>
    public event Action<int, int>? Connected;

    /// <summary>프레임버퍼 갱신 신호(변경 영역 x,y,w,h) — 데이터는 <see cref="Framebuffer" /> 참조.</summary>
    public event Action<int, int, int, int>? FrameReceived;

    /// <summary>커서 위치 갱신(게스트 좌표).</summary>
    public event Action<int, int>? CursorPosition;

    /// <summary>커서 모양 갱신(BGRA 픽셀, 가로, 세로).</summary>
    public event Action<byte[], int, int>? CursorShape;

    /// <summary>연결 종료. null=정상, 아니면 오류.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>대역폭·인코딩 통계 스냅숏(호출 시점부터 재측정).</summary>
    public (double Mbps, double Fps, long Raw, long Tight, long Image, long Copy) TakeStats()
    {
        return _rfb is null
            ? (0, 0, 0, 0, 0, 0)
            : _rfb.TakeStats();
    }

    /// <summary>콘솔을 연다. QEMU VM 전용, 비밀번호(티켓) 인증 전용.</summary>
    public async Task ConnectAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        if (kind != ResourceKind.Qemu) throw new InvalidOperationException(Res.T("ProxmoxVncSession_01"));

        if (_api.Profile.AuthMode == AuthMode.ApiToken || _api.AuthTicket is null)
            throw new InvalidOperationException(
                Res.T("ProxmoxVncSession_02"));

        ObjectDisposedException.ThrowIf(_disposed, this);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);

        RaiseStatus(Res.T("ProxmoxVncSession_03"));
        var proxy = await _api.CreateVncProxyAsync(node, kind, vmid, connectCts.Token).ConfigureAwait(false);

        RaiseStatus(Res.T("ProxmoxVncSession_04"));
        var websocket = await ProxmoxConsoleSocket
            .ConnectAsync(_api, node, kind, vmid, proxy.Port, proxy.Ticket, connectCts.Token)
            .ConfigureAwait(false);

        // 연결을 기다리는 사이 창이 닫혔으면(Dispose) 소켓을 즉시 버린다 — 주인 없는 세션이 계속 도는 것 방지
        if (_disposed)
        {
            websocket.Abort();
            websocket.Dispose();
            throw new ObjectDisposedException(nameof(ProxmoxVncSession));
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _websocket = websocket;

        var rfb = new RfbClient(new WebSocketStream(websocket))
        {
            ImageDecoder = ImageDecoder,
            Settings = Settings
        };
        _rfb = rfb;
        rfb.ServerInitReceived += (w, h) =>
        {
            IsConnected = true;
            Connected?.Invoke(w, h);
        };
        rfb.FrameUpdated += (x, y, w, h) => FrameReceived?.Invoke(x, y, w, h);
        rfb.CursorPosition += (x, y) => CursorPosition?.Invoke(x, y);
        rfb.CursorShape += (pixels, w, h) => CursorShape?.Invoke(pixels, w, h);
        rfb.ConnectionClosed += ex =>
        {
            IsConnected = false;
            Closed?.Invoke(ex);
        };

        RaiseStatus(Res.T("ProxmoxVncSession_05"));
        using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(connectCts.Token, _cts.Token))
        {
            handshakeCts.CancelAfter(HandshakeTimeout);
            try
            {
                await rfb.HandshakeAsync(proxy.Ticket, handshakeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!connectCts.IsCancellationRequested && !_disposed)
            {
                throw new TimeoutException(Res.T("ProxmoxVncSession_06"));
            }
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = rfb.StartAsync(_cts.Token);
    }

    /// <summary>키 이벤트 전송 — 확장 키 지원 시 스캔코드(0xE0nn = 확장), 아니면 keysym 사용.</summary>
    public Task SendKeyAsync(int xtScanCode, bool down, int keysym = 0)
    {
        if (!TryGetSender(out var rfb, out var token)) return Task.CompletedTask;

        try
        {
            return rfb.SendKeyEventAsync(xtScanCode, down, keysym, token);
        }
        catch (InvalidOperationException)
        {
            return Task.CompletedTask; // 전송 채널 종료(연결 끊김) — 종료는 Closed 이벤트로 처리
        }
    }

    /// <summary>포인터 이벤트 전송 — 연속된 이동은 전송 채널에서 마지막 좌표로 병합된다. 호출당 람다/클로저 할당 없음.</summary>
    public Task SendPointerAsync(int buttonMask, int x, int y)
    {
        if (!TryGetSender(out var rfb, out var token)) return Task.CompletedTask;

        try
        {
            return rfb.SendPointerEventAsync(buttonMask, x, y, token);
        }
        catch (InvalidOperationException)
        {
            return Task.CompletedTask; // 전송 채널 종료(연결 끊김) — 종료는 Closed 이벤트로 처리
        }
    }

    /// <summary>클립보드 텍스트를 게스트로 전송.</summary>
    public Task SendClipboardAsync(string text)
    {
        if (!TryGetSender(out var rfb, out var token)) return Task.CompletedTask;

        try
        {
            return rfb.SendClientCutTextAsync(text, token);
        }
        catch (InvalidOperationException)
        {
            return Task.CompletedTask; // 전송 채널 종료(연결 끊김) — 종료는 Closed 이벤트로 처리
        }
    }

    /// <summary>Ctrl+Alt+Del 시퀀스 전송(스캔코드 + keysym 모두 지정해 두 전송 방식 어느 쪽이든 동작).</summary>
    public async Task SendCtrlAltDelAsync()
    {
        await SendKeyAsync(ScanControlL, true, KeysymControlL).ConfigureAwait(false);
        await SendKeyAsync(ScanAltL, true, KeysymAltL).ConfigureAwait(false);
        await SendKeyAsync(ScanDelete, true, KeysymDelete).ConfigureAwait(false);
        await SendKeyAsync(ScanDelete, false, KeysymDelete).ConfigureAwait(false);
        await SendKeyAsync(ScanAltL, false, KeysymAltL).ConfigureAwait(false);
        await SendKeyAsync(ScanControlL, false, KeysymControlL).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_websocket is { State: WebSocketState.Open } ws)
            {
                using var closeCts = new CancellationTokenSource(CloseTimeout);
                try
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException
                                               or ObjectDisposedException)
                {
                    // 닫기 핸드셰이크 실패는 무시하고 강제 종료로 진행
                }
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _websocket?.Dispose();
            _websocket = null;
            _rfb = null;
            IsConnected = false;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    ///     전송 가능 상태면 RFB 클라이언트와 토큰을 돌려준다. 필드를 한 번만 읽어 지역에 고정 —
    ///     전송 도중 DisconnectAsync/Dispose 가 필드를 비워도 NRE 가 나지 않는다.
    ///     (RFB 전송 메서드는 채널에 넣기만 하는 동기 작업이라 await 상태 기계·델리게이트가 필요 없다)
    /// </summary>
    private bool TryGetSender(out RfbClient rfb, out CancellationToken token)
    {
        var current = _rfb;
        rfb = current!;
        token = default;
        var cts = _cts;
        if (current is null || !IsConnected || cts is null) return false;

        try
        {
            token = cts.Token;
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false; // 연결 해제와 경합 — 전송 생략
        }
    }

    private void RaiseStatus(string text)
    {
        StatusChanged?.Invoke(text);
    }

    /// <summary>ClientWebSocket 을 RFB 가 요구하는 스트림처럼 감싸는 어댑터.</summary>
    private sealed class WebSocketStream(ClientWebSocket websocket) : Stream
    {
        /// <summary>이 크기 이상을 요청받고 남은 데이터가 없으면 내부 버퍼를 거치지 않고 호출자 버퍼로 바로 수신한다.</summary>
        private const int DirectReceiveThreshold = 4096;

        private readonly byte[] _receiveBuffer = new byte[64 * 1024];
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
            // Raw 행·압축 청크·JPEG 데이터처럼 큰 읽기는 프레임버퍼/풀 버퍼에 직접 받아 복사 1회를 없앤다.
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

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            return websocket.SendAsync(buffer, WebSocketMessageType.Binary, true, ct);
            // 배열 복사 없이 전송
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
}