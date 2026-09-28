using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>작업(UPID) — 로그·상태 읽기와 멈추기. 목록·상태는 <see cref="ProxmoxApiClient.GetNodeTasksAsync" /> 등.</summary>
public sealed class TasksApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private static string T(string node, string upid) => $"nodes/{Seg(node)}/tasks/{Seg(upid)}";

    /// <summary>로그 줄(n·t) — start 번째 줄(0부터)부터 limit 줄. 모자라게 오면 끝까지 읽은 것.</summary>
    [PveApi("GET", "/nodes/{node}/tasks/{upid}/log")]
    public Task<IReadOnlyList<Row>> LogAsync(string node, string upid, int limit, int start = 0,
        CancellationToken ct = default)
    {
        var from = start > 0 ? $"&start={start}" : string.Empty;
        return Api.GetTableAsync($"{T(node, upid)}/log?limit={limit}{from}", ct);
    }

    /// <summary>작업 상태 — status(running·stopped), 끝났으면 exitstatus(OK 또는 오류 문구).</summary>
    [PveApi("GET", "/nodes/{node}/tasks/{upid}/status")]
    public Task<Row> StatusAsync(string node, string upid, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"{T(node, upid)}/status", ct);
    }

    [PveApi("DELETE", "/nodes/{node}/tasks/{upid}")]
    public Task<string> StopAsync(string node, string upid, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync(T(node, upid), ct);
    }
}
