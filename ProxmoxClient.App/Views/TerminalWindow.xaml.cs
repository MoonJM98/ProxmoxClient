using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Terminal.Wpf;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Terminal;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Views;

/// <summary>
///     터미널 콘솔(CT 콘솔·노드 셸) — Proxmox termproxy(서버 측 PTY) 를 Windows Terminal 렌더러(<see cref="TerminalControl" />)로 표시.
/// </summary>
public partial class TerminalWindow : Window
{
    private static readonly Color BackgroundColor = Color.FromRgb(0x0C, 0x0C, 0x0C);

    /// <summary>Campbell 팔레트(vt100 16색 순서: 검정·빨강·초록·노랑·파랑·자홍·청록·흰색, 이어서 밝은 색).</summary>
    private static readonly uint[] CampbellColorTable =
    [
        Rgb(0x0C, 0x0C, 0x0C), Rgb(0xC5, 0x0F, 0x1F), Rgb(0x13, 0xA1, 0x0E), Rgb(0xC1, 0x9C, 0x00),
        Rgb(0x00, 0x37, 0xDA), Rgb(0x88, 0x17, 0x98), Rgb(0x3A, 0x96, 0xDD), Rgb(0xCC, 0xCC, 0xCC),
        Rgb(0x76, 0x76, 0x76), Rgb(0xE7, 0x48, 0x56), Rgb(0x16, 0xC6, 0x0C), Rgb(0xF9, 0xF1, 0xA5),
        Rgb(0x3B, 0x78, 0xFF), Rgb(0xB4, 0x00, 0x9E), Rgb(0x61, 0xD6, 0xD6), Rgb(0xF2, 0xF2, 0xF2)
    ];

    private readonly ProxmoxApiClient _api;
    private readonly bool _canPowerManage;

    // 노드 셸이면 게스트·전원 관련 필드는 비어 있다
    private readonly PveResource? _guest;
    private readonly GuestPowerRunner? _runPower;
    private readonly GuestRunStateMonitor? _runState;
    private readonly ConsoleSettingsStore _settingsStore = new();
    private readonly ConsoleTarget _target;
    private readonly string _title;
    private bool _autoConnecting;
    private bool _closed;
    private ProxmoxTerminalConnection? _connection;

    private ImeResultForwarder? _imeForwarder;

    /// <summary>Tab·방향키와 마우스 클릭이 WPF 에 빼앗기지 않고 터미널로 가게 한다.</summary>
    private TerminalInputGuard? _inputGuard;
    private ProxmoxTerminalSession? _session;

    private ConsoleSettings _settings = new();

    public TerminalWindow(ProxmoxApiClient api, PveResource guest, string guestTitle, GuestPowerRunner runPower,
        bool canPowerManage)
        : this(api, ConsoleTarget.ForGuest(guest.Node, guest.Kind, guest.VmId),
            Loc.T("TerminalWindow_Header", guestTitle))
    {
        _guest = guest;
        _runPower = runPower;
        // 게스트 객체는 메인 새로고침으로 상태가 갱신되므로 전원 버튼 표시가 자동으로 따라간다
        PowerPanel.DataContext = guest;
        PowerPanel.Visibility = canPowerManage ? Visibility.Visible : Visibility.Collapsed;
        _canPowerManage = canPowerManage;
        StoppedPanel.StartRequested += OnStoppedPanelStart;
        _runState = new GuestRunStateMonitor(guest, OnGuestStoppedChanged);
    }

    /// <summary>
    ///     노드 셸 — 노드는 꺼질 일이 없으므로 전원 버튼과 정지 안내를 쓰지 않는다.
    ///     command 가 있으면 로그인 대신 그 명령을 실행한다(예: "upgrade" = 패키지 업그레이드).
    /// </summary>
    public TerminalWindow(ProxmoxApiClient api, string node, string? command = null)
        : this(api, ConsoleTarget.ForNode(node, command),
            Loc.T(command switch
            {
                "upgrade" => "TerminalWindow_NodeUpgrade",
                "ceph_install" => "TerminalWindow_CephInstall",
                _ => "TerminalWindow_NodeShell"
            }, node))
    {
        PowerPanel.Visibility = Visibility.Collapsed;
    }

    private TerminalWindow(ProxmoxApiClient api, ConsoleTarget target, string title)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _api = api;
        _target = target;
        _title = title;
        Title = _title;

        Loaded += async (_, _) =>
        {
            _settings = await _settingsStore.LoadAsync();
            if (_closed) return; // 설정을 읽는 사이 창을 닫았으면 전역 키 필터를 새로 걸지 않는다
            Terminal.AutoResize = true;
            ApplyTheme();
            AttachImeForwarder();
            if (_runState?.IsStopped == true)
            {
                ShowStopped(); // 정지 상태면 연결하지 않고 시작 안내
                return;
            }

            await ConnectAsync();
        };
        Closing += (_, _) =>
        {
            _closed = true;
            _runState?.Dispose();
            _imeForwarder?.Dispose();
            _imeForwarder = null;
            _inputGuard?.Dispose();
            _inputGuard = null;
            DisposeSession(true);
        };
    }

    /// <summary>한글 등 IME 확정 입력을 세션으로 직접 전달(컨트롤 자체는 IME 확정 문자를 버린다).</summary>
    private void AttachImeForwarder()
    {
        _imeForwarder?.Dispose();
        _imeForwarder = ImeResultForwarder.Attach(Terminal, text => _session?.SendInput(text));
        if (_imeForwarder is null) App.Log($"[터미널 {_target.DisplayName}] IME 입력 연결 실패: 터미널 네이티브 창을 찾지 못했습니다.");

        _inputGuard?.Dispose();
        _inputGuard = TerminalInputGuard.Attach(Terminal);
    }

    /// <summary>Win32 COLORREF(0x00BBGGRR).</summary>
    private static uint Rgb(byte r, byte g, byte b)
    {
        return (uint)(r | (g << 8) | (b << 16));
    }

    private void ApplyTheme()
    {
        var theme = new TerminalTheme
        {
            DefaultBackground = Rgb(BackgroundColor.R, BackgroundColor.G, BackgroundColor.B),
            DefaultForeground = Rgb(0xCC, 0xCC, 0xCC),
            DefaultSelectionBackground = Rgb(0x26, 0x4F, 0x78),
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable = CampbellColorTable
        };
        Terminal.SetTheme(theme, _settings.TerminalFontFamily, (short)_settings.TerminalFontSize, BackgroundColor);
    }

    /// <summary>안내 화면 표시 여부 — 네이티브 터미널 창은 WPF 가 위에 그릴 수 없어 함께 숨긴다.</summary>
    private void SetStoppedView(bool stopped)
    {
        TerminalHost.Visibility = stopped ? Visibility.Collapsed : Visibility.Visible;
        if (!stopped) StoppedPanel.Hide();
    }

    private void ShowStopped()
    {
        SetStoppedView(true);
        StoppedPanel.ShowStopped(_canPowerManage);
        SetState(Loc.T("ConsoleWindow_M01"));
        UpdateButtons(false);
    }

    private void ShowWaiting(string message)
    {
        SetStoppedView(true);
        StoppedPanel.ShowWaiting(message);
    }

    /// <summary>
    ///     정지↔실행 전환 — 꺼지면 안내 화면, 외부에서 켜지면 자동 연결.
    ///     목록의 상태는 몇 초씩 늦게, 부팅 중에는 잠깐 거꾸로 올 수도 있다. 콘솔이 붙어 있으면 게스트는 켜져 있는
    ///     것이므로 안내로 화면을 덮지 않는다 — 정말 꺼지면 서버가 연결을 끊고, 그 뒤 상태가 바뀔 때 안내를 띄운다.
    /// </summary>
    private void OnGuestStoppedChanged(bool stopped)
    {
        if (_closed) return;

        var connected = _session?.IsConnected == true;
        if (stopped)
        {
            if (!connected) ShowStopped();
        }
        else if (connected) SetStoppedView(false);
        else _ = AutoConnectAsync(Loc.T("TerminalWindow_StartedConnecting"));
    }

    private async void OnStoppedPanelStart(object? sender, EventArgs e)
    {
        if (_guest is null || _runPower is null) return;

        ShowWaiting(Loc.T("Console_StartRequesting"));
        await _runPower(_guest, GuestPowerAction.Start);
        await AutoConnectAsync(Loc.T("Console_WaitingBoot"));
    }

    private async Task AutoConnectAsync(string message)
    {
        if (_autoConnecting) return;

        _autoConnecting = true;
        try
        {
            ShowWaiting(message);
            var connected = await GuestRunStateMonitor.RetryConnectAsync(
                ConnectAsync, () => !_closed && _session?.IsConnected != true);
            if (!connected && !_closed && _session?.IsConnected != true)
            {
                ShowStopped();
                SetState(Loc.T("ConsoleWindow_M02"));
            }
        }
        finally
        {
            _autoConnecting = false;
        }
    }

    /// <summary>
    ///     컨트롤이 이미 계산한 행·열을 서버 PTY 에 알린다. 컨트롤은 창 크기가 바뀔 때만 연결에 크기를 알리는데,
    ///     창 배치는 연결을 붙이기 전에 끝나므로 이대로 두면 서버는 창을 다시 조절할 때까지 기본 80×24 로 그린다.
    /// </summary>
    private void SyncTerminalSize(ProxmoxTerminalSession session)
    {
        if (Terminal.Columns > 0 && Terminal.Rows > 0) session.Resize(Terminal.Columns, Terminal.Rows);
    }

    /// <summary>연결 시도(termproxy 생성·웹소켓·인증 전송). 성공하면 true — 최종 인증 결과는 Connected/Closed 이벤트.</summary>
    private async Task<bool> ConnectAsync()
    {
        DisposeSession(false);
        UpdateButtons(false);
        BtnReconnect.IsEnabled = false; // 연결 시도 중 중복 재연결 방지

        var session = new ProxmoxTerminalSession(_api);
        var connection = new ProxmoxTerminalConnection(session, Dispatcher);

        // 재연결 후 늦게 도착한 이전 세션 이벤트가 새 세션 UI 를 덮어쓰지 않도록 세션 동일성 확인
        bool IsCurrent()
        {
            return ReferenceEquals(_session, session);
        }

        session.StatusChanged += text => Dispatcher.BeginInvoke(() =>
        {
            if (IsCurrent()) SetState(text);
        });
        session.Connected += () => Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrent()) return;

            SetStoppedView(false);
            SyncTerminalSize(session);
            SetState(Loc.T("TerminalWindow_M01"));
            UpdateButtons(true);
            Terminal.Focus();
        });
        session.Closed += ex =>
        {
            App.Log($"[터미널 {_target.DisplayName}] 연결 종료: {(ex is null ? "정상" : ex.ToString())}");
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsCurrent()) return;

                SetState(ex is null ? Loc.T("ConsoleWindow_M05") : Loc.T("ConsoleWindow_M06", ex.Message));
                UpdateButtons(false);
                if (_runState?.IsStopped == true) ShowStopped(); // 이미 꺼진 것으로 보고된 뒤 연결이 끊겼다
            });
        };

        _session = session;
        _connection = connection;
        Terminal.Connection = connection;
        SyncTerminalSize(session);

        try
        {
            await session.ConnectAsync(_target);
            return true;
        }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                App.Log($"[터미널 {_target.DisplayName}] 연결 실패: {ex}");
                SetState(Loc.T("ConsoleWindow_M07", ex.Message));
                UpdateButtons(false);
            }

            return false;
        }
    }

    private void DisposeSession(bool graceful)
    {
        var session = _session;
        _session = null;
        _connection?.Dispose();
        _connection = null;
        if (session is null) return;

        if (graceful)
            _ = session.DisconnectAsync(); // 창 종료를 막지 않도록 비동기 정리
        else
            session.Dispose();
    }

    private void UpdateButtons(bool connected)
    {
        BtnReconnect.IsEnabled = !connected;
        BtnDisconnect.IsEnabled = connected;
        // 연결 중이면 '연결 끊기', 끊겼으면 '재연결' — 같은 자리에서 서로 바뀐다
        BtnDisconnect.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        BtnReconnect.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        BtnPaste.IsEnabled = connected;
    }

    private void SetState(string text)
    {
        StateText.Text = text;
        Title = text.StartsWith(Loc.T("ConsoleWindow_M08"), StringComparison.Ordinal) ? _title : $"{_title} — {text}";
    }

    private async void OnReconnect(object sender, RoutedEventArgs e)
    {
        await ConnectAsync();
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session) return;

        await session.DisconnectAsync();
        SetState(Loc.T("MainViewModel_M05"));
        UpdateButtons(false);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Terminal.GetSelectedText();
            if (string.IsNullOrEmpty(text))
            {
                SetState(Loc.T("TerminalWindow_M02"));
                return;
            }

            Clipboard.SetText(text);
            SetState(Loc.T("TerminalWindow_M03"));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            SetState(Loc.T("TerminalWindow_M04", ex.Message));
        }

        Terminal.Focus();
    }

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_session is { IsConnected: true } session && Clipboard.ContainsText())
                // 터미널 줄바꿈은 CR — CRLF/LF 를 그대로 보내면 빈 줄이 추가로 실행된다
                session.SendInput(Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r'));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            SetState(Loc.T("TerminalWindow_M05", ex.Message));
        }

        Terminal.Focus();
    }

    /// <summary>[종료 | ▾] 의 종료 쪽 — 버튼의 Action 속성(Stop)은 표시 조건이라 여기서는 종료를 직접 부른다.</summary>
    private async void OnShutdownClick(object sender, RoutedEventArgs e)
    {
        if (_guest is { IsRunning: false })
        {
            SetState(Loc.T("GuestPower_ShutdownNeedsRunning", _guest.VmId)); // 일시 정지 — ▾ 에서 재개·정지
            return;
        }

        await RunPowerAsync(GuestPowerAction.Shutdown);
    }

    private async void OnPowerAction(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject source
            || (GuestPowerVisibility.GetAction(source) is var action && action == GuestPowerAction.None))
            return;

        await RunPowerAsync(action);
    }

    private async Task RunPowerAsync(GuestPowerAction action)
    {
        if (_guest is null || _runPower is null) return;

        var label = GuestPowerRules.Label(action);
        SetState(Loc.T("ConsoleWindow_M12", label));
        await _runPower(_guest, action);
        SetState(Loc.T("ConsoleWindow_M13", label));
        Terminal.Focus();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new ConsoleSettingsWindow(_settings, ConsoleSettingsTab.Terminal) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SavedSettings is { } saved)
        {
            _settings = saved;
            ApplyTheme(); // 글꼴·크기는 즉시 적용
            SetState(Loc.T("TerminalWindow_M06"));
        }

        Terminal.Focus();
    }
}