namespace ProxmoxClient.Core.Vnc;

/// <summary>
///     화면 갱신 요청 일시 정지 — 콘솔 화면이 가려진 동안(설정 화면을 위에 연 경우 등) 다음 갱신을 요청하지 않아
///     데이터·배터리를 아낀다. 미뤄 둔 요청은 다시 켤 때 한 번 보낸다(서버가 그동안 바뀐 곳을 모아 보낸다).
/// </summary>
public sealed partial class RfbClient
{
    /// <summary>미뤄 둔 요청 없음 / 증분 / 전체.</summary>
    private const int NoRequest = 0, IncrementalRequest = 1, FullRequest = 2;

    private int _updatesPaused;
    private int _skippedRequest;

    /// <summary>이번 FramebufferUpdate 에 DesktopSize(크기 변경)가 있었다(읽기 루프 전용).</summary>
    private bool _resizedInUpdate;

    /// <summary>갱신 요청을 멈추거나 다시 켠다 — 어느 스레드에서 불러도 된다.</summary>
    public Task SetUpdatesPausedAsync(bool paused, CancellationToken ct = default)
    {
        Volatile.Write(ref _updatesPaused, paused ? 1 : 0);
        return paused ? Task.CompletedTask : SendSkippedRequestAsync(ct);
    }

    public bool UpdatesPaused => Volatile.Read(ref _updatesPaused) != 0;

    private Task RequestNextUpdateAsync(bool incremental, CancellationToken ct)
    {
        if (!UpdatesPaused) return RequestFramebufferUpdateAsync(incremental, ct);

        // 미뤄 둔다(전체 요청이 증분보다 우선)
        var want = incremental ? IncrementalRequest : FullRequest;
        int previous;
        do
        {
            previous = Volatile.Read(ref _skippedRequest);
            if (previous >= want) break;
        } while (Interlocked.CompareExchange(ref _skippedRequest, want, previous) != previous);

        // 미루는 사이 다시 켜졌으면(경합) 여기서 보낸다 — 요청이 사라져 화면이 멈추지 않게
        return UpdatesPaused ? Task.CompletedTask : SendSkippedRequestAsync(ct);
    }

    private Task SendSkippedRequestAsync(CancellationToken ct)
    {
        var skipped = Interlocked.Exchange(ref _skippedRequest, NoRequest);
        return skipped == NoRequest
            ? Task.CompletedTask
            : RequestFramebufferUpdateAsync(skipped == IncrementalRequest, ct);
    }
}
