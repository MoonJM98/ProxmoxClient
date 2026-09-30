using System.Diagnostics;
using System.Net.WebSockets;
using Devolutions.IronRdp;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Localization;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.Core.Rdp;

/// <summary>
///     PVE RDP 콘솔 세션(VM 디스플레이 rdp — qemu-rdp + pve-rdpproxy, 아직 정식 PVE 에 없는 기능):
///     rdpproxy 로 토큰·계정 받기 → 웹소켓(RDCleanPath) → CredSSP → RDP 세션. 화면·입력 처리는 IronRDP 가 한다.
///     IronRDP 객체는 스레드에 안전하지 않으므로 수신 처리와 입력을 한 잠금(<see cref="_gate" />)으로 줄 세운다.
///     UI 스레드는 이 잠금을 기다리지 않는다 — 수신 처리 중 클립보드 백엔드가 UI 스레드 메시지 처리를 기다릴 수 있어서
///     (크기 8 의 동기 통로) UI 가 잠금을 기다리면 서로 영원히 기다린다.
/// </summary>
public sealed partial class ProxmoxRdpSession(ProxmoxApiClient api) : IConsoleSession
{
    /// <summary>웹소켓은 열렸지만 서버가 답하지 않는 경우(프록시·RDP 서버 문제) 무한 대기 방지.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private RdpChannel? _channel;
    private bool _disposed;
    private byte[]? _framebuffer;

    /// <summary>디코딩 버퍼가 프레임버퍼보다 새롭다 — 다음에 <see cref="Framebuffer" /> 를 읽을 때 옮긴다.</summary>
    private bool _framebufferStale;
    private DecodedImage? _image;
    private InputDatabase? _input;

    /// <summary>재활성화(해상도 변경) 중 — 이때 들어온 입력은 버린다.</summary>
    private bool _reactivating;

    /// <summary>UI 가 잠금을 못 잡아 옛 프레임버퍼를 가져갔다 — 수신 처리가 끝나면 전체 갱신을 다시 알린다.</summary>
    private int _resignalFrame;

    private ActiveStage? _stage;
    private ClientWebSocket? _websocket;
    private int _width;
    private int _height;
    private long _statsBytes;
    private int _statsFrames;
    private long _statsStarted = Stopwatch.GetTimestamp();

    /// <summary>처음 요청할 게스트 해상도 — 서버가 다른 크기를 정할 수 있다.</summary>
    public (int Width, int Height) InitialSize { get; set; } = (1280, 800);

    public bool IsConnected { get; private set; }
    public int Width => _width;
    public int Height => _height;
    /// <summary>
    ///     프레임버퍼 — 화면이 바뀌었으면 읽는 순간 디코딩 버퍼에서 옮긴다. 수신 때마다 전체(1080p 8MB)를 복사하지 않고
    ///     창이 그릴 때(렌더 틱에 한 번)만 복사한다. 수신 처리 중이라 잠금을 못 잡으면 기다리지 않고 지금 것을 주고,
    ///     처리가 끝난 뒤 다시 그리게 알린다(<see cref="_resignalFrame" />).
    /// </summary>
    public byte[]? Framebuffer
    {
        get
        {
            if (!Monitor.TryEnter(_gate))
            {
                Volatile.Write(ref _resignalFrame, 1);
                return _framebuffer;
            }

            try
            {
                if (_framebufferStale && _image is not null && _framebuffer is not null)
                {
                    using var data = _image.GetData();
                    if ((int)data.GetSize() == _framebuffer.Length) data.Fill(_framebuffer);
                    _framebufferStale = false;
                }

                return _framebuffer;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }
    }

    public event Action<string>? StatusChanged;
    public event Action<int, int>? Connected;
    public event Action<int, int, int, int>? FrameReceived;
    public event Action<RfbCursor>? CursorShape;
    public event System.Action? CursorDefault;
    public event Action<Exception?>? Closed;

    /// <summary>RDP 는 키보드 LED 를 알려 주지 않는다(동기화는 클라이언트가 보낸다).</summary>
    event Action<KeyboardLeds>? IConsoleSession.LedState
    {
        add { }
        remove { }
    }

    /// <summary>RDP 클립보드는 IronRDP 백엔드가 PC 클립보드와 직접 주고받는다 — 창에 따로 알릴 것이 없다.</summary>
    event Action<string>? IConsoleSession.ClipboardReceived
    {
        add { }
        remove { }
    }

    /// <summary>콘솔을 연다 — 클립보드 백엔드 때문에 UI 스레드에서 부른다(아니면 클립보드 없이 연결).</summary>
    public async Task ConnectAsync(string node, int vmid, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clipboard = CreateClipboard(); // 첫 await 전에 — 부른 UI 스레드에서 만든다
        try
        {
            await ConnectCoreAsync(node, vmid, clipboard, ct).ConfigureAwait(false);
        }
        catch
        {
            DisposeClipboard();
            throw;
        }

        if (_clipboard is not null) _clipboardTask = Task.Run(() => ClipboardLoopAsync(_lifetimeCts.Token));
    }

    private async Task ConnectCoreAsync(string node, int vmid, CliprdrBackendFactory? clipboard, CancellationToken ct)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);

        StatusChanged?.Invoke(Res.T("Rdp_Preparing"));
        var proxy = await api.CreateRdpProxyAsync(node, vmid, connectCts.Token).ConfigureAwait(false);

        StatusChanged?.Invoke(Res.T("Rdp_Connecting"));
        var websocket = await ProxmoxConsoleSocket.ConnectRdpAsync(api, node, vmid, proxy.Token, connectCts.Token)
            .ConfigureAwait(false);
        _websocket = websocket;
        if (_disposed) // 연결을 기다리는 사이 창이 닫혔다 — 주인 없는 세션이 돌지 않게
        {
            AbortSocket();
            throw new ObjectDisposedException(nameof(ProxmoxRdpSession));
        }

        StatusChanged?.Invoke(Res.T("Rdp_LoggingIn"));
        var channel = new RdpChannel(new ConsoleWebSocketStream(websocket));
        var result = await HandshakeAsync(channel, proxy, vmid, clipboard, connectCts.Token).ConfigureAwait(false);

        lock (_gate)
        {
            if (_disposed) // 로그인하는 사이 창이 닫혔다
            {
                channel.Complete();
                throw new ObjectDisposedException(nameof(ProxmoxRdpSession));
            }

            _channel = channel;
            _stage = ActiveStage.New(result);
            _input = InputDatabase.New();
            ResetImage(result.GetDesktopSize());
            IsConnected = true;
        }

        Connected?.Invoke(_width, _height);
        _ = Task.Run(() => ReceiveLoopAsync(channel, _lifetimeCts.Token));
        _inputTask = Task.Run(() => InputLoopAsync(_lifetimeCts.Token));
    }

    /// <summary>RDP 연결 단계 — IronRDP 호출은 취소할 수 없으므로 시간을 넘기면 소켓을 끊어 멈춘다.</summary>
    private async Task<ConnectionResult> HandshakeAsync(RdpChannel channel, Models.RdpProxyInfo proxy, int vmid,
        CliprdrBackendFactory? clipboard, CancellationToken ct)
    {
        try
        {
            return await RdpHandshake.ConnectAsync(channel, BuildConfig(proxy), $"vm-{vmid}", proxy.Token, clipboard)
                .WaitAsync(HandshakeTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 어떤 실패든(시간 초과·웹소켓·EOF·인증서) 소켓과 보내기 줄을 정리하고 알린다
            AbortSocket();
            channel.Complete();
            if (ex is IronRdpException rdp)
                throw new InvalidOperationException(Res.T("Rdp_Failed", rdp.Inner.ToDisplay()), ex);

            throw;
        }
    }

    private Config BuildConfig(Models.RdpProxyInfo proxy)
    {
        var builder = ConfigBuilder.New();
        builder.WithUsernameAndPassword(proxy.User, proxy.Password);
        builder.SetDomain(string.Empty);
        builder.SetEnableTls(true);
        builder.SetEnableCredssp(true);
        builder.SetDesktopSize((ushort)ClampDimension(InitialSize.Height), (ushort)ClampDimension(InitialSize.Width));
        builder.SetClientName(Environment.MachineName);
        builder.SetClientDir("C:\\Windows\\System32\\mstscax.dll");
        builder.SetPerformanceFlags(PerformanceFlags.NewDefault());
        builder.SetEnableServerPointer(true); // 게스트 커서 모양을 받아 PC 커서로 쓴다
        builder.SetPointerSoftwareRendering(false);
        return builder.Build();
    }

    /// <summary>RDP 가 받는 해상도 범위(짝수 폭, 200~8192).</summary>
    private static int ClampDimension(int value)
    {
        return Math.Clamp(value & ~1, 200, 8192);
    }

    /// <summary>(잠금 안에서) 새 화면 크기로 디코딩 버퍼·프레임버퍼를 만든다.</summary>
    private void ResetImage(DesktopSize size)
    {
        _width = size.GetWidth();
        _height = size.GetHeight();
        _image?.Dispose();
        _image = DecodedImage.New(PixelFormat.BgrA32, (ushort)_width, (ushort)_height);
        _framebuffer = new byte[_width * _height * 4];
        _framebufferStale = false;
    }

    public string TakeStatsText()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Max(0.001, Stopwatch.GetElapsedTime(Interlocked.Exchange(ref _statsStarted, now), now)
            .TotalSeconds);
        var mbps = Interlocked.Exchange(ref _statsBytes, 0) * 8 / seconds / 1_000_000;
        var fps = Interlocked.Exchange(ref _statsFrames, 0) / seconds;
        return $"RDP · {mbps:F1} Mbps · {fps:F0} fps · {_width}×{_height}";
    }
}
