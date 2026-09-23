using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Node;

/// <summary>
///     Ceph 구성 요소 만들기·없애기 — OSD, 모니터, 매니저, CephFS·MDS, 그리고 설치(노드 셸에서 실행).
///     없애는 작업은 이름을 직접 입력해야 한다.
/// </summary>
internal static class CephManage
{
    private static readonly IReadOnlyList<TableColumn> ServiceColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 120 },
        new() { Key = "host", HeaderKey = "Table_Node", Width = 120 },
        new() { Key = "addr", HeaderKey = "Table_Address", Width = 0 },
        new() { Key = "state", HeaderKey = "Table_State", Width = 100 },
        new() { Key = "ceph_version_short", HeaderKey = "CephTab_Version", Width = 100 }
    ];

    private static readonly IReadOnlyList<TableColumn> FsColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "metadata_pool", HeaderKey = "CephTab_MetadataPool", Width = 160 },
        new() { Key = "data_pool", HeaderKey = "CephTab_DataPool", Width = 0 }
    ];

    /// <summary>Ceph 설치 — 웹 UI 처럼 노드 셸에서 설치 마법사(pveceph install)를 띄운다.</summary>
    public static TableAction Install(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "CephTab_Install", IconKey = "IconTerminal",
            Run = (_, _) =>
            {
                new TerminalWindow(api, node, "ceph_install").Show();
                return Task.FromResult<string?>(Loc.T("CephTab_InstallOpened"));
            }
        };
    }

    public static IReadOnlyList<TableAction> OsdLifecycle(ProxmoxApiClient api, string node, string basePath)
    {
        return
        [
            new TableAction
            {
                LabelKey = "CephTab_CreateOsd", IconKey = "IconPlus",
                Run = (_, owner) => CreateOsdAsync(api, node, basePath, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Run = (row, owner) =>
                {
                    var osd = row!;
                    var name = Value(osd, "name");
                    var host = HostPath(osd, node);
                    return SubmitTaskAsync(api, owner, Loc.T("CephTab_DeleteOsdTitle", name),
                    [
                        new FormField
                        {
                            Key = "cleanup", LabelKey = "NodeDisks_CleanupDisks", Kind = FormFieldKind.Bool,
                            Initial = "1"
                        },
                        TypeToConfirmField()
                    ], values => api.DeleteActionAsync(
                        $"{host}/osd/{Seg(Value(osd, "id"))}?cleanup={values["cleanup"]}"),
                        "CephTab_OsdDeleted", TypedMatches(name));
                }
            }
        ];
    }

    private static async Task<string?> CreateOsdAsync(ProxmoxApiClient api, string node, string basePath,
        Window? owner)
    {
        var disks = (await api.GetTableAsync($"nodes/{Seg(node)}/disks/list?type=unused"))
            .Select(d => (Value(d, "devpath"), $"{Value(d, "devpath")}  {Value(d, "model")}".Trim()))
            .ToList();

        return await SubmitTaskAsync(api, owner, Loc.T("CephTab_CreateOsd"),
        [
            new FormField
            {
                Key = "dev", LabelKey = "Table_Device", Kind = FormFieldKind.Choice, Choices = disks, Required = true
            },
            new FormField
            {
                Key = "crush-device-class", LabelKey = "CephTab_Class", Kind = FormFieldKind.Choice, Initial = "",
                Choices = [("", "CephTab_ClassAuto"), ("hdd", "HDD"), ("ssd", "SSD"), ("nvme", "NVMe")]
            },
            new FormField { Key = "encrypted", LabelKey = "CephTab_Encrypted", Kind = FormFieldKind.Bool }
        ], values => api.PostActionAsync($"{basePath}/osd", NonEmpty(values)), "CephTab_OsdCreated");
    }

    /// <summary>모니터·매니저 — 이 노드 이름으로 하나씩 만든다(웹 UI 기본값과 같다).</summary>
    public static TableTab Service(ProxmoxApiClient api, string node, string basePath, string kind, string hintKey,
        bool canEdit, IReadOnlyList<TableAction>? extra = null)
    {
        return new TableTab(() => api.GetTableAsync($"{basePath}/{kind}"), ServiceColumns, hintKey, canEdit
            ?
            [
                ..extra ?? [],
                new TableAction
                {
                    LabelKey = kind == "mon" ? "CephTab_CreateMon" : "CephTab_CreateMgr", IconKey = "IconPlus",
                    Confirm = _ => Loc.T("CephTab_CreateServiceConfirm", node),
                    Run = async (_, _) => await RunTaskAsync(api,
                        api.PostActionAsync($"{basePath}/{kind}/{Seg(node)}"), "CephTab_ServiceCreated")
                },
                new TableAction
                {
                    LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                    Run = (row, owner) =>
                    {
                        var name = Value(row!, "name");
                        var host = HostPath(row!, node);
                        return SubmitTaskAsync(api, owner, Loc.T("CephTab_DeleteServiceTitle", name),
                            [TypeToConfirmField()],
                            _ => api.DeleteActionAsync($"{host}/{kind}/{Seg(name)}"),
                            "CephTab_ServiceDeleted", TypedMatches(name));
                    }
                }
            ]
            : extra);
    }

    /// <summary>
    ///     서비스·OSD 가 도는 호스트의 ceph 경로 — 목록은 클러스터 전체를 보여 주지만, 없애기는 그 호스트에 요청해야
    ///     서비스와 디스크까지 정리된다. 행에 호스트가 없으면 지금 노드를 쓴다.
    /// </summary>
    private static string HostPath(IReadOnlyDictionary<string, string> row, string node)
    {
        var host = Value(row, "host");
        return $"nodes/{Seg(host.Length > 0 ? host : node)}/ceph";
    }

    /// <summary>CephFS — 파일 시스템 목록과 만들기. 먼저 MDS(메타데이터 서버)가 있어야 한다.</summary>
    public static TableTab FileSystems(ProxmoxApiClient api, string node, string basePath, bool canEdit)
    {
        return new TableTab(() => api.GetTableAsync($"{basePath}/fs"), FsColumns, "CephTab_FsHint", canEdit
            ?
            [
                new TableAction
                {
                    LabelKey = "CephTab_CreateMds", IconKey = "IconServer",
                    Confirm = _ => Loc.T("CephTab_CreateServiceConfirm", node),
                    Run = async (_, _) => await RunTaskAsync(api,
                        api.PostActionAsync($"{basePath}/mds/{Seg(node)}"), "CephTab_ServiceCreated")
                },
                new TableAction
                {
                    LabelKey = "CephTab_CreateFs", IconKey = "IconPlus",
                    Run = (_, owner) => SubmitTaskAsync(api, owner, Loc.T("CephTab_CreateFs"),
                    [
                        new FormField { Key = "name", LabelKey = "Table_Name", Required = true, Initial = "cephfs" },
                        new FormField { Key = "pg_num", LabelKey = "CephTab_PgNum", Initial = "128" },
                        new FormField
                        {
                            Key = "add-storage", LabelKey = "NodeDisks_AddStorage", Kind = FormFieldKind.Bool,
                            Initial = "1"
                        }
                    ], values =>
                    {
                        var form = NonEmpty(values);
                        form.Remove("name");
                        return api.PostActionAsync($"{basePath}/fs/{Seg(values["name"])}", form);
                    }, "CephTab_FsCreated")
                }
            ]
            : null);
    }
}
