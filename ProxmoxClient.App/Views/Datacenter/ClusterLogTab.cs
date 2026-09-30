using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>클러스터 로그(웹 UI 아래 'Cluster log') — 노드·사용자별 최근 기록. 새 기록이 위에 온다.</summary>
internal static class ClusterLogTab
{
    /// <summary>한 번에 읽을 줄 수(웹 UI 와 같은 수준).</summary>
    private const int MaxLines = 500;

    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "time", HeaderKey = "ClusterLog_Time", Width = 150, Format = TableFormats.EpochDate },
        new() { Key = "node", HeaderKey = "Table_Node", Width = 90 },
        new() { Key = "tag", HeaderKey = "ClusterLog_Service", Width = 100 },
        new() { Key = "user", HeaderKey = "ClusterLog_User", Width = 120 },
        new() { Key = "msg", HeaderKey = "ClusterLog_Message", Width = 0 }
    ];

    public static TableTab Create(ProxmoxApiClient api)
    {
        return new TableTab(async () => (await api.Cluster.LogAsync(MaxLines))
                .OrderByDescending(r => r.TryGetValue("time", out var t) && long.TryParse(t, out var n) ? n : 0)
                .ToList(),
            Columns, "ClusterLog_Hint");
    }
}
