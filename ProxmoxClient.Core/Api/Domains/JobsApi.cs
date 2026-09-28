using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     예약 작업 — 백업 일정(cluster/backup), 복제(cluster/replication), 일정 분석(7.1+), 백업 안 된 게스트.
///     백업 일정 파라미터는 버전마다 늘었다: notes-template·protected(7.1), repeat-missed·performance(7.2),
///     notification-mode(8.1), fleecing·pbs-change-detection-mode(8.2). 서버가 모르는 칸은 보내지 않는다.
/// </summary>
public sealed class JobsApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    [PveApi("GET", "/cluster/backup")]
    public Task<IReadOnlyList<Row>> ListBackupJobsAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/backup", ct);
    }

    /// <summary>일정 하나 — 배열(exclude-path 등)은 줄마다 한 항목.</summary>
    [PveApi("GET", "/cluster/backup/{id}")]
    public Task<Row> GetBackupJobAsync(string id, CancellationToken ct = default)
    {
        return Api.GetConfigLinesAsync($"cluster/backup/{Seg(id)}", ct);
    }

    /// <summary>일정 하나 — 객체 값(prune-backups 등)은 JSON 그대로(표시용).</summary>
    [PveApi("GET", "/cluster/backup/{id}")]
    public Task<Row> GetBackupJobRawAsync(string id, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"cluster/backup/{Seg(id)}", ct);
    }

    [PveApi("POST", "/cluster/backup")]
    [PveParam("notes-template", "7.1")]
    [PveParam("protected", "7.1")]
    [PveParam("repeat-missed", "7.2")]
    [PveParam("performance", "7.2")]
    [PveParam("notification-mode", "8.1")]
    [PveParam("fleecing", "8.2")]
    [PveParam("pbs-change-detection-mode", "8.2")]
    public Task<string> CreateBackupJobAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("cluster/backup", Supported(form), ct);
    }

    [PveApi("PUT", "/cluster/backup/{id}")]
    [PveParam("notes-template", "7.1")]
    [PveParam("protected", "7.1")]
    [PveParam("repeat-missed", "7.2")]
    [PveParam("performance", "7.2")]
    [PveParam("notification-mode", "8.1")]
    [PveParam("fleecing", "8.2")]
    [PveParam("pbs-change-detection-mode", "8.2")]
    public Task<string> UpdateBackupJobAsync(string id, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"cluster/backup/{Seg(id)}", Supported(form), ct);
    }

    [PveApi("DELETE", "/cluster/backup/{id}")]
    public Task<string> DeleteBackupJobAsync(string id, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"cluster/backup/{Seg(id)}", ct);
    }

    /// <summary>어느 백업 일정에도 들지 않은 게스트.</summary>
    [PveApi("GET", "/cluster/backup-info/not-backed-up")]
    public Task<IReadOnlyList<Row>> NotBackedUpAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/backup-info/not-backed-up", ct);
    }

    /// <summary>일정 식이 다음에 언제 도는지(7.1+) — 행마다 timestamp·utc.</summary>
    [PveApi("GET", "/cluster/jobs/schedule-analyze", Since = "7.1")]
    public async Task<IReadOnlyList<Row>> AnalyzeScheduleAsync(string schedule, int iterations = 10,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync(
            $"cluster/jobs/schedule-analyze?schedule={Uri.EscapeDataString(schedule)}&iterations={iterations}", ct)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 복제

    [PveApi("GET", "/cluster/replication")]
    public Task<IReadOnlyList<Row>> ListReplicationAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/replication", ct);
    }

    [PveApi("POST", "/cluster/replication")]
    public Task<string> CreateReplicationAsync(IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync("cluster/replication", form, ct);
    }

    [PveApi("PUT", "/cluster/replication/{id}")]
    public Task<string> UpdateReplicationAsync(string id, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"cluster/replication/{Seg(id)}", form, ct);
    }

    [PveApi("DELETE", "/cluster/replication/{id}")]
    public Task<string> DeleteReplicationAsync(string id, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"cluster/replication/{Seg(id)}", ct);
    }
}
