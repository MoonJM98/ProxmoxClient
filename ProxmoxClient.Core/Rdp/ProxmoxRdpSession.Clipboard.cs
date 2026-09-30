using System.Diagnostics;
using Devolutions.IronRdp;

namespace ProxmoxClient.Core.Rdp;

/// <summary>
///     RDP 클립보드(cliprdr) — IronRDP 의 Windows 백엔드가 숨은 창으로 PC 클립보드를 지켜보다가 양방향으로 자동 공유한다
///     (텍스트·이미지 등 표준 형식). 그 창은 만든 스레드의 메시지 루프에서 돌아야 하므로 UI 스레드에서 만들고 정리한다.
///     백엔드가 보낸 요청(복사 알림·붙여넣기 요청·데이터)은 짧은 주기로 꺼내 RDP PDU 로 바꿔 보낸다.
///     정리 순서: 폴링 루프가 끝난 뒤에만 백엔드를 해제한다(해제와 동시에 꺼내면 해제된 메모리를 읽는다).
/// </summary>
public sealed partial class ProxmoxRdpSession
{
    /// <summary>백엔드 요청을 꺼내는 주기 — 붙여넣기 지연이 느껴지지 않을 만큼 짧게.</summary>
    private static readonly TimeSpan ClipboardPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>재활성화 중 만든 클립보드 PDU — 끝난 뒤 보낸다(버리면 클립보드 채널 준비·붙여넣기가 멈춘다).</summary>
    private readonly List<byte[]> _heldClipboardFrames = [];

    private WinCliprdr? _clipboard;
    private SynchronizationContext? _clipboardContext;
    private Task? _clipboardTask;
    private int _clipboardThreadId;

    /// <summary>백엔드의 숨은 창 — 정리할 때 직접 없앤다(IronRDP 해제는 창을 없애지 않아 창·내부 상태가 남는다).</summary>
    private IntPtr _clipboardWindow;

    /// <summary>클립보드를 공유한다(연결 전에 정한다).</summary>
    public bool ShareClipboard { get; set; } = true;

    /// <summary>클립보드 공유가 켜져 동작 중이다.</summary>
    public bool ClipboardActive => _clipboard is not null;

    /// <summary>
    ///     (연결 시작 시 부르는 스레드에서) 클립보드 백엔드를 만든다 — 메시지 루프가 있는 UI 스레드가 아니면 만들지 않는다.
    /// </summary>
    private CliprdrBackendFactory? CreateClipboard()
    {
        if (!ShareClipboard || SynchronizationContext.Current is not { } context) return null;

        try
        {
            var before = ClipboardWindows.Find();
            _clipboard = WinCliprdr.New();
            _clipboardWindow = ClipboardWindows.Find().Except(before).FirstOrDefault();
            _clipboardContext = context;
            _clipboardThreadId = Environment.CurrentManagedThreadId;
            return _clipboard.BackendFactory();
        }
        catch (IronRdpException ex)
        {
            StatusChanged?.Invoke(Localization.Res.T("Rdp_Failed", ex.Inner.ToDisplay()));
            _clipboard = null;
            return null;
        }
    }

    private async Task ClipboardLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _clipboard is { } clipboard)
            {
                while (clipboard.NextClipboardMessage() is { } message)
                    using (message)
                        SubmitClipboard(message);

                await Task.Delay(ClipboardPollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 세션 종료
        }
        catch (IronRdpException ex)
        {
            Trace.TraceWarning($"[RDP 클립보드] 요청 꺼내기 실패 — 클립보드 공유 중단: {ex.Inner.ToDisplay()}");
        }
        finally
        {
            DisposeClipboard();
        }
    }

    /// <summary>백엔드 요청 하나를 RDP PDU 로 만들어 보내기 줄에 넣는다 — 하나가 실패해도 다음 요청은 처리한다.</summary>
    private void SubmitClipboard(ClipboardMessage message)
    {
        lock (_gate)
        {
            if (!IsConnected || _stage is null) return;

            try
            {
                using var frame = ToClipboardFrame(_stage, message);
                if (frame is null || (int)frame.GetSize() == 0) return;

                var bytes = Utils.VecU8ToByte(frame);
                if (_reactivating) _heldClipboardFrames.Add(bytes);
                else _channel?.Enqueue(bytes);
            }
            catch (IronRdpException ex)
            {
                Trace.TraceWarning($"[RDP 클립보드] {message.GetMessageType()} 처리 실패: {ex.Inner.ToDisplay()}");
            }
        }
    }

    /// <summary>종류별 내용은 그 종류일 때만 있다 — 꺼낸 내용 객체도 바로 해제한다.</summary>
    private static VecU8? ToClipboardFrame(ActiveStage stage, ClipboardMessage message)
    {
        switch (message.GetMessageType())
        {
            case ClipboardMessageType.SendInitiateCopy:
                using (var formats = message.GetSendInitiateCopy()!)
                    return stage.InitiateClipboardCopy(formats);
            case ClipboardMessageType.SendInitiatePaste:
                using (var format = message.GetSendInitiatePaste()!)
                    return stage.InitiateClipboardPaste(format);
            case ClipboardMessageType.SendFormatData:
                using (var data = message.GetSendFormatData()!)
                    return stage.SubmitClipboardFormatData(data);
            default:
                return null;
        }
    }

    /// <summary>(잠금 안에서) 재활성화가 끝났다 — 그동안 모아 둔 클립보드 PDU 를 보낸다.</summary>
    private void FlushHeldClipboardFrames()
    {
        foreach (var frame in _heldClipboardFrames) _channel?.Enqueue(frame);
        _heldClipboardFrames.Clear();
    }

    /// <summary>
    ///     정상 종료 전에 숨은 창을 먼저 없앤다(UI 스레드에서) — 게스트에서 복사해 둔 내용이 PC 클립보드에 있으면 창이
    ///     없어지며 실제 데이터를 받아 넣으므로(연결이 살아 있어야 한다) 콘솔을 닫은 뒤에도 붙여 넣을 수 있다.
    /// </summary>
    private Task ReleaseClipboardWindowAsync(bool sessionAlive)
    {
        if (_clipboardContext is not { } context || _clipboardWindow == IntPtr.Zero) return Task.CompletedTask;

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            DestroyClipboardWindow(sessionAlive);
            done.TrySetResult();
        }, null);
        return done.Task;
    }

    /// <summary>
    ///     (UI 스레드에서) 숨은 창을 없앤다. 연결이 끊겼는데 게스트 형식을 PC 클립보드에 걸어 두고 있으면 먼저 비운다 —
    ///     그대로 없애면 받을 수 없는 데이터를 형식마다 10초씩 기다리고, 두면 붙여 넣을 때마다 앱이 10초씩 멈춘다.
    /// </summary>
    private void DestroyClipboardWindow(bool sessionAlive)
    {
        var window = Interlocked.Exchange(ref _clipboardWindow, IntPtr.Zero);
        if (window == IntPtr.Zero) return;

        if (!sessionAlive) ClipboardWindows.EmptyIfOwner(window);
        ClipboardWindows.Destroy(window); // 창이 없어질 때 백엔드 내부 상태도 풀린다(WM_DESTROY)
    }

    /// <summary>백엔드를 만든 UI 스레드에서 창을 정리하고 해제한다.</summary>
    private void DisposeClipboard()
    {
        var clipboard = Interlocked.Exchange(ref _clipboard, null);
        if (clipboard is null) return;

        void Release()
        {
            DestroyClipboardWindow(sessionAlive: false);
            clipboard.Dispose();
        }

        var context = _clipboardContext;
        if (context is null || Environment.CurrentManagedThreadId == _clipboardThreadId) Release();
        else context.Post(_ => Release(), null);
    }
}
