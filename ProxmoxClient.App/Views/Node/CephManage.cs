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
                Services.ConsoleWindows.ShowNodeShell(api, node, "ceph_install");
                return Task.FromResult<string?>(Loc.T("CephTab_InstallOpened"));
            }
        };
    }

    /// <summary>
    ///     Ceph 첫 설정 — 설치 뒤 처음 한 번, 공용·클러스터 네트워크와 복제 수를 정해 ceph.conf 를 만든다(웹 UI 설치
    ///     마법사의 설정 단계).
    /// </summary>
    public static TableAction Init(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "CephInit_Action", IconKey = "IconSettings",
            Run = (_, owner) => SubmitAsync(owner, "CephInit_Action",
            [
                new FormField { Key = "network", LabelKey = "CephInit_Network", Trim = true,
                    Hint = Loc.T("CephInit_NetworkHint") },
                new FormField { Key = "cluster-network", LabelKey = "CephInit_ClusterNetwork", Trim = true,
                    Advanced = true, Hint = Loc.T("CephInit_ClusterNetworkHint") },
                new FormField { Key = "size", LabelKey = "CephTab_Size", Initial = "3", Trim = true },
                new FormField { Key = "min_size", LabelKey = "CephTab_MinSize", Initial = "2", Trim = true }
            ], async values =>
            {
                await api.Ceph.InitAsync(node, NonEmpty(values));
                return string.Empty;
            }, "CephInit_Done")
        };
    }

    public static IReadOnlyList<TableAction> OsdLifecycle(ProxmoxApiClient api, string node)
    {
        return
        [
            new TableAction
            {
                LabelKey = "CephTab_CreateOsd", IconKey = "IconPlus",
                Run = (_, owner) => CreateOsdAsync(api, node, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Run = async (row, owner) =>
                {
                    var osd = row!;
                    var name = Value(osd, "name");
                    var host = HostOf(osd, node);
                    if (!await CephExtras.ConfirmSafeAsync(api, host, $"osd.{Value(osd, "id")}", "destroy", owner))
                        return null;
                    return await SubmitTaskAsync(api, owner, Loc.T("CephTab_DeleteOsdTitle", name),
                    [
                        new FormField
                        {
                            Key = "cleanup", LabelKey = "NodeDisks_CleanupDisks", Kind = FormFieldKind.Bool,
                            Initial = "1"
                        },
                        TypeToConfirmField()
                    ], values => api.Ceph.DeleteOsdAsync(host, Value(osd, "id"), values["cleanup"] == "1"),
                        "CephTab_OsdDeleted", TypedMatches(name));
                }
            }
        ];
    }

    private static async Task<string?> CreateOsdAsync(ProxmoxApiClient api, string node, Window? owner)
    {
        var disks = (await api.Disks.ListAsync(node, unusedOnly: true))
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
        ], values => api.Ceph.CreateOsdAsync(node, NonEmpty(values)), "CephTab_OsdCreated");
    }

    /// <summary>모니터·매니저 — 이 노드 이름으로 하나씩 만든다(웹 UI 기본값과 같다).</summary>
    public static TableTab Service(ProxmoxApiClient api, string node, string kind, string hintKey,
        bool canEdit, IReadOnlyList<TableAction>? extra = null)
    {
        return new TableTab(() => CephTabs.Guard(() => api.Ceph.ListServicesAsync(node, kind)),
            ServiceColumns, hintKey, canEdit
            ?
            [
                ..extra ?? [],
                ..CephExtras.DaemonControls(api, node, row => $"{kind}.{Value(row, "name")}"),
                new TableAction
                {
                    LabelKey = kind == "mon" ? "CephTab_CreateMon" : "CephTab_CreateMgr", IconKey = "IconPlus",
                    Confirm = _ => Loc.T("CephTab_CreateServiceConfirm", node),
                    Run = async (_, _) => await RunTaskAsync(api,
                        api.Ceph.CreateServiceAsync(node, kind), "CephTab_ServiceCreated")
                },
                new TableAction
                {
                    LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                    Run = (row, owner) =>
                    {
                        var name = Value(row!, "name");
                        var host = HostOf(row!, node);
                        return SubmitTaskAsync(api, owner, Loc.T("CephTab_DeleteServiceTitle", name),
                            [TypeToConfirmField()],
                            _ => api.Ceph.DeleteServiceAsync(host, kind, name),
                            "CephTab_ServiceDeleted", TypedMatches(name));
                    }
                }
            ]
            : extra);
    }

    /// <summary>
    ///     서비스·OSD 가 도는 호스트 — 목록은 클러스터 전체를 보여 주지만, 없애기는 그 호스트에 요청해야
    ///     서비스와 디스크까지 정리된다. 행에 호스트가 없으면 지금 노드를 쓴다.
    /// </summary>
    internal static string HostOf(IReadOnlyDictionary<string, string> row, string node)
    {
        var host = Value(row, "host");
        return host.Length > 0 ? host : node;
    }

    /// <summary>CephFS — 파일 시스템 목록과 만들기. 먼저 MDS(메타데이터 서버)가 있어야 한다.</summary>
    public static TableTab FileSystems(ProxmoxApiClient api, string node, bool canEdit)
    {
        return new TableTab(() => CephTabs.Guard(() => api.Ceph.ListFileSystemsAsync(node)),
            FsColumns, "CephTab_FsHint", canEdit
            ?
            [
                new TableAction
                {
                    LabelKey = "CephTab_CreateMds", IconKey = "IconServer",
                    Confirm = _ => Loc.T("CephTab_CreateServiceConfirm", node),
                    Run = async (_, _) => await RunTaskAsync(api,
                        api.Ceph.CreateServiceAsync(node, "mds"), "CephTab_ServiceCreated")
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
                        return api.Ceph.CreateFileSystemAsync(node, values["name"], form);
                    }, "CephTab_FsCreated")
                },
                CephMaintenance.DeleteFileSystem(api, node)
            ]
            : null);
    }
}
