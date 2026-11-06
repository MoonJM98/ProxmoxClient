namespace ProxmoxClient.Core.Api;

/// <summary>
///     주기 조회(작업 완료 대기 등)가 일시적인 실패를 견디는 규칙 — 연결 끊김·시간 초과·5xx 는 간격을 늘려 가며
///     다시 묻고, 실패가 <see cref="GiveUpAfter" /> 동안 이어지면 마지막 오류를 그대로 알린다.
///     취소와 4xx(권한·잘못된 요청·세션 만료)는 바로 끝낸다.
/// </summary>
public sealed class PollRetryPolicy
{
    /// <summary>기본값 — 2분 동안 계속 실패하면 포기, 간격은 2초부터 두 배씩 최대 15초.</summary>
    public static PollRetryPolicy Default { get; } = new();

    /// <summary>연속 실패가 이 시간을 넘으면 포기한다.</summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>첫 재시도 간격.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>재시도 간격 상한.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>다시 물어볼 만한 실패인지 — 응답 없음(상태 0)·408·429·5xx·전송 오류.</summary>
    public static bool IsTransient(Exception ex)
    {
        return ex switch
        {
            CertificateTrustException => false,
            ProxmoxApiException api => api.StatusCode is 0 or 408 or 429 or >= 500,
            HttpRequestException or IOException or TimeoutException => true,
            _ => false
        };
    }

    /// <summary>연속 실패 횟수(1부터)에 맞는 다음 간격 — 두 배씩 늘리고 상한에서 멈춘다.</summary>
    public TimeSpan DelayFor(int consecutiveFailures)
    {
        var factor = Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 30));
        var delay = TimeSpan.FromMilliseconds(InitialDelay.TotalMilliseconds * factor);
        return delay < MaxDelay ? delay : MaxDelay;
    }

    /// <summary>
    ///     poll 을 done 이 참일 때까지 interval 간격으로 부른다. 일시적 실패는 이 규칙대로 다시 시도하고,
    ///     성공하면 실패 기록을 지운다.
    /// </summary>
    public async Task<T> PollAsync<T>(Func<CancellationToken, Task<T>> poll, Func<T, bool> done, TimeSpan interval,
        CancellationToken ct)
    {
        var failures = 0;
        DateTime? failingSince = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan wait;
            try
            {
                var value = await poll(ct).ConfigureAwait(false);
                if (done(value)) return value;

                failures = 0;
                failingSince = null;
                wait = interval;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsTransient(ex))
            {
                var now = DateTime.UtcNow;
                failingSince ??= now;
                if (now - failingSince.Value >= GiveUpAfter) throw;

                wait = DelayFor(++failures);
            }

            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }
}
