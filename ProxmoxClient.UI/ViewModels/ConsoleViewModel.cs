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
        Vnc = protocol == ConsoleProtocol.Rdp
            ? new Core.Rdp.ProxmoxRdpSession(session.Api)
            : new ProxmoxVncSession(session.Api)
            {
                // Tight JPEG 영역은 Skia 로 바로 프레임버퍼에 푼다(데스크톱·모바일 공용)
                ImageDecoder = SkiaImageDecoder.Decode,
                Settings = new ConsoleSettings()
            };
        Vnc.StatusChanged += text => Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = text);
        Vnc.Closed += ex => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            CanReconnect = true;
            Status = ex is null ? L.T("Console_Closed") : L.T("Console_ClosedError", ex.Message);
        });
        Vnc.Connected += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConnected = true;
            Status = null;
        });
    }

    public ServerSession SessionInfo { get; }

    public PveResource Guest { get; }

    public ConsoleProtocol Protocol { get; }

    /// <summary>원격 화면 세션(VNC 또는 RDP).</summary>
    public IConsoleSession Vnc { get; }

    public override string Title => $"{Guest.VmId} · {Guest.Name}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; private set; } = L.T("Console_Connecting");

    public bool HasStatus => Status is not null;

    [ObservableProperty] public partial bool IsConnected { get; private set; }

    /// <summary>연결이 끊겼거나 실패했다 — [다시 연결] 을 보인다.</summary>
    [ObservableProperty] public partial bool CanReconnect { get; private set; }

    /// <summary>
    ///     다시 연결 — 원격 화면 세션은 1회용이라 같은 게스트의 새 콘솔 화면으로 바꾼다(티켓도 새로 받는다).
    /// </summary>
    public async Task ReconnectAsync()
    {
        CanReconnect = false;
        await AppServices.Navigator.PopAsync();
        await AppServices.Navigator.PushAsync(new ConsoleViewModel(SessionInfo, Guest, Protocol));
    }

    public override async void OnAppearing()
    {
        if (_started) return;

        _started = true;
        try
        {
            // 설정 화면에서 저장한 콘솔 설정(화질·압축·키보드 등)을 연결 전에 읽는다 — 읽기 실패는 기본값
            if (Vnc is ProxmoxVncSession vnc) vnc.Settings = await new ConsoleSettingsStore().LoadAsync();
            await Vnc.ConnectAsync(Guest.Node, Guest.VmId);
        }
        catch (Exception ex)
        {
            Status = L.T("Console_ConnectFailed", ErrorText.Describe(ex));
            CanReconnect = true;
        }
    }

    public override void OnDisappearing(bool removed)
    {
        if (removed) Vnc.Dispose();
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
