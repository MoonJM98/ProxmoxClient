using System.Threading.Channels;
using Devolutions.IronRdp;

namespace ProxmoxClient.Core.Rdp;

/// <summary>
///     RDP PDU 를 주고받는 통로 — 읽기는 IronRDP <see cref="Framed{TS}" />(PDU 경계 찾기),
///     쓰기는 한 줄 대기열로 웹소켓 스트림에 직접 한다. 수신 응답·키·마우스가 여러 스레드에서 와도 넣은 순서대로 나간다.
///     (Framed.Write 는 스레드에 묶인 Mutex 를 await 너머로 풀어서 이어짐이 다른 스레드에서 돌면 예외가 난다)
/// </summary>
internal sealed class RdpChannel
{
    private readonly Channel<(byte[] Data, TaskCompletionSource? Done)> _queue =
        Channel.CreateUnbounded<(byte[], TaskCompletionSource?)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Stream _stream;

    public RdpChannel(Stream stream)
    {
        _stream = stream;
        Framed = new Framed<Stream>(stream);
        _ = Task.Run(PumpAsync);
    }

    public Framed<Stream> Framed { get; }

    public Task<(Devolutions.IronRdp.Action Action, byte[] Payload)> ReadPduAsync()
    {
        return Framed.ReadPdu();
    }

    public Task<byte[]> ReadByHintAsync(PduHint hint)
    {
        return Framed.ReadByHint(hint);
    }

    public Task<byte[]> ReadByHintAsync(IPduHint hint)
    {
        return Framed.ReadByHint(hint);
    }

    /// <summary>보내기를 줄 세운다(기다리지 않음) — 연결이 끊겼으면 조용히 버린다.</summary>
    public void Enqueue(byte[] data)
    {
        _queue.Writer.TryWrite((data, null));
    }

    /// <summary>줄 세워 보내고 실제로 나갈 때까지 기다린다.</summary>
    public Task WriteAsync(byte[] data)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _queue.Writer.TryWrite((data, done))
            ? done.Task
            : Task.FromException(new ObjectDisposedException(nameof(RdpChannel)));
    }

    /// <summary>지금까지 줄에 넣은 것이 모두 나갈 때까지 기다린다.</summary>
    public Task FlushAsync()
    {
        return WriteAsync([]);
    }

    /// <summary>더 보내지 않는다 — 대기열에 남은 것은 버린다.</summary>
    public void Complete()
    {
        _queue.Writer.TryComplete();
    }

    /// <summary>연결·재활성화 단계 하나 — 서버 PDU 가 필요하면 읽어서 넣고, 만든 응답이 있으면 보낸다.</summary>
    public async Task StepAsync(ISequence sequence, WriteBuf buf)
    {
        buf.Clear();
        var written = sequence.NextPduHint() is { } hint
            ? sequence.Step(await ReadByHintAsync(hint).ConfigureAwait(false), buf)
            : sequence.StepNoInput(buf);
        if (written.GetWrittenType() == WrittenType.Nothing) return;

        var response = new byte[(int)written.GetSize().Get()];
        buf.ReadIntoBuf(response);
        await WriteAsync(response).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        Exception? failure = null;
        await foreach (var (data, done) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (failure is not null)
            {
                done?.TrySetException(failure); // 이미 끊겼다 — 남은 것은 실패로 알린다
                continue;
            }

            try
            {
                if (data.Length > 0) await _stream.WriteAsync(data).ConfigureAwait(false); // 빈 것은 표시(Flush)
                done?.TrySetResult();
            }
            catch (Exception ex)
            {
                // 끊긴 연결 — 수신 루프가 종료를 알린다. 보내기는 더 하지 않는다
                failure = ex;
                done?.TrySetException(ex);
                _queue.Writer.TryComplete();
            }
        }
    }
}
