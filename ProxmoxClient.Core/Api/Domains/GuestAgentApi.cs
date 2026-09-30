using ProxmoxClient.Core.Api.Versioning;
using ProxmoxClient.Core.Models;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     게스트 에이전트로 게스트 안을 다루는 요청 — 명령 실행·파일 읽기/쓰기·파일 시스템 동결. 게스트를 root 로 다루는
///     것과 같아 서버가 권한(9.x: VM.GuestAgent.*, 그 전: VM.Monitor)을 따로 확인한다.
/// </summary>
public sealed class GuestAgentApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private static string A(string node, int vmid) => $"nodes/{Seg(node)}/qemu/{vmid}/agent";

    /// <summary>명령을 게스트 안에서 시작한다 — 프로그램과 인자를 하나씩(셸을 거치지 않는다). 돌려주는 값은 pid.</summary>
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/agent/exec")]
    public async Task<int> ExecAsync(string node, int vmid, IReadOnlyList<string> command, string? input,
        CancellationToken ct = default)
    {
        if (command.Count == 0) throw new ArgumentException("command is empty", nameof(command));

        var pairs = command.Select(c => new KeyValuePair<string, string>("command", c)).ToList();
        if (!string.IsNullOrEmpty(input)) pairs.Add(new KeyValuePair<string, string>("input-data", input));
        var result = await Api.SendPairsForObjectAsync(HttpMethod.Post, $"{A(node, vmid)}/exec", pairs, ct)
            .ConfigureAwait(false);
        return result.TryGetValue("pid", out var pid) && int.TryParse(pid, out var value)
            ? value
            : throw new ProxmoxApiException("agent exec: no pid");
    }

    /// <summary>시작한 명령의 상태 — exited·exitcode·out-data·err-data·out-truncated·err-truncated·signal.</summary>
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/exec-status")]
    public Task<Row> ExecStatusAsync(string node, int vmid, int pid, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"{A(node, vmid)}/exec-status?pid={pid}", ct);
    }

    /// <summary>게스트 안 파일 읽기(최대 16 MiB) — content·truncated.</summary>
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/file-read")]
    public Task<Row> FileReadAsync(string node, int vmid, string path, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"{A(node, vmid)}/file-read?file={Uri.EscapeDataString(path)}", ct);
    }

    /// <summary>게스트 안 파일 쓰기 — 있던 파일은 바뀐다. 서버가 base64 로 바꿔 에이전트에 넘긴다.</summary>
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/agent/file-write")]
    public Task<Row> FileWriteAsync(string node, int vmid, string path, string content,
        CancellationToken ct = default)
    {
        return Api.SendForObjectAsync(HttpMethod.Post, $"{A(node, vmid)}/file-write",
            new Dictionary<string, string> { ["file"] = path, ["content"] = content }, ct);
    }

    /// <summary>
    ///     게스트 안 파일 쓰기(바이트 그대로) — base64 를 직접 넘긴다(encode=0). 서버의 content 한도(60 KiB 글자) 안에서만.
    /// </summary>
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/agent/file-write")]
    public Task<Row> FileWriteBytesAsync(string node, int vmid, string path, ReadOnlyMemory<byte> data,
        CancellationToken ct = default)
    {
        return Api.SendForObjectAsync(HttpMethod.Post, $"{A(node, vmid)}/file-write",
            new Dictionary<string, string>
            {
                ["file"] = path, ["content"] = Convert.ToBase64String(data.Span), ["encode"] = "0"
            }, ct);
    }

    /// <summary>
    ///     파일 시스템 동결·해제·상태 — action: freeze·thaw·status. 동결 동안 게스트의 디스크 쓰기가 멈추므로 끝나면
    ///     반드시 해제해야 한다. 응답 result 를 글로 돌려준다.
    /// </summary>
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/agent/fsfreeze-freeze")]
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/agent/fsfreeze-thaw")]
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/agent/fsfreeze-status")]
    public async Task<string> FsFreezeAsync(string node, int vmid, string action, CancellationToken ct = default)
    {
        if (action is not ("freeze" or "thaw" or "status"))
            throw new ArgumentOutOfRangeException(nameof(action), action, null);
        var result = await Api.SendForObjectAsync(HttpMethod.Post, $"{A(node, vmid)}/fsfreeze-{action}",
            new Dictionary<string, string>(), ct).ConfigureAwait(false);
        return result.TryGetValue("result", out var text) ? text : string.Empty;
    }

    /// <summary>
    ///     명령 줄을 프로그램·인자로 나눈다 — 공백으로 가르고, 큰따옴표로 묶은 부분은 하나로 둔다(셸 문법은 해석하지 않는다).
    ///     닫히지 않은 큰따옴표가 있으면 null(확인 창에 보인 것과 실제로 보내는 것이 달라지지 않게).
    /// </summary>
    public static IReadOnlyList<string>? SplitCommandLine(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started) parts.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (quoted) return null;
        if (started) parts.Add(current.ToString());
        return parts;
    }
}
