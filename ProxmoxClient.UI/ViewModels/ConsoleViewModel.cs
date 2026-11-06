using CommunityToolkit.Mvvm.ComponentModel;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Vnc;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Console;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>
///     VM 화면 콘솔 — VNC(noVNC 와 같은 vncproxy + 웹소켓) 또는 RDP(IronRDP, 게스트에 RDP 가 켜진 Windows VM).
///     둘 다 Core 의 <see cref="IConsoleSession" /> 이라 그리기·입력은 같은 화면(<see cref="VncSurface" />)이 맡는다.
///     연결 수명과 상태 문구만 여기서 맡는다.
/// </summary>
public sealed partial class ConsoleViewModel : ViewModelBase
{
    private bool _started;

    public ConsoleViewModel(ServerSession session, PveResource guest, ConsoleProtocol protocol = ConsoleProtocol.Vnc)
    {
        SessionInfo = session;
        Guest = guest;
        Protocol = protocol;
        // 다시 연결할 때도 같은 방법으로 새 세션을 만든다(세션은 1회용)
        _createSession = () => protocol == ConsoleProtocol.Rdp
            ? new Core.Rdp.ProxmoxRdpSession(session.Api) { ShareClipboard = false } // 설정을 읽은 뒤 정한다
            : new ProxmoxVncSession(session.Api)
            {
                // Tight JPEG 영역은 Skia 로 바로 프레임버퍼에 푼다(데스크톱·모바일 공용)
                ImageDecoder = SkiaImageDecoder.Decode,
                Settings = new ConsoleSettings()
            };
        Vnc = _createSession();
        HookSession(Vnc);
    }

    private readonly Func<IConsoleSession> _createSession;

    /// <summary>세션 이벤트 — 다시 연결로 바뀐 옛 세션의 늦은 이벤트(끊김 등)는 무시한다.</summary>
    private void HookSession(IConsoleSession session)
    {
        session.StatusChanged += text => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (session == Vnc && !_paused) Status = text;
        });
        session.Closed += ex => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (session != Vnc) return;

            IsConnected = false;
            if (_paused) return; // 배경으로 가며 일부러 끊었다 — 돌아오면 다시 연결
            CanReconnect = true;
            Status = ex is null ? L.T("Console_Closed") : L.T("Console_ClosedError", ErrorText.Describe(ex));
        });
        session.Connected += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (session != Vnc) return;

            IsConnected = true;
            Status = null;
        });
        session.LedState += leds => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (session == Vnc) GuestCapsLock = leds.CapsLock;
        });
    }

    /// <summary>게스트 CapsLock 이 켜져 있다(VNC — 서버가 LED 상태를 알릴 때) — 화면 키보드 글자의 Shift 를 맞춘다.</summary>
    public bool GuestCapsLock { get; private set; }

    /// <summary>다시 연결로 세션이 바뀌었다(UI 스레드) — 화면이 새 세션의 이벤트를 받도록 다시 잇는다.</summary>
    public event Action<IConsoleSession>? SessionChanged;

    /// <summary>연결 직전(UI 스레드) — 화면이 RDP 처음 해상도 등을 정한다.</summary>
    public Action<IConsoleSession>? BeforeConnect { get; set; }

    public ServerSession SessionInfo { get; }

    public PveResource Guest { get; }

    public ConsoleProtocol Protocol { get; }

    /// <summary>
    ///     연결에 쓴 콘솔 설정 — 연결 직전에 저장된 설정을 읽는다(그 전에는 기본값). RDP 화면 쪽 설정
    ///     (커서·부드러운 확대·해상도 맞춤)은 화면이 <see cref="IConsoleSession.Connected" /> 뒤에 여기서 읽는다.
    /// </summary>
    public ConsoleSettings Settings { get; private set; } = new();

    /// <summary>원격 화면 세션(VNC 또는 RDP) — 다시 연결하면 새 세션으로 바뀐다(<see cref="SessionChanged" />).</summary>
    public IConsoleSession Vnc { get; private set; }

    public override string Title => $"{Guest.VmId} · {Guest.Name}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; private set; } = L.T("Console_Connecting");

    public bool HasStatus => Status is not null;

    [ObservableProperty] public partial bool IsConnected { get; private set; }

    /// <summary>연결이 끊겼거나 실패했다 — [다시 연결] 을 보인다.</summary>
    [ObservableProperty] public partial bool CanReconnect { get; private set; }

    /// <summary>
    ///     다시 연결 — 원격 화면 세션은 1회용이라 새 세션을 만들어(티켓도 새로 받는다) 이 화면에서 그대로 잇는다.
    ///     화면을 새로 만들지 않으므로 확대·이동·화면 모드·메뉴가 그대로 남는다.
    /// </summary>
    public async Task ReconnectAsync()
    {
        if (IsRemoved) return;

        CanReconnect = false;
        IsConnected = false;
        GuestCapsLock = false;
        var old = Vnc;
        Vnc = _createSession();
        HookSession(Vnc);
        old.Dispose();
        Status = L.T("Console_Connecting");
        SessionChanged?.Invoke(Vnc);
        await ConnectSessionAsync();
    }

    /// <summary>콘솔 설정을 저장하고 돌아왔다 — 화질·압축 등은 새 연결에서 적용되므로 돌아오면 다시 연결한다.</summary>
    public bool ReconnectWhenBack { get; set; }

    private bool _visible;
    private bool _paused;

    /// <summary>연결하는 중(웹소켓·핸드셰이크) — 이때 배경으로 가도 끊는다.</summary>
    private bool _connecting;

    /// <summary>
    ///     휴대폰·태블릿에서 앱이 배경으로 가면 원격 화면 연결을 끊고(배경에서는 OS 가 어차피 소켓을 끊고 배터리·데이터만 쓴다),
    ///     돌아오면 새로 연결한다(사용자 요청). 데스크톱은 창 포커스가 바뀔 뿐이라 그대로 둔다.
    /// </summary>
    private void OnForegroundChanged(bool foreground)
    {
        if (!Platform.PlatformInfo.IsTouch || IsRemoved) return;

        if (!foreground)
        {
            if (!IsConnected && !_connecting) return;

            _paused = true;
            IsConnected = false;
            Status = L.T("Console_PausedBackground");
            Vnc.Dispose();
            return;
        }

        if (!_paused) return;

        _paused = false;
        if (_visible) Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ReconnectAsync());
        else ReconnectWhenBack = true; // 다른 화면(설정 등)을 보고 있다 — 콘솔로 돌아오면
    }

    public override async void OnAppearing()
    {
        _visible = true;
        if (_started)
        {
            if (!ReconnectWhenBack)
            {
                (Vnc as ProxmoxVncSession)?.SetUpdatesPaused(false); // 가려진 동안 멈춘 화면 갱신을 다시
                return;
            }

            ReconnectWhenBack = false;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ReconnectAsync()); // 돌아오는 화면 전환이 끝난 뒤
            return;
        }

        _started = true;
        AppLifecycle.ForegroundChanged += OnForegroundChanged;
        await ConnectSessionAsync();
    }

    private async Task ConnectSessionAsync()
    {
        var session = Vnc;
        BeforeConnect?.Invoke(session);
        _connecting = true;
        try
        {
            // 설정 화면에서 저장한 콘솔 설정(화질·압축·키보드 등)을 연결 전에 읽는다 — 읽기 실패는 기본값
            Settings = await new ConsoleSettingsStore().LoadAsync();
            if (Vnc is ProxmoxVncSession vnc) vnc.Settings = Settings;
            // RDP 클립보드 공유는 Windows 에서만 된다(IronRDP 백엔드) — 다른 OS 는 클립보드 없이 연결
            if (Vnc is Core.Rdp.ProxmoxRdpSession rdp)
                rdp.ShareClipboard = Settings.RdpClipboard && Core.Rdp.ProxmoxRdpSession.ClipboardSupported;
            await Vnc.ConnectAsync(Guest.Node, Guest.VmId);
        }
        catch (Exception ex)
        {
            if (session != Vnc || _paused || IsRemoved) return; // 배경·다시 연결·화면 닫기로 일부러 끊었다
            Status = L.T("Console_ConnectFailed", ErrorText.Describe(ex));
            CanReconnect = true;
        }
        finally
        {
            if (session == Vnc) _connecting = false;
        }
    }

    public override void OnDisappearing(bool removed)
    {
        _visible = false;
        if (!removed)
        {
            // 다른 화면(설정 등)이 위를 덮었다 — 보이지 않는 화면의 갱신은 받지 않는다(데이터·배터리)
            (Vnc as ProxmoxVncSession)?.SetUpdatesPaused(true);
            return;
        }

        AppLifecycle.ForegroundChanged -= OnForegroundChanged;
        Vnc.Dispose();
    }

    public void SendKey(int keysym, bool down)
    {
        _ = Vnc.SendKeyAsync(0, down, keysym);
    }

    /// <summary>물리 키 — 서버가 QEMU 확장 키를 알면 스캔코드로, 아니면 keysym 으로(Core 가 고른다).</summary>
    public void SendScanKey(int xtScanCode, int keysym, bool down)
    {
        _ = Vnc.SendKeyAsync(xtScanCode, down, keysym);
    }

    public void SendPointer(int buttons, int x, int y)
    {
        _ = Vnc.SendPointerAsync(buttons, x, y);
    }
}
