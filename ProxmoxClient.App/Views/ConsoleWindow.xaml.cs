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

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;

        _hookProc = KeyboardHookCallback;
        _keyboardHook = SetWindowsHookEx(WhKeyboardLL, _hookProc, GetModuleHandle(null), 0);
    }

    private void RemoveKeyboardHook()
    {
        if (_keyboardHook == IntPtr.Zero) return;

        UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
        _heldModifiers.Clear();
        ReleasePressedKeys();
    }

    /// <summary>
    ///     포커스를 잃는 순간 눌려 있던 키의 key-up 을 게스트에 보낸다.
    ///     (Alt+Tab 등으로 떠나면 up 이벤트가 훅에 오지 않아 게스트에서 키가 계속 눌린 상태로 남는다)
    /// </summary>
    private void ReleasePressedKeys()
    {
        foreach (var (xtScanCode, keysym) in _pressedKeys.Values) SendGuestKey(xtScanCode, keysym, false);

        _pressedKeys.Clear();
    }

    private void SendGuestKey(int xtScanCode, int keysym, bool down)
    {
        if (_session?.IsConnected == true) _ = _session.SendKeyAsync(xtScanCode, down, keysym);
    }

    /// <summary>
    ///     저수준 훅 정보 → XT 스캔코드(확장 키는 0xE0nn). 0 이면 스캔코드 전송 불가(keysym 경로 사용).
    /// </summary>
    private static int ToXtScanCode(int vk, KbdLlHookStruct hook)
    {
        const int VkPause = 0x13;
        const int VkNumLock = 0x90;
        const int VkRShift = 0xA1;

        if (vk == VkPause) return 0; // E1 1D 45 멀티바이트 시퀀스 — keysym 으로 전송

        var scan = (int)hook.ScanCode & 0xFF;
        if (scan == 0) return 0;

        // NumLock·RShift 는 훅이 extended 로 보고하지만 XT 에서는 비확장 코드
        if (vk is VkNumLock or VkRShift) return scan;

        return (hook.Flags & LlkhfExtended) != 0 ? 0xE000 | scan : scan;
    }

    /// <summary>
    ///     저수준 키보드 훅 — 창이 포어그라운드일 때 모든 키를 가로채 VM에 전달.
    ///     IME(한/영), Win, Alt+Tab 등 호스트 OS에 도달하지 않게 차단.
    ///     Alt+F4만 예외로 통과.
    /// </summary>
    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_keyboardHook, code, wParam, lParam);

        var msg = wParam.ToInt32();
        var down = msg is WmKeydown or WmSyskeydown;
        var up = msg is WmKeyup or WmSyskeyup;
        if (!down && !up) return CallNextHookEx(_keyboardHook, code, wParam, lParam);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (GetForegroundWindow() != hwnd) return CallNextHookEx(_keyboardHook, code, wParam, lParam);

        // PtrToStructure<T> 는 내부적으로 박싱 할당 — 키 입력마다 호출되는 훅이므로 필요한 필드만 오프셋으로 직접 읽는다
        var hook = new KbdLlHookStruct
        {
            VkCode = (uint)Marshal.ReadInt32(lParam, 0),
            ScanCode = (uint)Marshal.ReadInt32(lParam, 4),
            Flags = (uint)Marshal.ReadInt32(lParam, 8)
        };
        var vk = (int)hook.VkCode;

        // Alt+F4: 창 닫기 허용
        if (vk == 0x73 && _heldModifiers.Contains(0xA4)) return CallNextHookEx(_keyboardHook, code, wParam, lParam);

        var isModifier = vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
        if (isModifier)
        {
            if (down)
                _heldModifiers.Add(vk);
            else
                _heldModifiers.Remove(vk);
        }

        // Ctrl+Alt+Del: Windows SAS를 차단하고 VM에 직접 전송
        if (vk == 0x2E && down && _heldModifiers.Contains(0xA2) && _heldModifiers.Contains(0xA4))
        {
            if (_session?.IsConnected == true) _ = _session.SendCtrlAltDelAsync();
            return 1;
        }

        var shift = _heldModifiers.Contains(0xA0) || _heldModifiers.Contains(0xA1);
        var keysym = vk == 0x0D && (hook.Flags & LlkhfExtended) != 0
            ? KeysymKpEnter // 넘패드 Enter 는 VK 가 같고 extended 플래그로만 구분된다
            : VirtualKeyToKeysym(vk, shift ? ModifierKeys.Shift : ModifierKeys.None);

        var xtScanCode = ToXtScanCode(vk, hook);

        if (down)
        {
            if (keysym != 0 || xtScanCode != 0)
            {
                _pressedKeys[vk] = (xtScanCode, keysym);
                SendGuestKey(xtScanCode, keysym, true);
            }
        }
        else if (_pressedKeys.Remove(vk, out var pressed))
        {
            SendGuestKey(pressed.XtScanCode, pressed.Keysym, false);
        }
        else if (keysym != 0 || xtScanCode != 0)
        {
            SendGuestKey(xtScanCode, keysym, false);
        }

        if (keysym == 0 && xtScanCode == 0)
        {
            // 훅 콜백 안에서 동기 파일 IO 금지 — 지연되면 Windows 가 훅을 조용히 제거한다
            var flags = hook.Flags;
            _ = Task.Run(() => App.Log($"[훅] 매핑 없음 VK=0x{vk:X2} flags=0x{flags:X} down={down}"));
        }

        return 1;
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

    private async void OnOpenSpice(object sender, RoutedEventArgs e)
    {
        await TryOpenSpiceAsync();
    }

    private async Task TryOpenSpiceAsync()
    {
        // 1) SPICE 클라이언트 확인 — 없으면 설치할지 먼저 묻는다(SPICE 티켓은 짧게만 유효하므로 설치가 끝난 뒤 발급)
        var viewer = EnsureSpiceViewer();
        if (viewer is null) return;

        BtnSpice.IsEnabled = false;
        string? vvPath = null;
        try
        {
            SetState(Loc.T("ConsoleWindow_M15"));
            var spice = await _api.GetSpiceProxyAsync(_node, _vmid);

            // 비밀번호가 담긴 파일 — 호출마다 고유 이름(여러 콘솔 동시 실행 시 덮어쓰기 방지)
            vvPath = Path.Combine(Path.GetTempPath(), $"pve-spice-{_vmid}-{Guid.NewGuid():N}.vv");
            await File.WriteAllTextAsync(vvPath, spice.ToVvFile(_guestTitle));

            var startInfo = new ProcessStartInfo(viewer) { UseShellExecute = false };
            startInfo.ArgumentList.Add(vvPath); // 경로 따옴표·공백 처리를 런타임에 맡김
            var process = Process.Start(startInfo)
                          ?? throw new InvalidOperationException(Loc.T("ConsoleWindow_RemoteViewerFailed"));

            SetState(Loc.T("ConsoleWindow_M16", Path.GetFileName(viewer)));
            _ = WatchSpiceViewerAsync(process, vvPath);
            vvPath = null; // 정리는 감시 작업이 맡는다
        }
        catch (ProxmoxApiException ex)
        {
            SetState(Loc.T("ConsoleWindow_M17", ex.Message));
        }
        catch (Exception ex) when (ex is CertificateTrustException or IOException or UnauthorizedAccessException
                                       or Win32Exception or InvalidOperationException)
        {
            SetState(Loc.T("ConsoleWindow_M18", ex.Message));
        }
        finally
        {
            if (vvPath is not null) TryDeleteFile(vvPath); // 실행 전에 실패 — 비밀번호 파일을 남기지 않는다

            BtnSpice.IsEnabled = true;
        }
    }

    /// <summary>remote-viewer 경로. 없으면 설치 여부를 묻고 설치 창을 연다. 끝내 없으면 null(상태 표시).</summary>
    private string? EnsureSpiceViewer()
    {
        var viewer = RemoteViewerLocator.Detect();
        if (viewer is not null) return viewer;

        var answer = ThemedMessageBox.Show(this,
            Loc.T("ConsoleWindow_M19"),
            Loc.T("ConsoleWindow_M20"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            SetState(Loc.T("ConsoleWindow_M21"));
            return null;
        }

        new VirtViewerSetupWindow { Owner = this }.ShowDialog();
        viewer = RemoteViewerLocator.Detect();
        if (viewer is null) SetState(Loc.T("ConsoleWindow_M22"));

        return viewer;
    }

    /// <summary>remote-viewer 가 곧바로 종료되면(접속 실패·파일 오류) 알리고, 남은 .vv(비밀번호 포함)를 정리한다.</summary>
    private async Task WatchSpiceViewerAsync(Process process, string vvPath)
    {
        using (process)
        {
            using var earlyExit = new CancellationTokenSource(SpiceEarlyExitWindow);
            try
            {
                await process.WaitForExitAsync(earlyExit.Token);
                if (!_closed && process.ExitCode != 0) SetState(Loc.T("ConsoleWindow_M23", process.ExitCode));
            }
            catch (OperationCanceledException)
            {
                // 계속 실행 중 — 정상
            }
        }

        await Task.Delay(SpiceFileCleanupDelay);
        TryDeleteFile(vvPath); // 정상이면 remote-viewer 가 이미 지웠다(delete-this-file)
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log($"[SPICE] 임시 연결 파일 삭제 실패: {ex.Message}");
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        SendKey(e, true);
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        SendKey(e, false);
    }

    private void SendKey(KeyEventArgs e, bool down)
    {
        if (_session?.IsConnected != true) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return; // Alt+F4 창 닫기 허용

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return;

        var scan = MapVirtualKey((uint)virtualKey, MapVkVkToVscEx);
        if (scan == 0) return;

        var keysym = VirtualKeyToKeysym(virtualKey, Keyboard.Modifiers);
        _ = _session.SendKeyAsync((int)(scan & 0xFFFF), down, keysym);
        e.Handled = true;
    }

    /// <summary>Windows VK → X11 keysym 기본 매핑 (QEMU가 LED 상태 추적에 사용).</summary>
    private static int VirtualKeyToKeysym(int vk, ModifierKeys modifiers)
    {
        if (vk is >= 0x41 and <= 0x5A) return modifiers.HasFlag(ModifierKeys.Shift) ? vk : vk + 0x20;

        if (vk is >= 0x30 and <= 0x39) return vk;

        if (vk is >= 0x60 and <= 0x69) return vk - 0x60 + 0xFFB0; // KP_0..KP_9 — 일반 숫자로 보내면 넘패드 키로 인식되지 않는다

        return vk switch
        {
            0x2D => 0xFF63, // Insert
            0x2C => 0xFF61, // PrintScreen
            0x91 => 0xFF14, // ScrollLock
            0x13 => 0xFF13, // Pause
            0x5D => 0xFF67, // Apps(메뉴)
            0x6A => 0xFFAA, // KP_Multiply
            0x6B => 0xFFAB, // KP_Add
            0x6C => 0xFFAC, // KP_Separator
            0x6D => 0xFFAD, // KP_Subtract
            0x6E => 0xFFAE, // KP_Decimal
            0x6F => 0xFFAF, // KP_Divide
            0xE2 => 0x3C, // OEM_102 (<>)
            0x20 => 0x20, // Space
            0x0D => 0xFF0D, // Enter
            0x09 => 0xFF09, // Tab
            0x1B => 0xFF1B, // Escape
            0x08 => 0xFF08, // BackSpace
            0x2E => 0xFFFF, // Delete
            0x24 => 0xFF50, // Home
            0x23 => 0xFF57, // End
            0x25 => 0xFF51, // Left
            0x26 => 0xFF52, // Up
            0x27 => 0xFF53, // Right
            0x28 => 0xFF54, // Down
            0x70 => 0xFFBE, // F1
            0x71 => 0xFFBF, // F2
            0x72 => 0xFFC0, // F3
            0x73 => 0xFFC1, // F4
            0x74 => 0xFFC2, // F5
            0x75 => 0xFFC3, // F6
            0x76 => 0xFFC4, // F7
            0x77 => 0xFFC5, // F8
            0x78 => 0xFFC6, // F9
            0x79 => 0xFFC7, // F10
            0x7A => 0xFFC8, // F11
            0x7B => 0xFFC9, // F12
            0xA0 => 0xFFE1, // LShift
            0xA1 => 0xFFE2, // RShift
            0xA2 => 0xFFE3, // LCtrl
            0xA3 => 0xFFE4, // RCtrl
            0xA4 => 0xFFE9, // LAlt
            0xA5 => 0xFFEA, // RAlt
            0x21 => 0xFF55, // PageUp
            0x22 => 0xFF56, // PageDown
            0x90 => 0xFF7F, // NumLock
            0x14 => 0xFFE5, // CapsLock
            0x15 => 0xFFEA, // 한/영 → RAlt (한국어 키보드에서 물리적으로 RAlt 자리)
            0x19 => 0xFFE4, // 한자 → RCtrl (물리적으로 RCtrl 자리)
            0x1C => 0xFF23, // 변환(Henkan)
            0x1D => 0xFF22, // 무변환(Muhenkan)
            0x1E => 0xFF0D,
            0x5B => 0xFFEB,
            0x5C => 0xFFEC,
            0xC0 => 0x60,
            0xBD => 0x2D,
            0xBB => 0x3D,
            0xBC => 0x2C,
            0xBE => 0x2E,
            0xBF => 0x2F,
            0xDC => 0x5C,
            0xBA => 0x3B,
            0xDE => 0x27,
            0xDD => 0x5D,
            0xDB => 0x5B,
            _ => 0
        };
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

    private void SendPointer(int extraMask)
    {
        if (_session?.IsConnected != true) return;

        var pos = ToVncCoordinates(Mouse.GetPosition(ScreenImage));
        _ = _session.SendPointerAsync(extraMask, pos.X, pos.Y);
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        ScreenImage.CaptureMouse();
        var mask = _pointerMask | ButtonMask(e.ChangedButton);
        _pointerMask = mask;
        SendPointer(mask);
        e.Handled = true;
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        var mask = _pointerMask & ~ButtonMask(e.ChangedButton);
        _pointerMask = mask;
        SendPointer(mask);
        if (mask == 0) ScreenImage.ReleaseMouseCapture();

        e.Handled = true;
    }

    private void OnImageMouseEnter(object sender, MouseEventArgs e)
    {
        if (_session?.IsConnected == true) SendPointer(_pointerMask); // 진입 시점 위치 동기화
    }

    private void OnImageMouseLeave(object sender, MouseEventArgs e)
    {
        // 영역을 벗어나도 캡처 중(드래그)이면 계속 전송 — CaptureMouse 유지
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        // VNC는 절대 좌표 방식 — 호버 이동도 항상 전송해야 게스트 커서가 따라온다
        SendPointer(_pointerMask);
        e.Handled = true;
    }

    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        const int wheelUp = 8;
        const int wheelDown = 16;
        var wheel = e.Delta > 0 ? wheelUp : wheelDown;
        SendPointer(_pointerMask | wheel);
        SendPointer(_pointerMask);
        e.Handled = true;
    }

    private static int ButtonMask(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => 1,
            MouseButton.Middle => 2,
            MouseButton.Right => 4,
            // RFB 마스크 8/16 은 휠 위/아래 — 뒤로/앞으로 버튼을 여기에 매핑하면 스크롤로 동작하므로 전송하지 않는다
            _ => 0
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }
}