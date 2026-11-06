using CommunityToolkit.Mvvm.ComponentModel;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Terminal;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>터미널 출력 받는 쪽(화면) — 수신 스레드에서 불린다.</summary>
public delegate void TerminalOutput(ReadOnlySpan<char> text);

/// <summary>
///     터미널 콘솔(WPF TerminalWindow 와 같은 역할) — 노드 셸·CT 콘솔·VM 시리얼을 termproxy 로 연다.
///     세션은 1회용이라 다시 연결하면 새로 만든다. 늦게 도착한 이전 세션 이벤트는 버린다.
/// </summary>
public sealed partial class TerminalViewModel : ViewModelBase
{
    private const int MinColumns = 10;
    private const int MinRows = 2;

    private readonly string _title;
    private ProxmoxTerminalSession? _session;
    private bool _started;
    private int _columns = 80;
    private int _rows = 24;

    public TerminalViewModel(ProxmoxApiClient api, ConsoleTarget target, string title, PveResource? guest = null)
    {
        Api = api;
        Target = target;
        Guest = guest;
        _title = title;
    }

    public ProxmoxApiClient Api { get; }

    public ConsoleTarget Target { get; }

    public PveResource? Guest { get; }

    public override string Title => _title;

    /// <summary>터미널 화면 — 화면이 붙으면 출력을 넘긴다.</summary>
    public TerminalOutput? Output { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; private set; } = L.T("ConsoleWindow_06");

    /// <summary>아래 상태 줄 문구(연결됨 포함).</summary>
    [ObservableProperty] public partial string StateText { get; private set; } = L.T("ConsoleWindow_06");

    public bool HasStatus => Status is not null;

    [ObservableProperty] public partial bool IsConnected { get; private set; }

    [ObservableProperty] public partial bool IsConnecting { get; private set; }

    private bool _paused;

    public override async void OnAppearing()
    {
        if (_started) return;

        _started = true;
        AppLifecycle.ForegroundChanged += OnForegroundChanged;
        Platform.SessionKeepAlive.DisconnectAllRequested += OnDisconnectAllRequested;
        await ConnectAsync();
    }

    /// <summary>
    ///     휴대폰·태블릿에서 앱이 배경으로 갈 때. 배경에서 연결을 살려 둘 수 있으면(Android 포그라운드 서비스,
    ///     <see cref="Platform.SessionKeepAlive" />) 끊지 않는다 — 다시 연결하면 새 셸이 열려 하던 작업이 사라지므로(사용자 요청).
    ///     살려 둘 수 없으면 끊고, 돌아오면 다시 연결한다. 데스크톱은 창 포커스만 바뀌므로 그대로 둔다.
    /// </summary>
    private async void OnForegroundChanged(bool foreground)
    {
        if (!Platform.PlatformInfo.IsTouch || IsRemoved) return;
        if (!foreground && Platform.SessionKeepAlive.IsAvailable) return;

        if (!foreground)
        {
            if (!IsConnected || _session is not { } session) return;

            _paused = true;
            _session = null; // 늦게 오는 닫힘 이벤트는 버린다
            IsConnected = false;
            SetState(L.T("Console_PausedBackground"), true);
            await session.DisconnectAsync();
            return;
        }

        if (!_paused) return;

        _paused = false;
        // 배경에서 연결을 살려 둘 수 없는 플랫폼(iOS) — 새 셸로 다시 연결된다는 것을 한 번만 알린다
        if (!_backgroundNoticeShown)
        {
            _backgroundNoticeShown = true;
            AppServices.Dialogs.Toast(L.T("Terminal_ReconnectedAfterBackground"));
        }

        await ConnectAsync();
    }

    private static bool _backgroundNoticeShown;

    public override void OnDisappearing(bool removed)
    {
        if (!removed) return;

        AppLifecycle.ForegroundChanged -= OnForegroundChanged;
        Platform.SessionKeepAlive.DisconnectAllRequested -= OnDisconnectAllRequested;
        Platform.SessionKeepAlive.Release(this);

        var session = _session;
        _session = null;
        if (session is not null) _ = session.DisconnectAsync(); // 화면 닫기를 막지 않게
    }

    /// <summary>연결된 동안 배경에서도 살려 두도록 등록한다(알림에 제목이 보인다).</summary>
    partial void OnIsConnectedChanged(bool value)
    {
        // 대상까지 남겨 앱이 죽은 뒤 다음 실행에서 다시 열 수 있게 한다(TerminalRestore)
        if (value && !IsRemoved)
            Platform.SessionKeepAlive.Acquire(this, Platform.OpenTerminalEntry.From(Api.Profile, Target, Title, Guest));
        else Platform.SessionKeepAlive.Release(this);
    }

    /// <summary>알림의 [모두 끊기].</summary>
    private async void OnDisconnectAllRequested()
    {
        if (IsConnected) await DisconnectAsync();
    }

    /// <summary>키·붙여넣기 입력.</summary>
    public void Send(string text)
    {
        if (_session is { IsConnected: true } session) session.SendInput(text);
    }

    /// <summary>화면 칸 수 — 연결 전이면 기억해 두었다가 인증 직후 보낸다.</summary>
    public void Resize(int columns, int rows)
    {
        if (columns < MinColumns || rows < MinRows) return;

        _columns = columns;
        _rows = rows;
        _session?.Resize(columns, rows);
    }

    public async Task ConnectAsync()
    {
        if (IsConnecting) return;

        DisposeSession();
        IsConnecting = true;
        SetState(L.T("ConsoleWindow_06"), true);

        var session = new ProxmoxTerminalSession(Api);
        _session = session;
        bool IsCurrent() => ReferenceEquals(_session, session);

        session.Output += text =>
        {
            if (IsCurrent()) Output?.Invoke(text);
        };
        session.StatusChanged += text => Post(() =>
        {
            if (IsCurrent() && !IsConnected) SetState(text, true);
        });
        session.Connected += () => Post(() =>
        {
            if (!IsCurrent()) return;

            IsConnected = true;
            SetState(L.T("TerminalWindow_M01"), false);
        });
        session.Closed += ex => Post(() =>
        {
            if (!IsCurrent()) return;

            IsConnected = false;
            SetState(ex is null ? L.T("ConsoleWindow_M05") : L.T("ConsoleWindow_M06", ErrorText.Describe(ex)), true);
        });
        session.Resize(_columns, _rows);

        try
        {
            await session.ConnectAsync(Target);
        }
        catch (Exception ex)
        {
            if (IsCurrent()) SetState(L.T("ConsoleWindow_M07", ErrorText.Describe(ex)), true);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_session is not { } session) return;

        _session = null;
        await session.DisconnectAsync();
        IsConnected = false;
        SetState(L.T("MainViewModel_M05"), true);
    }

    private void DisposeSession()
    {
        var session = _session;
        _session = null;
        session?.Dispose();
    }

    /// <param name="overlay">화면 가운데에도 보일지(연결 중·끊김) — 연결됨은 상태 줄에만.</param>
    private void SetState(string text, bool overlay)
    {
        StateText = text;
        Status = overlay ? text : null;
    }

    private static void Post(Action action)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }
}
