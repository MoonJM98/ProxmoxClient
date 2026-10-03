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
public sealed class ProxmoxVncSession : IConsoleSession
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

    /// <summary>연결 도중 실패 — 이 연결의 소켓만 버린다(그사이 다른 연결로 바뀌었으면 그것은 두고).</summary>
    private void AbandonConnection(ClientWebSocket websocket)
    {
        if (Interlocked.CompareExchange(ref _websocket, null, websocket) == websocket) _rfb = null;
        websocket.Abort();
        websocket.Dispose();
    }

    /// <summary>UI 표시용 상태 문자열.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>콘솔 연결 확립(ServerInit 완료). 인자: 가로, 세로.</summary>
    public event Action<int, int>? Connected;

    /// <summary>프레임버퍼 갱신 신호(변경 영역 x,y,w,h) — 데이터는 <see cref="Framebuffer" /> 참조.</summary>
    public event Action<int, int, int, int>? FrameReceived;

    /// <summary>커서 위치 갱신(게스트 좌표).</summary>
    public event Action<int, int>? CursorPosition;

    /// <summary>커서 모양 갱신(픽셀·크기·핫스팟, 크기 0 은 숨김).</summary>
    public event Action<RfbCursor>? CursorShape;

    /// <summary>게스트 키보드 LED(CapsLock·NumLock·ScrollLock) 변경 — 서버가 QEMU LED State 를 지원할 때.</summary>
    public event Action<KeyboardLeds>? LedState;

    /// <summary>연결 종료. null=정상, 아니면 오류.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>게스트 클립보드가 바뀌었다(확장 클립보드 — VM 이 clipboard=vnc 일 때). 수신 스레드에서 불린다.</summary>
    public event Action<string>? ClipboardReceived;

    /// <summary>VNC 는 기본 커서 알림이 없다(모양은 <see cref="CursorShape" /> 로만 온다).</summary>
    event Action? IConsoleSession.CursorDefault
    {
        add { }
        remove { }
    }

    Task IConsoleSession.ConnectAsync(string node, int vmid, CancellationToken ct)
    {
        return ConnectAsync(node, ResourceKind.Qemu, vmid, ct);
    }

    /// <summary>VNC 는 클라이언트가 해상도를 바꿀 수 없다(게스트가 정한다).</summary>
    public void RequestDesktopSize(int width, int height)
    {
    }

    public string TakeStatsText()
    {
        var s = TakeStats();
        return $"{s.Mbps:F1} Mbps · {s.Fps:F0} fps · Raw {s.Raw} · Tight {s.Tight} · Img {s.Image} · Copy {s.Copy}";
    }

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

        var rfb = new RfbClient(new ConsoleWebSocketStream(websocket))
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
        rfb.CursorShape += cursor => CursorShape?.Invoke(cursor);
        rfb.LedState += leds => LedState?.Invoke(leds);
        rfb.ServerCutText += text => ClipboardReceived?.Invoke(text);
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
            catch (Exception ex)
            {
                // 핸드셰이크 실패(보안 유형 미지원·인증 실패·시간 초과 등) — 연 웹소켓을 닫는다. 남기면 창을 닫거나
                // 다시 연결할 때까지 PVE 의 vncproxy 연결이 열린 채로 남는다
                AbandonConnection(websocket);
                if (ex is OperationCanceledException && !connectCts.IsCancellationRequested && !_disposed)
                    throw new TimeoutException(Res.T("ProxmoxVncSession_06"));
                throw;
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
            return rfb.SendClipboardTextAsync(text, token);
        }
        catch (InvalidOperationException)
        {
            return Task.CompletedTask; // 전송 채널 종료(연결 끊김) — 종료는 Closed 이벤트로 처리
        }
    }

    /// <summary>게스트 클립보드를 지금 요청한다(확장 클립보드일 때만) — 답은 <see cref="ClipboardReceived" /> 로 온다.</summary>
    public Task RequestClipboardAsync()
    {
        if (!TryGetSender(out var rfb, out var token)) return Task.CompletedTask;

        try
        {
            return rfb.RequestClipboardTextAsync(token);
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
}