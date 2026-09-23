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
    /// <summary>remote-viewer 가 이 시간 안에 종료되면 연결 실패로 보고 알린다.</summary>
    private static readonly TimeSpan SpiceEarlyExitWindow = TimeSpan.FromSeconds(5);
    /// <summary>remote-viewer 가 .vv 를 읽고 지우지 못한 경우(비정상 종료 등) 비밀번호 파일을 정리하기까지의 대기.</summary>
    private static readonly TimeSpan SpiceFileCleanupDelay = TimeSpan.FromSeconds(15);
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
    private ProxmoxVncSession? _session;
    private ConsoleSettings _settings = new();
    private DispatcherTimer? _statsTimer;
    private int _writePixelsFailures;
    public ConsoleWindow(ProxmoxApiClient api, PveResource guest, string guestTitle, GuestPowerRunner runPower,
        bool canPowerManage)
    {
        if (guest.Kind != ResourceKind.Qemu) throw new NotSupportedException(Loc.T("ConsoleWindow_VmOnly"));

        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _api = api;
        _guest = guest;
        _runPower = runPower;
        _node = guest.Node;
        _vmid = guest.VmId;
        _guestTitle = guestTitle;
        // 게스트 객체는 메인 새로고침으로 상태가 갱신되므로 전원 버튼 표시가 자동으로 따라간다
        PowerPanel.DataContext = guest;
        PowerPanel.Visibility = canPowerManage ? Visibility.Visible : Visibility.Collapsed;
        _canPowerManage = canPowerManage;
        StoppedPanel.StartRequested += OnStoppedPanelStart;
        _runState = new GuestRunStateMonitor(guest, OnGuestStoppedChanged);
        _title = Loc.T("ConsoleWindow_Header", guestTitle);
        Title = _title;
        Activated += (_, _) => InstallKeyboardHook();
        Deactivated += (_, _) => RemoveKeyboardHook();
        ScreenImage.LostMouseCapture += (_, _) => _pointerMask = 0; // 캡처를 잃으면 눌림 상태가 남지 않게
        Loaded += async (_, _) =>
        {
            _settings = await _settingsStore.LoadAsync();
            ApplyScalingMode();
            if (_runState.IsStopped)
            {
                ShowStopped(); // 정지 상태면 연결하지 않고 시작 안내
                return;
            }

            await ConnectAsync();
        };
        Closing += (_, _) =>
        {
            _closed = true;
            _runState.Dispose();
            CompositionTarget.Rendering -= OnCompositionRendering;
            _statsTimer?.Stop();
            RemoveKeyboardHook();
            _session?.Dispose();
        };
    }
    private void ShowStopped()
    {
        StoppedPanel.ShowStopped(_canPowerManage);
        SetState(Loc.T("ConsoleWindow_M01"));
        UpdateButtons(false);
    }
    /// <summary>정지↔실행 전환 — 꺼지면 안내 화면, 외부에서 켜지면 자동 연결.</summary>
    private void OnGuestStoppedChanged(bool stopped)
    {
        if (_closed) return;

        if (stopped)
            ShowStopped();
        else if (_session?.IsConnected != true) _ = AutoConnectAsync(Loc.T("ConsoleWindow_StartedConnecting"));
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
        SetState(Loc.T("ConsoleWindow_M03"));
        UpdateButtons(false);
        BtnConnect.IsEnabled = false; // 연결 시도 중 중복 재연결 방지

        var session = new ProxmoxVncSession(_api)
        {
            ImageDecoder = DecodeTightImage,
            Settings = _settings
        };

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
            FitWindowToFramebuffer(w, h);
            var tier = RenderCapability.Tier >> 16;
            var accel = tier >= 2 ? Loc.T("ConsoleWindow_AccelHardware") :
                tier == 1 ? Loc.T("ConsoleWindow_AccelPartial") : Loc.T("ConsoleWindow_AccelSoftware");
            SetState(Loc.T("ConsoleWindow_M04", w, h, accel));
            UpdateButtons(true);
            Focus();
            StartStatsTicker();
        });
        session.FrameReceived += QueueFrameFlush;
        session.CursorShape += (pixels, w, h) => Dispatcher.BeginInvoke(() => ApplyCursorShape(pixels, w, h));
        session.CursorPosition += (x, y) => Dispatcher.BeginInvoke(() => MoveCursorOverlay(x, y));
        session.Closed += ex =>
        {
            App.Log($"[콘솔 {_vmid}] 연결 종료: {(ex is null ? "정상" : ex.ToString())}");
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsCurrent()) return;

                _statsTimer?.Stop();
                SetState(ex is null ? Loc.T("ConsoleWindow_M05") : Loc.T("ConsoleWindow_M06", ex.Message));
                UpdateButtons(false);
            });
        };
        _session = session;
        try
        {
            await session.ConnectAsync(_node, ResourceKind.Qemu, _vmid);
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
    /// <summary>
    ///     Tight JPEG/PNG → 프레임버퍼 직접 디코드(WPF WIC 사용, Core 는 WPF 무의존 유지).
    ///     행 간격(stride)을 프레임버퍼 폭으로 지정해 (x, y) 위치에 곧바로 기록 — 중간 픽셀 배열·행 복사 없음.
    /// </summary>
    internal static void DecodeTightImage(
        byte[] data, int length, byte[] framebuffer, int framebufferWidth, int x, int y, int width, int height)
    {
        using var stream = new MemoryStream(data, 0, length, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new IOException(Loc.T("ConsoleWindow_TightNoFrame"));

        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgr32
            && frame.Format != PixelFormats.Bgra32)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);

        var w = Math.Min(width, frame.PixelWidth);
        var h = Math.Min(height, frame.PixelHeight);
        if (w <= 0 || h <= 0) return;

        var rowBytes = w * 4;
        var lastRowEnd = ((y + h - 1) * framebufferWidth + x) * 4 + rowBytes;
        if (x < 0 || y < 0 || x + w > framebufferWidth || lastRowEnd > framebuffer.Length)
            throw new IOException(Loc.T("ConsoleWindow_TightOutOfRange"));

        // rect 폭을 stride 로 풀 버퍼에 받은 뒤 행 단위 복사 — WIC 가 행마다 stride 전체를 쓰더라도
        // 프레임버퍼의 이웃 픽셀(rect 오른쪽·다음 행 왼쪽)을 덮지 않도록 한다
        var scratch = ArrayPool<byte>.Shared.Rent(rowBytes * h);
        try
        {
            frame.CopyPixels(new Int32Rect(0, 0, w, h), scratch, rowBytes, 0);
            for (var row = 0; row < h; row++)
                Buffer.BlockCopy(scratch, row * rowBytes, framebuffer, ((y + row) * framebufferWidth + x) * 4,
                    rowBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }
    private void ApplyCursorShape(byte[] bgra, int w, int h)
    {
        var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
        CursorImage.Source = bitmap;
        CursorImage.Width = w;
        CursorImage.Height = h;
        CursorImage.Visibility = Visibility.Visible;
    }
    private void MoveCursorOverlay(int guestX, int guestY)
    {
        if (_bitmap is null || _fbWidth == 0 || _fbHeight == 0) return;

        double scale;
        double offsetX;
        double offsetY;
        if (_fitMode)
        {
            scale = Math.Min(ScreenImage.ActualWidth / _fbWidth, ScreenImage.ActualHeight / _fbHeight);
            offsetX = (ScreenImage.ActualWidth - _fbWidth * scale) / 2;
            offsetY = (ScreenImage.ActualHeight - _fbHeight * scale) / 2;
        }
        else
        {
            scale = 1;
            offsetX = 0;
            offsetY = 0;
        }

        CursorTransform.X = offsetX + guestX * scale;
        CursorTransform.Y = offsetY + guestY * scale;
    }
    private void StartStatsTicker()
    {
        _statsTimer?.Stop();
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += (_, _) =>
        {
            if (_session?.IsConnected != true) return;

            var s = _session.TakeStats();
            StateText.Text =
                $"{s.Mbps:F1} Mbps · {s.Fps:F0} fps · Raw {s.Raw} · Tight {s.Tight} · Img {s.Image} · Copy {s.Copy}";
        };
        _statsTimer.Start();
    }
    private void UpdateButtons(bool connected)
    {
        BtnDisconnect.IsEnabled = connected;
        BtnCad.IsEnabled = connected;
        BtnClip.IsEnabled = connected;
        BtnConnect.IsEnabled = !connected;
    }
    private void SetState(string text)
    {
        StateText.Text = text;
        Title = text.StartsWith(Loc.T("ConsoleWindow_M08")) ? _title : $"{_title} — {text}";
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
    private void OnCtrlAltDel(object sender, RoutedEventArgs e)
    {
        if (_session?.IsConnected == true) _ = _session.SendCtrlAltDelAsync();
    }
    private void OnSendClipboard(object sender, RoutedEventArgs e)
    {
        if (_session?.IsConnected != true) return;

        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            if (text.Length > 0)
            {
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
        RenderOptions.SetBitmapScalingMode(ScreenImage, _fitMode && _settings.SmoothScaling
            ? BitmapScalingMode.Linear
            : BitmapScalingMode.NearestNeighbor);
    }
    private async void OnPowerAction(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject source
            || (GuestPowerVisibility.GetAction(source) is var action && action == GuestPowerAction.None))
            return;

        var label = GuestPowerRules.Label(action);
        SetState(Loc.T("ConsoleWindow_M12", label));
        await _runPower(_guest, action);
        SetState(Loc.T("ConsoleWindow_M13", label));
    }
    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new ConsoleSettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SavedSettings is not { } saved) return;

        _settings = saved;
        ApplyScalingMode();
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
