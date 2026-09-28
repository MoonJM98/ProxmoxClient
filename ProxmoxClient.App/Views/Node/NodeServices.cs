using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Node;

/// <summary>
///     노드 시스템 서비스(웹 UI 시스템 → 서비스) — 상태 보기와 시작·중지·재시작·다시 읽기. pveproxy·pvedaemon 을
///     멈추거나 다시 시작하면 이 앱의 연결도 잠시 끊기므로 중지·재시작은 확인을 받는다.
/// </summary>
internal static class NodeServices
{
    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 150 },
        new() { Key = "active-state", HeaderKey = "Table_State", Width = 90 },
        new() { Key = "unit-state", HeaderKey = "NodeServices_UnitState", Width = 90 },
        new() { Key = "desc", HeaderKey = "Table_Description", Width = 0 }
    ];

    public static TableTab Create(ProxmoxApiClient api, string node, bool canEdit)
    {
        return new TableTab(() => api.Nodes.ServicesAsync(node), Columns, "NodeServices_Hint",
            canEdit
                ?
                [
                    Command(api, node, "start", "NodeServices_Start", "IconPlay", confirm: false),
                    Command(api, node, "stop", "NodeServices_Stop", "IconSquare", confirm: true),
                    Command(api, node, "restart", "NodeServices_Restart", "IconRotate", confirm: true),
                    Command(api, node, "reload", "NodeServices_Reload", "IconRefresh", confirm: false)
                ]
                : null);
    }

    private static TableAction Command(ProxmoxApiClient api, string node, string verb, string labelKey,
        string iconKey, bool confirm)
    {
        return new TableAction
        {
            LabelKey = labelKey, IconKey = iconKey, NeedsSelection = true,
            Confirm = confirm ? row => Loc.T("NodeServices_Confirm", Loc.T(labelKey), Name(row!)) : null,
            Run = async (row, _) => await RunTaskAsync(api,
                api.Nodes.ServiceCommandAsync(node, Name(row!), verb), "NodeServices_Done")
        };
    }

    /// <summary>서비스 이름 — 목록의 service(없으면 name) 값을 그대로 요청 경로에 쓴다.</summary>
    private static string Name(IReadOnlyDictionary<string, string> row)
    {
        return row.TryGetValue("service", out var service) && service.Length > 0 ? service : row["name"];
    }
}
