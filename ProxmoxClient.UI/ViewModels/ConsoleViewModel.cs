using CommunityToolkit.Mvvm.ComponentModel;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Vnc;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Console;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>VM 화면 콘솔(noVNC 와 같은 vncproxy + 웹소켓 VNC) — 연결 수명과 상태 문구를 맡는다. 입력·그리기는 화면이 맡는다.</summary>
public sealed partial class ConsoleViewModel : ViewModelBase
{
    private bool _started;

    public ConsoleViewModel(ServerSession session, PveResource guest)
    {
        SessionInfo = session;
        Guest = guest;
        Vnc = new ProxmoxVncSession(session.Api)
        {
            // Tight JPEG 영역은 Skia 로 바로 프레임버퍼에 푼다(데스크톱·모바일 공용)
            ImageDecoder = SkiaImageDecoder.Decode,
            Settings = new ConsoleSettings()
        };
        Vnc.StatusChanged += text => Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = text);
        Vnc.Closed += ex => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
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

    public ProxmoxVncSession Vnc { get; }

    public override string Title => $"{Guest.VmId} · {Guest.Name}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; private set; } = L.T("Console_Connecting");

    public bool HasStatus => Status is not null;

    [ObservableProperty] public partial bool IsConnected { get; private set; }

    public override async void OnAppearing()
    {
        if (_started) return;

        _started = true;
        try
        {
            await Vnc.ConnectAsync(Guest.Node, Guest.Kind, Guest.VmId);
        }
        catch (Exception ex)
        {
            Status = L.T("Console_ConnectFailed", ErrorText.Describe(ex));
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

    public void SendPointer(int buttons, int x, int y)
    {
        _ = Vnc.SendPointerAsync(buttons, x, y);
    }
}
