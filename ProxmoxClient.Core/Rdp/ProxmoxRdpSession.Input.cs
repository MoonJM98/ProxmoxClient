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

    public Task SendKeyAsync(int xtScanCode, bool down, int keysym = 0)
    {
        // 스캔코드가 없는 키(Pause 등 여러 바이트 시퀀스)는 RDP 로 보낼 수 없다
        if (xtScanCode == 0 || !IsConnected) return Task.CompletedTask;

        var extended = (xtScanCode & 0xFF00) == 0xE000;
        var scancode = Scancode.FromU8(extended, (byte)(xtScanCode & 0xFF));
        Queue(down ? scancode.AsOperationKeyPressed() : scancode.AsOperationKeyReleased());
        return Task.CompletedTask;
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
    ///     디스플레이 제어 채널로 해상도 변경 요청 — 채널이 아직 준비되지 않았으면 IronRDP 가 무시한다.
    ///     부르는 UI 스레드는 잠금을 기다리지 않는다(다른 스레드에서 처리).
    /// </summary>
    public void RequestDesktopSize(int width, int height)
    {
        width = ClampDimension(width);
        height = ClampDimension(height);
        _ = Task.Run(() => ResizeDesktop(width, height));
    }

    private void ResizeDesktop(int width, int height)
    {
        lock (_gate)
        {
            if (!CanSend() || (width == _width && height == _height)) return;

            try
            {
                using var outputs = _stage!.EncodedResize((uint)width, (uint)height);
                if (outputs is not null) Collect(outputs);
            }
            catch (IronRdpException ex)
            {
                // 해상도 변경을 못 해도 세션은 그대로 쓴다
                StatusChanged?.Invoke(Localization.Res.T("Rdp_Failed", ex.Inner.ToDisplay()));
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

    /// <summary>(잠금 안에서) 재활성화 중이면 붙잡아 두고 true(해제는 보낼 때), 아니면 보내고 false.</summary>
    private bool ApplyOrHold(Operation operation)
    {
        if (_reactivating && IsConnected && _heldInputs.Count < MaxHeldInputs)
        {
            _heldInputs.Add(operation);
            return true;
        }

        ApplyInput(operation);
        return false;
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
            foreach (var operation in _heldInputs) operation.Dispose();
            _heldInputs.Clear();
        }
    }

    /// <summary>(잠금 안에서) 입력 하나를 빠른 경로 PDU 로 만들어 보내기 줄에 넣는다.</summary>
    private void ApplyInput(Operation operation)
    {
        if (!CanSend()) return;

        using var events = _input!.Apply(operation);
        using var outputs = _stage!.ProcessFastpathInput(_image!, events);
        Collect(outputs);
    }

    /// <summary>입력 소비자 — 잠금 안에서 빠른 경로 PDU 로 만들어 보내기 줄에 넣는다(수신 응답과 순서가 섞이지 않게).</summary>
    private async Task InputLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var operation in _inputs.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var held = false;
                try
                {
                    lock (_gate)
                        held = ApplyOrHold(operation);
                }
                finally
                {
                    if (!held) operation.Dispose();
                }
            }
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
