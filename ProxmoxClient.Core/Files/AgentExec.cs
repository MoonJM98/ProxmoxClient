using System.Text;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.Core.Files;

/// <summary>게스트 에이전트 명령 결과.</summary>
internal sealed record AgentResult(int ExitCode, string Output, string Error);

/// <summary>
///     게스트 에이전트로 명령 하나를 실행하고 끝날 때까지 기다린다(exec → exec-status 를 짧게부터 점점 길게 물음 —
///     <see cref="PollDelays" /> 뒤로는 1초마다).
///     인자는 하나씩 넘기므로(셸을 거치지 않음) 경로 인용 문제가 없다. 출력이 잘리면(에이전트 한도) 실패로 본다.
/// </summary>
internal sealed class AgentExec(ProxmoxApiClient api, string node, int vmid)
{
    /// <summary>묻는 간격(ms) — 짧은 명령은 곧바로 받고, 긴 명령은 서버를 자주 두드리지 않게.</summary>
    private static readonly int[] PollDelays = [50, 100, 100, 200, 200, 500, 500, 500, 500, 500];

    private static readonly TimeSpan LastPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    public async Task<AgentResult> RunAsync(IReadOnlyList<string> command, string? input, CancellationToken ct)
    {
        var pid = await api.Agent.ExecAsync(node, vmid, command, input, ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + Limit;
        for (var attempt = 0;; attempt++)
        {
            await Task.Delay(PollDelay(attempt), ct).ConfigureAwait(false);
            var status = await api.Agent.ExecStatusAsync(node, vmid, pid, ct).ConfigureAwait(false);
            if (Value(status, "exited") is "1" or "true")
            {
                if (Value(status, "out-truncated") is "1" or "true")
                    throw new GuestFileException(Localization.Res.T("AgentFiles_Truncated"));

                return new AgentResult(int.TryParse(Value(status, "exitcode"), out var code) ? code : -1,
                    Utf8(Value(status, "out-data")), Utf8(Value(status, "err-data")).Trim());
            }

            if (DateTime.UtcNow > deadline) throw new TimeoutException(Localization.Res.T("AgentFiles_Timeout", pid));
        }
    }

    internal static TimeSpan PollDelay(int attempt)
    {
        return attempt < PollDelays.Length ? TimeSpan.FromMilliseconds(PollDelays[attempt]) : LastPoll;
    }

    /// <summary>
    ///     PVE 는 게스트 출력 바이트를 한 글자씩(Latin-1) JSON 에 싣는다 — 한글 오류 문구가 깨지므로 UTF-8 로 다시 읽는다.
    ///     이미 유니코드이거나(0xFF 넘는 글자) UTF-8 이 아니면 그대로 둔다.
    /// </summary>
    internal static string Utf8(string text)
    {
        if (text.All(c => c < 0x80) || text.Any(c => c > 0xFF)) return text;

        try
        {
            return new UTF8Encoding(false, true).GetString(text.Select(c => (byte)c).ToArray());
        }
        catch (DecoderFallbackException)
        {
            return text;
        }
    }

    private static string Value(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var value) ? value : string.Empty;
    }
}
