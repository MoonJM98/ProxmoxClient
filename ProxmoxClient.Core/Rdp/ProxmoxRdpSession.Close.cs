using System.Net.WebSockets;
using Devolutions.IronRdp;

namespace ProxmoxClient.Core.Rdp;

/// <summary>
///     닫기 — 정상 종료(남은 입력 → 클립보드 정리 → 종료 알림)와 즉시 중단. 잠금은 UI 스레드가 아닌 곳에서만 잡는다.
/// </summary>
public sealed partial class ProxmoxRdpSession
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private Task? _inputTask;

    /// <summary>
    ///     정상 종료 — 아직 보내지 않은 입력(창을 닫을 때 떼는 키 등)을 순서대로 다 보내고, 게스트에서 복사해 둔 내용을
    ///     PC 클립보드에 실제로 넣은 뒤, 서버에 끝낸다고 알리고(짧게 기다림) 연결을 닫는다. 실패해도 끝에는 반드시 닫는다.
    /// </summary>
    public async Task DisconnectAsync()
    {
        try
        {
            // 입력 소비자가 줄을 다 비울 때까지 — 여기서 대신 비우면 소비자가 쥔 입력과 순서가 뒤바뀐다
            _inputs.Writer.TryComplete();
            if (_inputTask is { } inputs) await WaitQuietlyAsync(inputs).ConfigureAwait(false);

            await ReleaseClipboardWindowAsync(IsConnected).ConfigureAwait(false);
            await WaitQuietlyAsync(Task.Run(SendShutdown)).ConfigureAwait(false);
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>(UI 가 아닌 스레드에서) 종료 알림을 보내기 줄에 넣고 나갈 때까지 기다린다.</summary>
    private Task SendShutdown()
    {
        lock (_gate)
        {
            if (_stage is null || _channel is not { } channel || !IsConnected) return Task.CompletedTask;

            try
            {
                using var outputs = _stage.GracefulShutdown();
                Collect(outputs);
                return channel.FlushAsync();
            }
            catch (IronRdpException ex)
            {
                return Task.FromException(ex); // 알리지 못해도 닫는다
            }
        }
    }

    /// <summary>닫는 중의 기다림 — 시간을 넘기거나 이미 끊겨 실패해도 닫기는 계속한다.</summary>
    private static async Task WaitQuietlyAsync(Task task)
    {
        try
        {
            await task.WaitAsync(CloseTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or WebSocketException or ObjectDisposedException
                                       or IOException or OperationCanceledException or IronRdpException)
        {
            // 서버가 연결 끊김으로 정리한다
        }
    }

    /// <summary>
    ///     즉시 중단(닫기 핸드셰이크 없음) — 창 닫기·재연결 시 UI 스레드를 막지 않는다.
    ///     IronRDP 객체 해제는 잠금이 필요하므로 다른 스레드에서 한다. 클립보드는 그 루프가 끝나며 UI 스레드에서 정리한다.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        IsConnected = false;
        _lifetimeCts.Cancel();
        AbortSocket();
        _inputs.Writer.TryComplete();
        if (_clipboardTask is null) DisposeClipboard(); // 루프가 없으면(연결 전) 바로 정리
        _ = Task.Run(ReleaseNative);
    }

    private void ReleaseNative()
    {
        lock (_gate)
        {
            _stage?.Dispose();
            _stage = null;
            _image?.Dispose();
            _image = null;
            _input?.Dispose();
            _input = null;
            _channel?.Complete();
            _channel = null;
            foreach (var operation in _heldInputs) operation.Dispose(); // 재활성화 중에 닫혔다
            _heldInputs.Clear();
        }
    }

    private void AbortSocket()
    {
        var websocket = Interlocked.Exchange(ref _websocket, null);
        websocket?.Abort();
        websocket?.Dispose();
    }
}
