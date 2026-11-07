using System.Threading.Channels;
using Devolutions.IronRdp;

namespace ProxmoxClient.Core.Rdp;

/// <summary>
///     입력 — 콘솔 창이 VNC 와 같은 모양(XT 스캔코드, RFB 버튼 마스크)으로 보내면 RDP 빠른 경로 입력으로 바꾼다.
///     부르는 쪽(키보드 훅·마우스 처리, UI 스레드)은 대기열에 넣기만 하고 바로 돌아간다 — 화면 디코딩이 잡고 있는
///     잠금을 기다리다 훅 제한 시간을 넘기면 Windows 가 훅을 떼어 버리기 때문이다. 한 소비자가 순서대로 처리한다.
/// </summary>
public sealed partial class ProxmoxRdpSession
{
    private const int ButtonLeft = 1;
    private const int ButtonMiddle = 2;
    private const int ButtonRight = 4;
    private const int WheelUp = 8;
    private const int WheelDown = 16;
    private const int WheelLeft = 32;
    private const int WheelRight = 64;

    /// <summary>휠 한 칸(Windows WHEEL_DELTA).</summary>
    private const short WheelStep = 120;

    private const int ScanControlL = 0x1D;
    private const int ScanAltL = 0x38;
    private const int ScanDelete = 0xE053;

    private readonly Channel<Operation> _inputs =
        Channel.CreateUnbounded<Operation>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>재활성화 중에 붙잡아 둘 입력의 상한 — 끝나지 않는 재활성화에 마우스 이동이 끝없이 쌓이지 않게.</summary>
    private const int MaxHeldInputs = 4096;

    /// <summary>
    ///     재활성화(해상도 변경) 중에 들어온 입력 — 끝나면 순서대로 보낸다(잠금 안에서만). 버리면 그 사이 뗀 키·버튼이
    ///     게스트에 눌린 채 남는다(Shift 가 계속 눌림 등). 버튼 마스크는 넣을 때 이미 바뀌어 다시 보내지도 않는다.
    /// </summary>
    private readonly List<Operation> _heldInputs = [];

    /// <summary>마지막으로 넣은 버튼 마스크(휠 제외) — 바뀐 버튼만 누름/뗌으로 보낸다. 입력 스레드(UI) 전용.</summary>
    private int _buttons;

    /// <summary><see cref="_shortcutModifiers" />·<see cref="_keysymRoutes" /> 잠금(UI 스레드와 Ctrl+Alt+Del 등).</summary>
    private readonly object _keyState = new();

    /// <summary>지금 눌려 있는 Ctrl·Alt·Win 스캔코드.</summary>
    private readonly HashSet<int> _shortcutModifiers = [];

    /// <summary>눌린 keysym 키를 어떻게 보냈는지 — 뗄 때 같은 방법으로 뗀다.</summary>
    private readonly Dictionary<int, (RdpKeyRoute Route, int Code)> _keysymRoutes = [];

    /// <summary>
    ///     키 하나 — 스캔코드가 있으면 그대로(물리 키), 없으면 keysym 으로 고른다(<see cref="RdpKeyMap.Resolve" />):
    ///     특수 키는 스캔코드, 글자(한글 등)는 유니코드 입력, Ctrl·Alt·Win 을 누른 채면 영문·숫자는 US 스캔코드(단축키).
    ///     뗄 때는 누를 때 고른 방법을 그대로 쓴다(그 사이 수식 키가 바뀌어도 눌린 채 남지 않게).
    /// </summary>
    public Task SendKeyAsync(int xtScanCode, bool down, int keysym = 0)
    {
        if (!IsConnected) return Task.CompletedTask;

        if (xtScanCode != 0)
        {
            QueueScancode(xtScanCode, down);
            return Task.CompletedTask;
        }

        (RdpKeyRoute Route, int Code) key;
        lock (_keyState)
        {
            if (down || !_keysymRoutes.Remove(keysym, out key))
                key = RdpKeyMap.Resolve(keysym, _shortcutModifiers.Count > 0);
            if (down && key.Route != RdpKeyRoute.Drop) _keysymRoutes[keysym] = key;
        }

        switch (key.Route)
        {
            case RdpKeyRoute.Scancode:
                QueueScancode(key.Code, down);
                break;
            case RdpKeyRoute.Unicode:
                var ch = Devolutions.IronRdp.Char.New((uint)key.Code);
                Queue(down ? ch.AsOperationUnicodeKeyPressed() : ch.AsOperationUnicodeKeyReleased());
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>XT 스캔코드 키를 대기열에 넣고, 단축키 수식 키(Ctrl·Alt·Win)가 눌려 있는지 기억한다.</summary>
    private void QueueScancode(int xtScanCode, bool down)
    {
        if (RdpKeyMap.IsShortcutModifier(xtScanCode))
            lock (_keyState)
            {
                if (down) _shortcutModifiers.Add(xtScanCode);
                else _shortcutModifiers.Remove(xtScanCode);
            }

        var extended = (xtScanCode & 0xFF00) == 0xE000;
        var scancode = Scancode.FromU8(extended, (byte)(xtScanCode & 0xFF));
        Queue(down ? scancode.AsOperationKeyPressed() : scancode.AsOperationKeyReleased());
    }

    public Task SendPointerAsync(int buttonMask, int x, int y)
    {
        if (!IsConnected) return Task.CompletedTask;

        Queue(MousePosition.New((ushort)Math.Clamp(x, 0, ushort.MaxValue),
            (ushort)Math.Clamp(y, 0, ushort.MaxValue)).AsMoveOperation());
        QueueButton(buttonMask, ButtonLeft, MouseButtonType.Left);
        QueueButton(buttonMask, ButtonMiddle, MouseButtonType.Middle);
        QueueButton(buttonMask, ButtonRight, MouseButtonType.Right);
        if ((buttonMask & WheelUp) != 0) Queue(WheelRotations.New(true, WheelStep).AsOperation());
        if ((buttonMask & WheelDown) != 0) Queue(WheelRotations.New(true, -WheelStep).AsOperation());
        // 가로 휠 — RDP 는 양수가 오른쪽(MOUSEHWHEEL)
        if ((buttonMask & WheelLeft) != 0) Queue(WheelRotations.New(false, (short)-WheelStep).AsOperation());
        if ((buttonMask & WheelRight) != 0) Queue(WheelRotations.New(false, WheelStep).AsOperation());
        return Task.CompletedTask;
    }

    public async Task SendCtrlAltDelAsync()
    {
        await SendKeyAsync(ScanControlL, true).ConfigureAwait(false);
        await SendKeyAsync(ScanAltL, true).ConfigureAwait(false);
        await SendKeyAsync(ScanDelete, true).ConfigureAwait(false);
        await SendKeyAsync(ScanDelete, false).ConfigureAwait(false);
        await SendKeyAsync(ScanAltL, false).ConfigureAwait(false);
        await SendKeyAsync(ScanControlL, false).ConfigureAwait(false);
    }

    /// <summary>RDP 클립보드는 아직 연결하지 않는다.</summary>
    public Task SendClipboardAsync(string text)
    {
        return Task.CompletedTask;
    }

    public Task RequestClipboardAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     디스플레이 제어 채널로 해상도 변경 요청(크기는 짝수 폭·200~8192 로 맞춘다). 마지막 요청만 기억한다 —
    ///     연결 전이면 처음 해상도로 쓰고, 채널이 아직 준비되지 않았으면 준비된 뒤(수신 처리마다 다시 시도) 보낸다.
    ///     부르는 UI 스레드는 잠금을 기다리지 않는다(다른 스레드에서 처리). 어느 스레드에서 불러도 된다.
    /// </summary>
    public void RequestDesktopSize(int width, int height)
    {
        Interlocked.Exchange(ref _pendingSize, PackSize(ClampDimension(width), ClampDimension(height)));
        if (!IsConnected) return;

        _ = Task.Run(() =>
        {
            TryApplyPendingResize();
        });
    }

    /// <summary>요청된(아직 보내지 않은) 해상도 — 0 이면 없음. 상위 32비트 가로, 하위 세로.</summary>
    private long _pendingSize;

    /// <summary>해상도 변경이 오류로 실패하면 다시 해 보기까지 기다리는 시간과 횟수 상한(채널 준비 중일 때).</summary>
    private static readonly TimeSpan ResizeRetryDelay = TimeSpan.FromMilliseconds(500);

    private const int MaxResizeFailures = 10;

    /// <summary>(잠금 안에서) 다음 해상도 변경 시도 시각(TickCount64)·연속 실패 횟수.</summary>
    private long _resizeRetryAt;

    private int _resizeFailures;

    private static long PackSize(int width, int height)
    {
        return ((long)width << 32) | (uint)height;
    }

    private static (int Width, int Height) UnpackSize(long packed)
    {
        return ((int)(packed >> 32), (int)packed);
    }

    /// <summary>
    ///     기억해 둔 해상도 요청을 보낸다(잠금을 잡는다 — UI 스레드에서 부르지 않는다). 채널이 준비되지 않았으면
    ///     IronRDP 가 null 을 주므로 요청을 그대로 두고 다음 수신 처리에서 다시 시도한다.
    /// </summary>
    private void TryApplyPendingResize()
    {
        var packed = Volatile.Read(ref _pendingSize);
        if (packed == 0) return;

        lock (_gate)
        {
            if (!CanSend() || Environment.TickCount64 < _resizeRetryAt) return; // 수신 처리에서 다시 시도한다
            // 디스플레이 제어 채널이 아직 열리지 않았다(서버가 능력을 보내기 전) — 요청은 두고 다음 수신 처리에서 다시
            if (_stage!.DisplayControlState != DisplayControlState.Ready) return;

            var (width, height) = UnpackSize(packed);
            if (width == _width && height == _height)
            {
                Interlocked.CompareExchange(ref _pendingSize, 0, packed);
                return;
            }

            try
            {
                using var outputs = _stage!.EncodedResize((uint)width, (uint)height);
                if (outputs is null) return; // 채널이 막 닫혔다 — 다시 열리면 다시 시도한다
                Interlocked.CompareExchange(ref _pendingSize, 0, packed); // 그사이 새 요청이 왔으면 그것은 남긴다
                _resizeFailures = 0;
                Collect(outputs);
            }
            catch (IronRdpException ex)
            {
                // 채널이 아직 준비 중일 수 있다 — 잠시 뒤 몇 번 더 해 보고, 그래도 안 되면 이 요청은 버린다.
                // 해상도 변경을 못 해도 세션은 그대로 쓴다
                _resizeRetryAt = Environment.TickCount64 + (long)ResizeRetryDelay.TotalMilliseconds;
                if (++_resizeFailures < MaxResizeFailures) return;

                _resizeFailures = 0;
                Interlocked.CompareExchange(ref _pendingSize, 0, packed);
                System.Diagnostics.Trace.TraceWarning($"[RDP] 해상도 변경 실패 {width}x{height}: {ex.Inner.ToDisplay()}");
            }
        }
    }

    /// <summary>(잠금 안에서) 연결돼 있고 재활성화 중이 아닐 때만 입력을 보낸다.</summary>
    private bool CanSend()
    {
        return IsConnected && !_reactivating && _stage is not null && _image is not null && _input is not null;
    }

    private void Queue(Operation operation)
    {
        _inputs.Writer.TryWrite(operation);
    }

    private void QueueButton(int mask, int bit, MouseButtonType type)
    {
        var now = (mask & bit) != 0;
        if (now == ((_buttons & bit) != 0)) return;

        var button = MouseButton.New(type);
        Queue(now ? button.AsOperationMouseButtonPressed() : button.AsOperationMouseButtonReleased());
        _buttons = now ? _buttons | bit : _buttons & ~bit;
    }

    /// <summary>(잠금 안에서) 재활성화 중이면 붙잡아 두고, 아니면 보낸다.</summary>
    private void ApplyOrHold(Operation operation)
    {
        if (_reactivating && IsConnected && _heldInputs.Count < MaxHeldInputs)
        {
            _heldInputs.Add(operation);
            return;
        }

        ApplyInput(operation);
    }

    /// <summary>(잠금 안에서) 재활성화가 끝났다 — 붙잡아 둔 입력을 순서대로 보낸다. 닫는 중이면 버리기만 한다.</summary>
    private void FlushHeldInputs()
    {
        try
        {
            foreach (var operation in _heldInputs) ApplyInput(operation);
        }
        finally
        {
            _heldInputs.Clear();
        }
    }

    /// <summary>(잠금 안에서) 입력 하나를 빠른 경로 PDU 로 만들어 보내기 줄에 넣는다.</summary>
    private void ApplyInput(Operation operation)
    {
        if (!CanSend()) return;

        var events = _input!.Apply(operation);
        using var outputs = _stage!.ProcessFastpathInput(_image!, events);
        Collect(outputs);
    }

    /// <summary>입력 소비자 — 잠금 안에서 빠른 경로 PDU 로 만들어 보내기 줄에 넣는다(수신 응답과 순서가 섞이지 않게).</summary>
    private async Task InputLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var operation in _inputs.Reader.ReadAllAsync(token).ConfigureAwait(false))
                lock (_gate)
                    ApplyOrHold(operation);
        }
        catch (OperationCanceledException)
        {
            // 세션 종료
        }
        catch (IronRdpException ex)
        {
            // 입력을 만들지 못했다 — 세션이 망가진 것이므로 끊고 알린다(수신 루프가 Closed 를 낸다)
            StatusChanged?.Invoke(Localization.Res.T("Rdp_Failed", ex.Inner.ToDisplay()));
            AbortSocket();
        }
    }
}
