using ProxmoxClient.Core.Api;
using ProxmoxClient.Presentation.Shared;

namespace ProxmoxClient.Presentation.Datacenter;

/// <summary>데이터센터 HA 목록(WPF DatacenterTables 의 HA 부분) — 리소스·상태·규칙.</summary>
internal static partial class DatacenterTables
{
    private static readonly IReadOnlyList<TableColumn> HaColumns =
    [
        new() { Key = "sid", HeaderKey = "Table_Id", Width = 110 },
        new() { Key = "state", HeaderKey = "Table_State", Width = 100 },
        new() { Key = "group", HeaderKey = "Table_Group", Width = 120 },
        new() { Key = "max_restart", HeaderKey = "Table_MaxRestart", Width = 100 },
        new() { Key = "max_relocate", HeaderKey = "Table_MaxRelocate", Width = 100 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> HaStatusColumns =
    [
        new() { Key = "type", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "id", HeaderKey = "Table_Id", Width = 160 },
        new() { Key = "node", HeaderKey = "Table_Node", Width = 110 },
        new() { Key = "status", HeaderKey = "Table_State", Width = 0 },
        new() { Key = "request_state", HeaderKey = "DcHa_Requested", Width = 110 },
        new() { Key = "quorate", HeaderKey = "Table_Quorate", Width = 70, Format = TableFormats.Flag }
    ];

    /// <param name="rules">PVE 9 이상(규칙) — 리소스 편집 칸이 버전마다 다르다.</param>
    public static TableSpec HaResources(ProxmoxApiClient api, bool canEdit, bool rules)
    {
        return Create(() => api.Ha.ListResourcesAsync(), HaColumns, "DcHa_Hint",
            canEdit ? HaActions.Resources(api, rules) : null);
    }

    public static TableSpec HaStatus(ProxmoxApiClient api, IReadOnlyList<TableAction>? actions = null)
    {
        return Create(() => api.Ha.StatusAsync(), HaStatusColumns, "DcHa_StatusHint", actions);
    }

    /// <summary>웹 UI 의 HA 화면 — 상태·리소스와, 서버 버전에 따라 규칙(PVE 9) 또는 그룹(PVE 8).</summary>
    public static DeferredSpec Ha(ProxmoxApiClient api, bool canEdit)
    {
        return HaActions.View(api, canEdit, HaResources);
    }

    public static TableSpec HaRules(ProxmoxApiClient api, bool canEdit)
    {
        return Create(() => api.Ha.ListRulesAsync(), HaRuleActions.Columns, "DcHaRules_Hint",
            canEdit ? HaRuleActions.Actions(api) : null);
    }
}
