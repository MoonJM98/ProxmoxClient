using System.Globalization;
using System.Windows;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     데이터센터 "검색" 탭 — 클러스터의 모든 리소스(노드·게스트·저장소·풀·SDN)를 한 표에 모아 필터로 찾는다.
///     서버가 권한에 맞게 걸러 주므로 누구에게나 보인다.
/// </summary>
internal static class SearchTab
{
    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "type", HeaderKey = "Table_Type", Width = 80 },
        new() { Key = "description", HeaderKey = "Table_Description", Width = 200 },
        new() { Key = "id", HeaderKey = "Table_Id", Width = 150 },
        new() { Key = "node", HeaderKey = "Table_Node", Width = 90 },
        new() { Key = "pool", HeaderKey = "Table_Pool", Width = 90 },
        new() { Key = "status", HeaderKey = "Table_State", Width = 80 },
        new() { Key = "cpu", HeaderKey = "Table_Cpu", Width = 70, Format = TableFormats.Percent },
        new() { Key = "memusage", HeaderKey = "Table_MemoryUsage", Width = 100, Format = TableFormats.Percent },
        new() { Key = "diskusage", HeaderKey = "Table_DiskUsage", Width = 100, Format = TableFormats.Percent },
        new() { Key = "uptime", HeaderKey = "Table_Uptime", Width = 0, Format = TableFormats.Uptime }
    ];

    /// <param name="open">행을 두 번 눌렀을 때 그 리소스 창을 여는 동작(메인 창이 넘긴다).</param>
    /// <param name="actions">표 위 버튼(클러스터 일괄 작업 등).</param>
    public static TableTab Create(ProxmoxApiClient api, Action<IReadOnlyDictionary<string, string>, Window?>? open,
        IReadOnlyList<TableAction>? actions = null)
    {
        return new TableTab(
            async () => (await api.Cluster.ResourcesAsync()).Select(ToSearchRow).ToList(),
            Columns, "DcSearch_Hint", actions, filterable: true, open: open);
    }

    /// <summary>서버 행에 설명·메모리/디스크 사용률(0~1)을 덧붙인 새 행을 만든다.</summary>
    internal static IReadOnlyDictionary<string, string> ToSearchRow(IReadOnlyDictionary<string, string> row)
    {
        return new Dictionary<string, string>(row)
        {
            ["description"] = Describe(row),
            ["memusage"] = Ratio(row, "mem", "maxmem"),
            ["diskusage"] = Ratio(row, "disk", "maxdisk")
        };
    }

    /// <summary>게스트는 이름, 저장소·SDN 은 "이름 (노드)", 풀·노드는 그 이름.</summary>
    private static string Describe(IReadOnlyDictionary<string, string> row)
    {
        var value = (string key) => row.TryGetValue(key, out var v) ? v : string.Empty;
        var node = value("node");
        return value("type") switch
        {
            "qemu" or "lxc" => value("name"),
            "storage" => WithNode(value("storage"), node),
            "sdn" => WithNode(value("sdn"), node),
            "pool" => value("pool"),
            _ => node
        };
    }

    private static string WithNode(string name, string node)
    {
        return string.IsNullOrEmpty(node) ? name : $"{name} ({node})";
    }

    private static string Ratio(IReadOnlyDictionary<string, string> row, string usedKey, string maxKey)
    {
        return row.TryGetValue(usedKey, out var used) && row.TryGetValue(maxKey, out var max)
               && double.TryParse(used, NumberStyles.Float, CultureInfo.InvariantCulture, out var u)
               && double.TryParse(max, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0
            ? (u / m).ToString("R", CultureInfo.InvariantCulture)
            : string.Empty;
    }
}
