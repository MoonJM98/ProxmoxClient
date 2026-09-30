using System.Text.Json.Serialization;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Api;

/// <summary>작업(UPID) 목록·상태·완료 대기.</summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>Lists cluster-wide tasks, newest first (GET /cluster/tasks).</summary>
    [Versioning.PveApi("GET", "/cluster/tasks")]
    public Task<IReadOnlyList<PveTask>> GetClusterTasksAsync(CancellationToken ct = default)
    {
        return GetListAsync<PveTask, TaskDto>("cluster/tasks", MapTask, ct);
    }
    /// <summary>
    ///     Lists tasks on one node (GET /nodes/{node}/tasks?source=all|active&amp;limit=100[&amp;vmid=]).
    ///     source=all 은 실행 중과 끝난 작업을 함께 준다(기본 archive 는 끝난 것만). vmid 를 주면 그 게스트 작업만
    ///     서버가 골라 준다(노드의 최근 100개 안에서만 거르지 않게). Running tasks are reported as "running".
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/tasks")]
    public Task<IReadOnlyList<PveTask>> GetNodeTasksAsync(string node, bool activeOnly = false, int? vmid = null,
        CancellationToken ct = default)
    {
        var filter = vmid is { } id ? $"&vmid={id}" : string.Empty;
        return GetListAsync<PveTask, TaskDto>(
            $"nodes/{Escape(node)}/tasks?source={(activeOnly ? "active" : "all")}&limit=100{filter}",
            MapTask,
            ct);
    }
    /// <summary>
    ///     Gets the current status of one task by UPID
    ///     (GET /nodes/{node}/tasks/{upid}/status). The node is parsed from the UPID.
    ///     Terminal tasks report "OK" / "ERROR: ..." (exitstatus); running ones "running".
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/tasks/{upid}/status")]
    public async Task<PveTask> GetTaskStatusAsync(string upid, CancellationToken ct = default)
    {
        var baseTask = ParseUpid(upid);
        var data = await GetJsonAsync(
            $"nodes/{Escape(baseTask.Node)}/tasks/{Escape(upid)}/status",
            ct).ConfigureAwait(false);

        // Finished tasks expose status "stopped" plus exitstatus ("OK"/"ERROR: ...").
        var status = GetString(data, "exitstatus");
        if (status.Length == 0) status = GetString(data, "status");

        var endTime = GetLong(data, "endtime");
        var startTime = GetLong(data, "starttime"); // 목록과 같은 값 — UPID 로 읽은 값은 예비
        return new PveTask
        {
            Upid = baseTask.Upid,
            Node = baseTask.Node,
            Type = baseTask.Type,
            Id = baseTask.Id,
            User = baseTask.User,
            StartTimeUtc = startTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(startTime).UtcDateTime
                : baseTask.StartTimeUtc,
            EndTimeUtc = endTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(endTime).UtcDateTime
                : null,
            Status = status.Length > 0 ? status : "running"
        };
    }
    /// <summary>
    ///     Polls a task (default every 2 s) until it leaves the "running" state and
    ///     returns the final task snapshot. Cancellation stops the wait.
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/tasks/{upid}/status")]
    public async Task<PveTask> WaitTaskAsync(string upid, TimeSpan? pollInterval = null, CancellationToken ct = default)
    {
        var delay = pollInterval ?? TimeSpan.FromSeconds(2);
        while (true)
        {
            var task = await GetTaskStatusAsync(upid, ct).ConfigureAwait(false);
            if (!task.IsRunning) return task;

            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
    private static PveTask MapTask(TaskDto dto)
    {
        var upid = dto.Upid ?? string.Empty;
        var fromUpid = ParseUpid(upid);
        var status = dto.Status;
        if (string.IsNullOrEmpty(status) && dto.ExitStatus is not null)
            status = dto.ExitStatus;
        else if (string.IsNullOrEmpty(status))
            // Task list entries without a status are still running.
            status = "running";

        return new PveTask
        {
            Upid = upid,
            Node = dto.Node ?? fromUpid.Node,
            Type = dto.Type ?? fromUpid.Type,
            Id = dto.Id ?? fromUpid.Id,
            User = dto.User ?? fromUpid.User,
            StartTimeUtc = dto.StartTime is > 0
                ? DateTimeOffset.FromUnixTimeSeconds(dto.StartTime.Value).UtcDateTime
                : fromUpid.StartTimeUtc,
            EndTimeUtc = dto.EndTime is > 0
                ? DateTimeOffset.FromUnixTimeSeconds(dto.EndTime.Value).UtcDateTime
                : null,
            Status = status
        };
    }
    private sealed class TaskDto
    {
        [JsonPropertyName("upid")] public string? Upid { get; set; }
        [JsonPropertyName("node")] public string? Node { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("user")] public string? User { get; set; }
        [JsonPropertyName("starttime")] public long? StartTime { get; set; }
        [JsonPropertyName("endtime")] public long? EndTime { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("exitstatus")] public string? ExitStatus { get; set; }
    }
}
