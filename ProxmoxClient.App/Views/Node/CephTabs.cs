using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Node;

/// <summary>
///     노드 Ceph 화면 — 상태·설정·모니터·매니저·OSD·CephFS·풀·로그. Ceph 가 설치되지 않은 노드는 각 탭에 서버 오류가
///     보이며, 모니터 탭의 'Ceph 설치'로 설치 마법사를 연다.
/// </summary>
internal static class CephTabs
{
    private static readonly IReadOnlyList<TableColumn> OsdColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 90 },
        new() { Key = "host", HeaderKey = "Table_Node", Width = 110 },
        new() { Key = "status", HeaderKey = "Table_State", Width = 70 },
        new() { Key = "in", HeaderKey = "CephTab_In", Width = 50, Format = TableFormats.Flag },
        new() { Key = "device_class", HeaderKey = "CephTab_Class", Width = 70 },
        new() { Key = "total_space", HeaderKey = "Table_Size", Width = 100, Format = TableFormats.Bytes },
        new() { Key = "percent_used", HeaderKey = "CephTab_UsedPercent", Width = 80 },
        new() { Key = "ceph_version_short", HeaderKey = "CephTab_Version", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> PoolColumns =
    [
        new() { Key = "pool_name", HeaderKey = "Table_Name", Width = 0 },
        new() { Key = "size", HeaderKey = "CephTab_Size", Width = 60 },
        new() { Key = "min_size", HeaderKey = "CephTab_MinSize", Width = 70 },
        new() { Key = "pg_num", HeaderKey = "CephTab_PgNum", Width = 60 },
        new() { Key = "pg_autoscale_mode", HeaderKey = "CephTab_Autoscale", Width = 90 },
        new() { Key = "crush_rule_name", HeaderKey = "CephTab_CrushRule", Width = 130 },
        new() { Key = "bytes_used", HeaderKey = "NodeDisks_Used", Width = 100, Format = TableFormats.Bytes }
    ];

    public static SubTabsView Create(ProxmoxApiClient api, string node, bool canEdit, bool canConsole)
    {
        IReadOnlyList<TableAction>? install = canConsole ? [CephManage.Install(api, node)] : null;
        var basePath = $"nodes/{Seg(node)}/ceph";
        return new SubTabsView(
        [
            ("DcHa_Status", () => new TextEditTab(async () => (await api.GetPrettyJsonAsync($"{basePath}/status"),
                string.Empty), null, "CephTab_StatusHint")),
            ("CephTab_Config", () => new TextEditTab(async () => (await api.GetTextAsync($"{basePath}/cfg/raw"),
                string.Empty), null, "CephTab_ConfigHint")),
            ("CephTab_Monitors", () => CephManage.Service(api, node, basePath, "mon", "CephTab_MonitorsHint",
                canEdit, install)),
            ("CephTab_Managers", () => CephManage.Service(api, node, basePath, "mgr", "CephTab_ManagersHint",
                canEdit)),
            ("CephTab_Osd", () => new TableTab(() => api.GetCephOsdsAsync(node), OsdColumns, "CephTab_OsdHint",
                canEdit ? [..CephManage.OsdLifecycle(api, node, basePath), ..OsdActions(api, basePath)] : null)),
            ("CephTab_CephFs", () => CephManage.FileSystems(api, node, basePath, canEdit)),
            ("CephTab_Pools", () => new TableTab(() => api.GetTableAsync($"{basePath}/pool"), PoolColumns,
                "CephTab_PoolsHint", canEdit ? PoolActions(api, basePath) : null)),
            ("DcFirewall_Log", () => new TextEditTab(async () =>
            {
                var lines = await api.GetTableAsync($"{basePath}/log?limit=500");
                return (string.Join('\n', lines.Select(l => Value(l, "t"))), string.Empty);
            }, null, "CephTab_LogHint"))
        ]);
    }

    /// <summary>OSD 넣기(in)·빼기(out)·스크럽 — 빼면 데이터가 다른 OSD 로 옮겨지기 시작한다.</summary>
    private static IReadOnlyList<TableAction> OsdActions(ProxmoxApiClient api, string basePath)
    {
        TableAction OsdAction(string labelKey, string iconKey, string verb, string? confirmKey) => new()
        {
            LabelKey = labelKey, IconKey = iconKey, NeedsSelection = true,
            Confirm = confirmKey is null ? null : row => Loc.T(confirmKey, Value(row!, "name")),
            Run = async (row, _) =>
            {
                var osd = row!;
                await api.PostActionAsync($"{basePath}/osd/{Seg(Value(osd, "id"))}/{verb}");
                return Loc.T("CephTab_OsdDone", Value(osd, "name"));
            }
        };

        return
        [
            OsdAction("CephTab_OsdIn", "IconPlay", "in", null),
            OsdAction("CephTab_OsdOut", "IconSquare", "out", "CephTab_OsdOutConfirm"),
            OsdAction("CephTab_Scrub", "IconRefresh", "scrub", null)
        ];
    }

    private static IReadOnlyList<TableAction> PoolActions(ProxmoxApiClient api, string basePath)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitTaskAsync(api, owner, Loc.T("CephTab_AddPool"),
                [
                    new FormField { Key = "name", LabelKey = "Table_Name", Required = true },
                    new FormField { Key = "size", LabelKey = "CephTab_Size", Initial = "3" },
                    new FormField { Key = "min_size", LabelKey = "CephTab_MinSize", Initial = "2" },
                    new FormField
                    {
                        Key = "pg_autoscale_mode", LabelKey = "CephTab_Autoscale", Kind = FormFieldKind.Choice,
                        Initial = "on", Choices = [("on", "on"), ("warn", "warn"), ("off", "off")]
                    },
                    new FormField
                    {
                        Key = "add_storages", LabelKey = "NodeDisks_AddStorage", Kind = FormFieldKind.Bool,
                        Initial = "1"
                    }
                ], values => api.PostActionAsync($"{basePath}/pool", NonEmpty(values)), "CephTab_PoolCreated")
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Run = (row, owner) =>
                {
                    var name = row!["pool_name"];
                    return SubmitTaskAsync(api, owner, Loc.T("CephTab_DeletePoolTitle", name),
                    [
                        new FormField
                        {
                            Key = "remove_storages", LabelKey = "NodeDisks_CleanupConfig", Kind = FormFieldKind.Bool,
                            Initial = "1"
                        },
                        TypeToConfirmField()
                    ], values => api.DeleteActionAsync(
                        $"{basePath}/pool/{Seg(name)}?remove_storages={values["remove_storages"]}"),
                        "CephTab_PoolDeleted", TypedMatches(name));
                }
            }
        ];
    }
}
