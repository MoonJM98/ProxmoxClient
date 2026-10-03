using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Rdp;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Views;

public partial class ConsoleWindow : Window
{
    private const uint MapVkVkToVscEx = 4;
    // ── 저수준 키보드 훅 (모든 키 가로채기: IME 한/영, Win, Alt+Tab 등) ──
    private const int WhKeyboardLL = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;
    private const uint LlkhfExtended = 0x01;
    // 렌더 루프는 프레임이 들어올 때만 구독 — 유휴 시 모니터 주사율로 도는 WPF 렌더 틱을 멈춘다
    private const int IdleRenderTicksBeforeUnhook = 120;
    /// <summary>WritePixels 연속 실패 상한 — 조건이 계속 맞지 않을 때 매 틱 예외·재요청이 반복되지 않도록.</summary>
    private const int MaxConsecutiveWritePixelsFailures = 5;
    private const int KeysymKpEnter = 0xFF8D;
    private readonly ProxmoxApiClient _api;
    private readonly bool _canPowerManage;
    private readonly object _dirtyLock = new();
    /// <summary>FlushFrame 이 잠금 밖에서 업로드할 사각형 스냅숏(UI 스레드 전용, 재사용).</summary>
    private readonly DirtyRegion.Rect[] _flushRects =
        new DirtyRegion.Rect[DirtyRegion.Capacity];
    private readonly PveResource _guest;
    private readonly string _guestTitle;
    private readonly HashSet<int> _heldModifiers = [];
    private readonly string _node;
    /// <summary>렌더 틱 사이에 쌓인 변경 영역(최대 8개, 떨어진 작은 영역을 화면 전체로 합치지 않음). _dirtyLock 보호.</summary>
    private readonly DirtyRegion _pendingDirty = new();
    /// <summary>눌린 키(VK) → down 때 보낸 (스캔코드, keysym). up 은 이 값으로 보내 Shift 변화 등에도 짝이 맞는다.</summary>
    private readonly Dictionary<int, (int XtScanCode, int Keysym)> _pressedKeys = [];
    private readonly GuestPowerRunner _runPower;
    private readonly GuestRunStateMonitor _runState;
    private readonly ConsoleSettingsStore _settingsStore = new();
    private readonly string _title;
    private readonly Toast _toast;
    private readonly int _vmid;
    private bool _autoConnecting;
    private WriteableBitmap? _bitmap;
    private bool _closed;
    private int _fbHeight;
    private int _fbWidth;
    private bool _fitMode = true;
    private int _flushQueued;
    private HookProc? _hookProc;
    private int _idleRenderTicks;
    private IntPtr _keyboardHook;
    private int _pointerMask;
    private int _renderingHooked;
    private IConsoleSession? _session;
    private ConsoleSettings _settings = new();
    private DispatcherTimer? _statsTimer;
    private int _writePixelsFailures;
    public ConsoleWindow(ProxmoxApiClient api, PveResource guest, string guestTitle, GuestPowerRunner runPower,
        bool canPowerManage, ConsoleProtocol protocol = ConsoleProtocol.Vnc)
    {
        if (guest.Kind != ResourceKind.Qemu) throw new NotSupportedException(Loc.T("ConsoleWindow_VmOnly"));

        InitializeComponent();
        _toast = new Toast(ConsoleScroll);
        WindowTheme.ApplyDarkTitleBar(this);
        _api = api;
        _guest = guest;
        _runPower = runPower;
        _node = guest.Node;
        _vmid = guest.VmId;
        _guestTitle = guestTitle;
        _protocol = protocol;
        // 게스트 객체는 메인 새로고침으로 상태가 갱신되므로 전원 버튼 표시가 자동으로 따라간다
        PowerPanel.DataContext = guest;
        PowerPanel.Visibility = canPowerManage ? Visibility.Visible : Visibility.Collapsed;
        InitFiles(api, guest);
        _canPowerManage = canPowerManage;
        StoppedPanel.StartRequested += OnStoppedPanelStart;
        _runState = new GuestRunStateMonitor(guest, OnGuestStoppedChanged);
        _title = Loc.T(IsRdp ? "ConsoleWindow_HeaderRdp" : "ConsoleWindow_Header", guestTitle);
        Title = _title;
        // 키보드는 이 창이 활성일 때만 가로챈다 — 비활성·최소화되면 곧바로 풀고 눌린 키를 뗀다
        Activated += (_, _) =>
        {
            InstallKeyboardHook();
            SyncClipboardToGuest(); // PC 에서 새로 복사하고 돌아왔으면 게스트로
        };
        Deactivated += (_, _) =>
        {
            RemoveKeyboardHook();
            RequestGuestClipboard(); // 다른 창에서 붙여 넣을 수 있게 게스트 클립보드를 PC 로
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) RemoveKeyboardHook();
            else InstallKeyboardHook();
        };
        ScreenImage.LostMouseCapture += (_, _) => _pointerMask = 0; // 캡처를 잃으면 눌림 상태가 남지 않게
        // 화면 크기(맞춤 배율)·모니터 DPI 가 바뀌면 커서 크기도 게스트 화면과 같은 비율로 다시 만든다
        ScreenImage.SizeChanged += (_, _) => RefreshCursor();
        ConsoleScroll.SizeChanged += (_, _) => QueueDesktopResize(); // RDP — 게스트 해상도를 창에 맞춘다
        // GPU 화면 — 뷰포트·스크롤·배치가 바뀌면 자식 창 크기와 그릴 자리를 다시 맞춘다
        ConsoleScroll.ScrollChanged += (_, _) =>
        {
            UpdateDirectSlot();
            UpdateDirectLayout();
            MoveCursorOverlay(Mouse.GetPosition(ScreenHost)); // 1:1 에서 스크롤하면 겹친 커서도 따라오게
        };
        LayoutUpdated += (_, _) => UpdateDirectLayout();
        StoppedPanel.IsVisibleChanged += (_, _) => UpdateDirectSlot();
        DpiChanged += (_, _) =>
        {
            RefreshCursor();
            UpdateDirectLayout(); // GPU 화면의 그릴 자리는 물리 픽셀 기준
            QueueDesktopResize(); // RDP — WPF 크기는 같아도 실제 픽셀 수가 바뀐다
        };
        Loaded += async (_, _) =>
        {
            _settings = await _settingsStore.LoadAsync();
            ApplyScalingMode();
            ApplyDirectRendering();
            if (_runState.IsStopped)
            {
                ShowStopped(); // 정지 상태면 연결하지 않고 시작 안내
                return;
            }

            await ConnectAsync();
        };
        Closing += (_, _) =>
        {
            ReleaseStickyKeys(); // 게스트에 Ctrl 등이 눌린 채 남지 않게(연결이 살아 있을 때 보낸다)
            _closed = true;
            CloseDirect();
            _runState.Dispose();
            CompositionTarget.Rendering -= OnCompositionRendering;
            _statsTimer?.Stop();
            RemoveKeyboardHook();
            _filesWindow?.Close();
            // RDP 는 떼는 키·종료 알림을 보낸 뒤 닫는다(짧게, 뒤에서) — 게스트에 키가 눌린 채 남지 않게
            if (IsRdp && _session is { IsConnected: true } rdp)
                _ = rdp.DisconnectAsync().ContinueWith(t => App.Log($"[콘솔 {_vmid}] RDP 종료 실패: {t.Exception}"),
                    TaskContinuationOptions.OnlyOnFaulted);
            else _session?.Dispose();
        };
        Closed += (_, _) => _cursorHandle?.Dispose(); // 창이 사라진 뒤 — 쓰는 중인 커서 핸들을 먼저 지우지 않게
    }
    private void ShowStopped()
    {
        StoppedPanel.ShowStopped(_canPowerManage);
        SetState(Loc.T("ConsoleWindow_M01"));
        UpdateButtons(false);
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
        else if (connected) StoppedPanel.Hide();
        else _ = AutoConnectAsync(Loc.T("ConsoleWindow_StartedConnecting"));
    }
    private async void OnStoppedPanelStart(object? sender, EventArgs e)
    {
        StoppedPanel.ShowWaiting(Loc.T("Console_StartRequesting"));
        await _runPower(_guest, GuestPowerAction.Start);
        await AutoConnectAsync(Loc.T("Console_WaitingBoot"));
    }
    private async Task AutoConnectAsync(string message)
    {
        if (_autoConnecting) return;

        _autoConnecting = true;
        try
        {
            StoppedPanel.ShowWaiting(message);
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
    /// <summary>연결 시도. 핸드셰이크까지 성공하면 true.</summary>
    private async Task<bool> ConnectAsync()
    {
        _session?.Dispose();
        _session = null;
        ResetCursor();
        SetState(Loc.T("ConsoleWindow_M03"));
        UpdateButtons(false);
        BtnConnect.IsEnabled = false; // 연결 시도 중 중복 재연결 방지

        IConsoleSession session = IsRdp
            ? new ProxmoxRdpSession(_api)
            {
                InitialSize = DesiredDesktopSize(), ShareClipboard = _settings.RdpClipboard
            }
            : new ProxmoxVncSession(_api) { ImageDecoder = DecodeTightImage, Settings = _settings };

        // 재연결 후 늦게 도착한 이전 세션 이벤트가 새 세션 UI 를 덮어쓰지 않도록 세션 동일성 확인
        bool IsCurrent()
        {
            return ReferenceEquals(_session, session);
        }

        session.StatusChanged += text => Dispatcher.BeginInvoke(() =>
        {
            if (IsCurrent()) SetState(text);
        });
        session.Connected += (w, h) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrent()) return;

            StoppedPanel.Hide();
            EnsureBitmap(w, h);
            _direct?.InvalidateAll();
            // RDP 창 맞춤 해상도면 게스트가 창에 맞췄다 — 창을 게스트 크기(물리 픽셀)로 다시 키우지 않는다
            if (!FitsGuestToWindow) FitWindowToFramebuffer(w, h);
            var tier = RenderCapability.Tier >> 16;
            var accel = tier >= 2 ? Loc.T("ConsoleWindow_AccelHardware") :
                tier == 1 ? Loc.T("ConsoleWindow_AccelPartial") : Loc.T("ConsoleWindow_AccelSoftware");
            SetState(Loc.T("ConsoleWindow_M04", w, h, accel));
            UpdateButtons(true);
            Focus();
            StartStatsTicker();
            _ = DetectClipboardAsync();
            if (!IsRdp) _ = HintCursorShapeAsync(session); // RDP 는 커서 모양을 늘 보낸다
        });
        session.ClipboardReceived += OnGuestClipboard;
        session.FrameReceived += QueueFrameFlush;
        session.LedState += leds => Dispatcher.BeginInvoke(() =>
        {
            if (IsCurrent()) OnGuestLeds(leds);
        });
        session.CursorShape += cursor => Dispatcher.BeginInvoke(() =>
        {
            if (IsCurrent()) ApplyCursorShape(cursor);
        });
        session.CursorDefault += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsCurrent()) ResetCursor();
        });
        session.Closed += ex =>
        {
            App.Log($"[콘솔 {_vmid}] 연결 종료: {(ex is null ? "정상" : ex.ToString())}");
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsCurrent()) return;

                _statsTimer?.Stop();
                SetState(ex is null ? Loc.T("ConsoleWindow_M05") : Loc.T("ConsoleWindow_M06", ex.Message));
                UpdateButtons(false);
                if (_runState.IsStopped) ShowStopped(); // 이미 꺼진 것으로 보고된 뒤 연결이 끊겼다
            });
        };
        _session = session;
        try
        {
            await session.ConnectAsync(_node, _vmid);
            return true;
        }
        catch (Exception ex) when (IsCurrent())
        {
            // 이전 세션의 실패가 새 세션 상태를 덮어쓰지 않도록 현재 세션일 때만 표시
            App.Log($"[콘솔 {_vmid}] 연결 실패: {ex}");
            SetState(Loc.T("ConsoleWindow_M07", ex.Message));
            UpdateButtons(false);
            return false;
        }
        catch (Exception ex)
        {
            App.Log($"[콘솔 {_vmid}] 이전 세션 연결 중단: {ex.Message}");
            return false;
        }
    }
    private void EnsureBitmap(int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        if (_bitmap is not null && _fbWidth == width && _fbHeight == height) return;

        _fbWidth = width;
        _fbHeight = height;
        // Bgr32: 4번째 바이트(서버 패딩) 무시 — 픽셀 단위 후처리 없이 곧장 업로드
        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        ScreenImage.Source = _bitmap;
    }
    /// <summary>프레임 병합 — flush 대기 중 도착한 갱신은 dirty 영역만 확장(업로드 최신 상태에 자동 포함).</summary>
    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _flushQueued, 0) == 1)
        {
            _idleRenderTicks = 0;
            FlushFrame();
        }
        else if (++_idleRenderTicks >= IdleRenderTicksBeforeUnhook)
        {
            UnhookRendering();
        }
    }
    /// <summary>읽기 스레드에서 호출 — 변경 영역을 누적하고 렌더 루프가 꺼져 있으면 UI 스레드에서 켠다.</summary>
    private void QueueFrameFlush(int x, int y, int w, int h)
    {
        if (TryQueueDirectFrame(x, y, w, h)) return; // GPU 로 바로 그리는 중 — WPF 렌더 틱을 거치지 않는다

        lock (_dirtyLock)
        {
            _pendingDirty.Add(x, y, w, h);
        }

        Interlocked.Exchange(ref _flushQueued, 1);
        if (Interlocked.CompareExchange(ref _renderingHooked, 1, 0) == 0) Dispatcher.BeginInvoke(HookRendering);
    }
    private void HookRendering()
    {
        if (_closed) return;

        _idleRenderTicks = 0;
        CompositionTarget.Rendering -= OnCompositionRendering;
        CompositionTarget.Rendering += OnCompositionRendering;
    }
    private void UnhookRendering()
    {
        CompositionTarget.Rendering -= OnCompositionRendering;
        Volatile.Write(ref _renderingHooked, 0);

        // 해제 직전에 도착한 프레임은 QueueFrameFlush 가 재구독을 건너뛰었으므로 여기서 다시 켠다
        if (Volatile.Read(ref _flushQueued) == 1 && Interlocked.CompareExchange(ref _renderingHooked, 1, 0) == 0)
            HookRendering();
    }
    private void FlushFrame()
    {
        var session = _session;
        var buffer = session?.Framebuffer;
        if (session?.IsConnected != true || buffer is null) return;
        var width = session.Width;
        var height = session.Height;
        if (width <= 0 || height <= 0 || buffer.Length < width * height * 4) return;

        int count;
        lock (_dirtyLock)
        {
            count = _pendingDirty.CopyTo(_flushRects);
            _pendingDirty.Clear();
        }

        // 빈 영역이면 아무것도 올리지 않는다
        if (count == 0) return;

        EnsureBitmap(width, height);
        if (_bitmap is null) return;

        for (var i = 0; i < count; i++)
        {
            var rect = _flushRects[i];
            // 해상도 변경 직후엔 이전 해상도 기준 좌표가 남을 수 있으므로 현재 크기로 잘라낸다
            var x = Math.Clamp(rect.X1, 0, width);
            var y = Math.Clamp(rect.Y1, 0, height);
            var w = Math.Min(rect.X2, width) - x;
            var h = Math.Min(rect.Y2, height) - y;
            if (w <= 0 || h <= 0) continue;

            try
            {
                // 원본 버퍼의 변경 영역을 비트맵 같은 위치로 한 번에 복사(스테이징 배열 경유 이중 복사 없음)
                _bitmap.WritePixels(new Int32Rect(x, y, w, h), buffer, width * 4, x, y);
                _writePixelsFailures = 0;
            }
            catch (ArgumentException ex)
            {
                // 크기 변경과 경합한 프레임은 버리고 다음 틱에 전체 갱신 — 연속 실패가 상한을 넘으면 다음 서버 갱신까지 대기
                if (++_writePixelsFailures <= MaxConsecutiveWritePixelsFailures)
                    QueueFrameFlush(0, 0, width, height);
                else if (_writePixelsFailures == MaxConsecutiveWritePixelsFailures + 1)
                    App.Log($"[콘솔 {_vmid}] 화면 업로드 연속 실패로 재시도 중단: {ex.Message}");

                return;
            }
        }
    }
    /// <summary>Tight JPEG/PNG → 프레임버퍼 직접 디코드(WIC 직접 호출, Core 는 WPF·Windows 무의존 유지).</summary>
    internal static void DecodeTightImage(
        byte[] data, int length, byte[] framebuffer, int framebufferWidth, int x, int y, int width, int height)
    {
        WicImageDecoder.Decode(data, length, framebuffer, framebufferWidth, x, y, width, height);
    }
    private void StartStatsTicker()
    {
        _statsTimer?.Stop();
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += (_, _) =>
        {
            if (_session?.IsConnected != true) return;

            StateText.Text = _session.TakeStatsText();
        };
        _statsTimer.Start();
    }
    private void UpdateButtons(bool connected)
    {
        BtnDisconnect.IsEnabled = connected;
        BtnKeys.IsEnabled = connected;
        UpdateClipboardButton(connected);
        if (!connected) ReleaseStickyKeys();
        BtnConnect.IsEnabled = !connected;
        // 연결 중이면 '연결 끊기', 끊겼으면 '재연결' — 같은 자리에서 서로 바뀐다
        BtnDisconnect.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        BtnConnect.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
    }
    /// <summary>상태 — 아래 상태 줄에 두고, 평소 상태(연결됨)가 아니면 화면 위에 잠깐 알린다(창 제목은 그대로).</summary>
    private void SetState(string text)
    {
        StateText.Text = text;
        if (!text.StartsWith(Loc.T("ConsoleWindow_M08"), StringComparison.Ordinal)) _toast.Show(text);
    }
    private async void OnReconnect(object sender, RoutedEventArgs e)
    {
        await ConnectAsync();
    }
    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        await _session.DisconnectAsync();
        SetState(Loc.T("MainViewModel_M05"));
        UpdateButtons(false);
    }
    private void OnSendClipboard(object sender, RoutedEventArgs e)
    {
        if (_session?.IsConnected != true) return;

        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            if (text.Length > 0)
            {
                _lastClipboardSync = text;
                _ = _session.SendClipboardAsync(text);
                SetState(Loc.T("ConsoleWindow_M09"));
            }
        }
        catch (Exception ex)
        {
            SetState(Loc.T("ConsoleWindow_M10", ex.Message));
        }
    }
    private void OnToggleScale(object sender, RoutedEventArgs e)
    {
        _fitMode = !_fitMode;
        ScreenImage.Stretch = _fitMode ? Stretch.Uniform : Stretch.None;
        ApplyScalingMode();
        ResizeMode = _fitMode ? ResizeMode.CanResize : ResizeMode.NoResize;
        if (!_fitMode && _fbWidth > 0) FitWindowToFramebuffer(_fbWidth, _fbHeight);
        BtnScale.Content = _fitMode ? Loc.T("ConsoleWindow_04") : Loc.T("ConsoleWindow_M11");
        QueueDesktopResize();
    }
    /// <summary>
    ///     화면 영역이 프레임버퍼와 정확히 같아지도록 창 크기를 맞춘다.
    ///     고정 오프셋 대신 실제 창 크기와 화면 영역의 차이(제목 표시줄·테두리·도구 모음·상태 표시줄)를 사용한다.
    /// </summary>
    private void FitWindowToFramebuffer(int fbWidth, int fbHeight)
    {
        UpdateLayout();
        var chromeWidth = Math.Max(0, ActualWidth - ConsoleScroll.ActualWidth);
        var chromeHeight = Math.Max(0, ActualHeight - ConsoleScroll.ActualHeight);
        var workArea = SystemParameters.WorkArea;

        Width = Math.Min(fbWidth + chromeWidth, workArea.Width);
        Height = Math.Min(fbHeight + chromeHeight, workArea.Height);
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }
    /// <summary>맞춤(축소) 모드 + 부드러운 스케일링 설정 시 Linear, 그 외(1:1 포함)는 NearestNeighbor 로 픽셀 선명도 유지.</summary>
    private void ApplyScalingMode()
    {
        RenderOptions.SetBitmapScalingMode(ScreenImage, _fitMode && SmoothScaling
            ? BitmapScalingMode.Linear
            : BitmapScalingMode.NearestNeighbor);
        RefreshCursor(); // 맞춤 전환·설정 변경 — 커서 크기·대체 커서를 다시 맞춘다
        UpdateDirectLayout();
    }
    /// <summary>[종료 | ▾] 의 종료 쪽 — 버튼의 Action 속성(Stop)은 표시 조건이라 여기서는 종료를 직접 부른다.</summary>
    private async void OnShutdownClick(object sender, RoutedEventArgs e)
    {
        if (!_guest.IsRunning)
        {
            SetState(Loc.T("GuestPower_ShutdownNeedsRunning", _vmid)); // 일시 정지 — ▾ 에서 재개·정지
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
        var label = GuestPowerRules.Label(action);
        SetState(Loc.T("ConsoleWindow_M12", label));
        await _runPower(_guest, action);
        SetState(Loc.T("ConsoleWindow_M13", label));
    }
    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var tab = IsRdp ? ConsoleSettingsTab.Rdp : ConsoleSettingsTab.Vnc;
        var dialog = new ConsoleSettingsWindow(_settings, tab) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SavedSettings is not { } saved) return;

        _settings = saved;
        ApplyScalingMode();
        ApplyDirectRendering();
        QueueDesktopResize(); // RDP 창 맞춤 해상도를 방금 켰으면 바로 맞춘다
        SetState(Loc.T("ConsoleWindow_M14"));
    }
    private (int X, int Y) ToVncCoordinates(Point position)
    {
        if (_bitmap is null || _fbWidth == 0 || _fbHeight == 0) return (0, 0);

        double x;
        double y;
        if (_fitMode)
        {
            var scale = Math.Min(ScreenImage.ActualWidth / _fbWidth, ScreenImage.ActualHeight / _fbHeight);
            var offsetX = (ScreenImage.ActualWidth - _fbWidth * scale) / 2;
            var offsetY = (ScreenImage.ActualHeight - _fbHeight * scale) / 2;
            x = (position.X - offsetX) / scale;
            y = (position.Y - offsetY) / scale;
        }
        else
        {
            x = position.X;
            y = position.Y;
        }

        return ((int)Math.Clamp(x, 0, _fbWidth - 1), (int)Math.Clamp(y, 0, _fbHeight - 1));
    }
}
