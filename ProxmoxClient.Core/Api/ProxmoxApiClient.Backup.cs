using System.Text.Json;
using ProxmoxClient.Core.Localization;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Api;

/// <summary>데이터센터 백업 일정의 부가 정보 — 포함 볼륨 조회와 즉시 실행.</summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>vzdump 가 받는 옵션 — 일정 설정 중 이것만 즉시 실행 요청으로 넘긴다(id·schedule 등은 일정 전용).</summary>
    private static readonly HashSet<string> VzdumpOptions = new(StringComparer.Ordinal)
    {
        "all", "bwlimit", "compress", "dumpdir", "exclude", "exclude-path", "fleecing", "ionice", "lockwait",
        "mailnotification", "mailto", "mode", "notes-template", "notification-mode", "pbs-change-detection-mode",
        "performance", "pigz", "pool", "protected", "prune-backups", "quiet", "remove", "script", "stdexcludes",
        "stop", "stopwait", "storage", "tmpdir", "vmid", "zstd"
    };

    /// <summary>
    ///     백업 일정이 실제로 담을 게스트·볼륨(GET cluster/backup/{id}/included_volumes).
    ///     서버는 게스트 → 볼륨 트리로 주므로 볼륨마다 한 행으로 펼친다(볼륨이 없는 게스트는 게스트 행 하나).
    /// </summary>
    [Versioning.PveApi("GET", "/cluster/backup/{id}/included_volumes")]
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> GetBackupJobVolumesAsync(
        string jobId, CancellationToken ct = default)
    {
        var data = await GetJsonAsync($"cluster/backup/{Escape(jobId)}/included_volumes", ct).ConfigureAwait(false);
        var rows = new List<IReadOnlyDictionary<string, string>>();
        if (!data.TryGetProperty("children", out var guests) || guests.ValueKind != JsonValueKind.Array) return rows;

        foreach (var guest in guests.EnumerateArray())
        {
            var guestRow = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vmid"] = ToText(guest.TryGetProperty("id", out var id) ? id : default),
                ["name"] = ToText(guest.TryGetProperty("name", out var name) ? name : default),
                ["type"] = ToText(guest.TryGetProperty("type", out var type) ? type : default)
            };

            if (!guest.TryGetProperty("children", out var volumes) || volumes.ValueKind != JsonValueKind.Array
                                                                      || volumes.GetArrayLength() == 0)
            {
                rows.Add(guestRow);
                continue;
            }

            foreach (var volume in volumes.EnumerateArray())
            {
                var row = new Dictionary<string, string>(guestRow, StringComparer.Ordinal);
                foreach (var property in volume.EnumerateObject())
                    row[$"volume-{property.Name}"] = ToText(property.Value);
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>
    ///     백업 일정을 지금 한 번 실행한다 — 웹 UI 의 '지금 실행'처럼 일정 설정을 노드마다 vzdump 로 보낸다.
    ///     일정에 노드가 지정돼 있으면 그 노드만, 아니면 전달받은 노드 모두. 시작된 작업의 UPID 를 돌려준다.
    /// </summary>
    [Versioning.PveApi("POST", "/nodes/{node}/vzdump")]
    public async Task<IReadOnlyList<string>> RunBackupJobNowAsync(
        IReadOnlyDictionary<string, string> job, IReadOnlyList<string> onlineNodes, CancellationToken ct = default)
    {
        // 보존(prune-backups)·성능(performance)·플리싱(fleecing)은 GET 에서 객체로 오므로 서버 입력 형식으로 되돌려
        // 함께 보낸다 — 버리면 지금 실행이 예약 실행과 다른 설정으로 돈다
        var form = job
            .Where(kv => VzdumpOptions.Contains(kv.Key) && kv.Value.Length > 0)
            .Select(kv => (kv.Key, Value: PropertyString.FromJsonObject(kv.Value)))
            .Where(kv => kv.Value.Length > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var pinned = job.TryGetValue("node", out var only) && only.Length > 0;
        if (pinned && !onlineNodes.Contains(only!, StringComparer.OrdinalIgnoreCase))
            throw new ProxmoxApiException(0, Res.T("Backup_PinnedNodeOffline", only!));
        var nodes = pinned ? [only!] : onlineNodes;
        var pairs = form.SelectMany(kv => kv.Key == "exclude-path"
                ? kv.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p => new KeyValuePair<string, string>(kv.Key, p))
                : [kv])
            .ToList();
        var upids = new List<string>(nodes.Count);
        foreach (var node in nodes)
            upids.Add(await SendPairsAsync(HttpMethod.Post, $"nodes/{Escape(node)}/vzdump", pairs, ct)
                .ConfigureAwait(false));

        return upids;
    }
}
