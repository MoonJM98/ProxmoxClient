using ProxmoxClient.Core.Api;
using ProxmoxClient.Presentation.Shared;

namespace ProxmoxClient.Presentation.Datacenter;

/// <summary>데이터센터 클러스터 화면의 표 — 요약·저장소·백업 일정·복제(WPF DatacenterTables 의 같은 부분).</summary>
internal static partial class DatacenterTables
{
    private static readonly IReadOnlyList<TableColumn> StatusColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "online", HeaderKey = "Table_Online", Width = 70, Format = TableFormats.Flag },
        new() { Key = "ip", HeaderKey = "Table_Address", Width = 140 },
        new() { Key = "nodeid", HeaderKey = "Table_NodeId", Width = 80 },
        new() { Key = "quorate", HeaderKey = "Table_Quorate", Width = 80, Format = TableFormats.Flag },
        new() { Key = "nodes", HeaderKey = "Table_Nodes", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> StorageColumns =
    [
        new() { Key = "storage", HeaderKey = "Table_Name", Width = 140 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "content", HeaderKey = "Table_Content", Width = 0 },
        new() { Key = "_target", HeaderKey = "DcStorage_PathTarget", Width = 180 },
        new() { Key = "shared", HeaderKey = "Table_Shared", Width = 60, Format = TableFormats.Flag },
        new() { Key = "disable", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.InverseFlag },
        new() { Key = "nodes", HeaderKey = "Table_Nodes", Width = 140 }
    ];

    private static readonly IReadOnlyList<TableColumn> BackupJobColumns =
    [
        new() { Key = "id", HeaderKey = "Table_Id", Width = 150 },
        new() { Key = "enabled", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.Flag },
        new() { Key = "schedule", HeaderKey = "Table_Schedule", Width = 130 },
        new() { Key = "next-run", HeaderKey = "Table_NextRun", Width = 140, Format = TableFormats.EpochDate },
        new() { Key = "storage", HeaderKey = "Table_Storage", Width = 110 },
        new() { Key = "node", HeaderKey = "Table_Node", Width = 90 },
        new() { Key = "_selection", HeaderKey = "Table_Guests", Width = 150 },
        new() { Key = "mode", HeaderKey = "Table_Mode", Width = 80 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> ReplicationColumns =
    [
        new() { Key = "id", HeaderKey = "Table_Id", Width = 110 },
        new() { Key = "guest", HeaderKey = "Table_Guest", Width = 80 },
        new() { Key = "source", HeaderKey = "Table_Source", Width = 100 },
        new() { Key = "target", HeaderKey = "Table_Target", Width = 100 },
        new() { Key = "schedule", HeaderKey = "Table_Schedule", Width = 110 },
        new() { Key = "disable", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.InverseFlag },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    public static TableSpec Summary(ProxmoxApiClient api)
    {
        return Create(() => api.Cluster.StatusAsync(), StatusColumns, "DcSummary_Hint");
    }

    public static TableSpec Storage(ProxmoxApiClient api, bool canEdit)
    {
        return new TableSpec(async () => (await api.Storage.ListAsync()).Select(WithTarget).ToList(),
            StorageColumns, "DcStorage_Hint", canEdit ? StorageActions.Actions(api) : null);
    }

    public static TableSpec BackupJobs(ProxmoxApiClient api, bool canEdit)
    {
        return new TableSpec(async () => (await api.Jobs.ListBackupJobsAsync()).Select(WithSelection).ToList(),
            BackupJobColumns, "DcBackup_Hint", canEdit ? ClusterActions.BackupJobs(api) : null);
    }

    /// <summary>웹 UI 의 Path/Target 열 — 유형마다 다른 위치 값(경로·서버:내보내기·풀·대상·데이터스토어).</summary>
    internal static IReadOnlyDictionary<string, string> WithTarget(IReadOnlyDictionary<string, string> row)
    {
        string V(string key) => row.TryGetValue(key, out var v) ? v : string.Empty;
        var target = V("path") is { Length: > 0 } path ? path
            : V("export") is { Length: > 0 } export ? $"{V("server")}:{export}"
            : V("share") is { Length: > 0 } share ? $"//{V("server")}/{share}"
            : V("datastore") is { Length: > 0 } store ? $"{V("server")}:{store}"
            : V("thinpool") is { Length: > 0 } thin ? $"{V("vgname")}/{thin}"
            : V("vgname") is { Length: > 0 } vg ? vg
            : V("pool") is { Length: > 0 } pool ? pool
            : V("target");
        return new Dictionary<string, string>(row) { ["_target"] = target };
    }

    /// <summary>웹 UI 의 Selection 열 — 전체(제외 목록)·풀·지정 게스트.</summary>
    internal static IReadOnlyDictionary<string, string> WithSelection(IReadOnlyDictionary<string, string> row)
    {
        string V(string key) => row.TryGetValue(key, out var v) ? v : string.Empty;
        var selection = V("all") is "1" or "true"
            ? V("exclude") is { Length: > 0 } excluded
                ? UiText.T("DcBackup_AllExcept", excluded)
                : UiText.T("DcBackup_SelectAll")
            : V("pool") is { Length: > 0 } pool ? UiText.T("DcBackup_PoolSelection", pool)
            : V("vmid");
        return new Dictionary<string, string>(row) { ["_selection"] = selection };
    }

    public static TableSpec Replication(ProxmoxApiClient api, bool canEdit)
    {
        return Create(() => api.Jobs.ListReplicationAsync(), ReplicationColumns, "DcReplication_Hint",
            canEdit ? ClusterActions.Replication(api) : null);
    }
}
