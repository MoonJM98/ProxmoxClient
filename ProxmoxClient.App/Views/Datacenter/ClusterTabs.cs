using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>데이터센터의 클러스터 구성·SDN·리소스 매핑·Ceph 상태 화면.</summary>
internal static class ClusterTabs
{
    private static readonly IReadOnlyList<TableColumn> ClusterNodeColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "nodeid", HeaderKey = "Table_NodeId", Width = 80 },
        new() { Key = "quorum_votes", HeaderKey = "DcCluster_Votes", Width = 80 },
        new() { Key = "ring0_addr", HeaderKey = "DcCluster_Link0", Width = 160 },
        new() { Key = "ring1_addr", HeaderKey = "DcCluster_Link1", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> ZoneColumns =
    [
        new() { Key = "zone", HeaderKey = "Table_Name", Width = 140 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "bridge", HeaderKey = "DcSdn_Bridge", Width = 110 },
        new() { Key = "nodes", HeaderKey = "Table_Nodes", Width = 0 },
        new() { Key = "mtu", HeaderKey = "NodeNetwork_Mtu", Width = 70 },
        new() { Key = "state", HeaderKey = "Table_State", Width = 90 }
    ];

    private static readonly IReadOnlyList<TableColumn> VnetColumns =
    [
        new() { Key = "vnet", HeaderKey = "Table_Name", Width = 140 },
        new() { Key = "zone", HeaderKey = "DcSdn_Zone", Width = 120 },
        new() { Key = "tag", HeaderKey = "DcSdn_Tag", Width = 80 },
        new() { Key = "alias", HeaderKey = "DcSdn_Alias", Width = 0 },
        new() { Key = "vlanaware", HeaderKey = "NodeNetwork_VlanAware", Width = 80, Format = TableFormats.Flag },
        new() { Key = "state", HeaderKey = "Table_State", Width = 90 }
    ];

    private static readonly IReadOnlyList<TableColumn> SdnStatusColumns =
    [
        new() { Key = "sdn", HeaderKey = "DcSdn_Zone", Width = 140 },
        new() { Key = "node", HeaderKey = "Table_Node", Width = 140 },
        new() { Key = "status", HeaderKey = "Table_State", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> MappingColumns =
    [
        new() { Key = "id", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "map", HeaderKey = "DcMapping_Map", Width = 0 },
        new() { Key = "description", HeaderKey = "Table_Description", Width = 200 }
    ];

    // ------------------------------------------------------------ 클러스터

    /// <summary>Corosync totem 설정·QDevice 상태 보기(읽기 전용).</summary>
    private static IEnumerable<TableAction> ClusterViews(ProxmoxApiClient api)
    {
        yield return new TableAction
        {
            LabelKey = "DcCluster_Totem", IconKey = "IconSettings",
            Run = async (_, owner) => TextViewWindow.ShowModal(owner, Loc.T("DcCluster_Totem"),
                await api.Cluster.TotemJsonAsync())
        };
        yield return new TableAction
        {
            LabelKey = "DcCluster_QDevice", IconKey = "IconServer",
            Run = async (_, owner) => TextViewWindow.ShowModal(owner, Loc.T("DcCluster_QDevice"),
                await api.Cluster.QDeviceJsonAsync())
        };
    }

    public static TableTab Cluster(ProxmoxApiClient api, bool canEdit)
    {
        var actions = new List<TableAction>
        {
            new()
            {
                LabelKey = "DcCluster_JoinInfo", IconKey = "IconCopy",
                Run = async (_, owner) =>
                {
                    var info = await api.GetClusterJoinInfoAsync();
                    if (info is null) return Loc.T("DcCluster_NotCluster");

                    return TextViewWindow.ShowModal(owner, Loc.T("DcCluster_JoinInfo"),
                        Loc.T("DcCluster_JoinInfoText", info.IpAddress, info.Fingerprint, info.Encoded));
                }
            }
        };
        actions.AddRange(ClusterViews(api));

        if (canEdit)
        {
            actions.Add(new TableAction
            {
                LabelKey = "DcCluster_Create", IconKey = "IconPlus",
                Run = (_, owner) => SubmitTaskAsync(api, owner, Loc.T("DcCluster_Create"),
                [
                    new FormField { Key = "clustername", LabelKey = "DcCluster_Name", Required = true, Trim = true },
                    new FormField { Key = "link0", LabelKey = "DcCluster_Link0", Trim = true,
                        Hint = Loc.T("DcCluster_LinkHint") },
                    // 여분 링크(웹 UI 의 '링크 추가') — corosync 는 링크를 최대 8개까지 쓴다
                    new FormField { Key = "link1", LabelKey = "DcCluster_Link1", Trim = true, Advanced = true },
                    new FormField { Key = "link2", LabelKey = "DcCluster_Link2", Trim = true, Advanced = true },
                    new FormField { Key = "link3", LabelKey = "DcCluster_Link3", Trim = true, Advanced = true }
                ], values => api.Cluster.CreateAsync(NonEmpty(values)), "DcCluster_Created")
            });
            actions.Add(new TableAction
            {
                LabelKey = "DcCluster_Join", IconKey = "IconLogIn",
                Run = (_, owner) => JoinAsync(api, owner)
            });
        }

        return new TableTab(() => api.Cluster.ConfigNodesAsync(), ClusterNodeColumns, "DcCluster_Hint",
            actions);
    }

    /// <summary>
    ///     이 노드를 다른 클러스터에 가입시킨다 — 그 클러스터의 가입 정보와 root 암호가 필요하다.
    ///     가입 정보에 든 링크(peerLinks)마다 이 노드의 주소를 받는다(웹 UI 와 같다 — link0 만 보내면
    ///     링크가 여러 개인 클러스터에는 가입할 수 없다). 게스트가 없는 새 노드에서만 한다(서버도 확인한다).
    /// </summary>
    private static async Task<string?> JoinAsync(ProxmoxApiClient api, Window? owner)
    {
        var first = new FormDialog(Loc.T("DcCluster_Join"),
        [
            new FormField { Key = "info", LabelKey = "DcCluster_JoinInfo", Kind = FormFieldKind.Multiline,
                Required = true },
            new FormField { Key = "password", LabelKey = "DcCluster_PeerPassword", Kind = FormFieldKind.Password,
                Required = true }
        ], values => ProxmoxApiClient.ParseClusterJoinInfo(values["info"]) is null
            ? Loc.T("DcCluster_BadJoinInfo")
            : null) { Owner = owner };
        if (first.ShowDialog() != true || first.Result is not { } entered) return null;

        var info = ProxmoxApiClient.ParseClusterJoinInfo(entered["info"])!;
        var links = info.PeerLinks.Count > 0 ? info.PeerLinks : new Dictionary<int, string> { [0] = info.IpAddress };
        var multi = links.Count > 1;
        return await SubmitTaskAsync(api, owner, Loc.T("DcCluster_JoinLinks", info.IpAddress),
            links.Select(link => new FormField
            {
                Key = $"link{link.Key}", LabelKey = Loc.T("DcCluster_LinkN", link.Key), Trim = true,
                Required = multi, Hint = Loc.T("DcCluster_PeerLinkHint", link.Value)
            }).ToList(), values =>
            {
                var form = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["hostname"] = info.IpAddress, ["fingerprint"] = info.Fingerprint,
                    ["password"] = entered["password"]
                };
                foreach (var (key, value) in values.Where(kv => kv.Value.Length > 0)) form[key] = value;
                return api.Cluster.JoinAsync(form);
            }, "DcCluster_Joined");
    }

    // ------------------------------------------------------------ SDN

    public static SubTabsView Sdn(ProxmoxApiClient api, bool canEdit)
    {
        var apply = new TableAction
        {
            LabelKey = "NodeNetwork_Apply", IconKey = "IconCheck",
            Confirm = _ => Loc.T("DcSdn_ApplyConfirm"),
            Run = async (_, _) => await RunTaskAsync(api,
                api.Sdn.ApplyAsync(), "DcSdn_Applied")
        };
        IReadOnlyList<TableAction> pending = [apply, ..SdnPending.Actions(api)];

        return new SubTabsView(new List<SubTab>
        {
            // 노드마다 영역이 제대로 적용됐는지(웹 UI 의 SDN 상태) — 적용 뒤 확인용
            new("DcSdn_Status", () => new TableTab(() => api.Cluster.ResourcesAsync("sdn"),
                SdnStatusColumns, "DcSdn_StatusHint")),
            new("DcSdn_Zones", () => new TableTab(() => api.Sdn.ListAsync("zones", pending: true), ZoneColumns,
                "DcSdn_ZonesHint", canEdit ? [..SdnActions.Zones(api), ..pending] : null)),
            new("DcSdn_Vnets", () => new TableTab(() => api.Sdn.ListAsync("vnets", pending: true), VnetColumns,
                "DcSdn_VnetsHint",
                canEdit ? [..SdnActions.Vnets(api), SdnPending.VnetFirewall(api), ..pending] : null)),
            new("DcSdn_Controllers", () => SdnExtras.Controllers(api, canEdit)),
            new("DcSdn_Ipam", () => SdnExtras.Ipams(api, canEdit)),
            new("DcSdn_Dns", () => SdnExtras.Dns(api, canEdit)),
            new("DcSdn_Fabrics", () => new TableTab(() => api.Sdn.ListFabricsAsync(), SdnFabrics.FabricColumns,
                    "DcSdn_FabricsHint", canEdit ? [..SdnFabrics.Fabrics(api), ..pending] : null),
                api.Sdn.Feature(nameof(SdnApi.ListFabricsAsync))),
            SdnRouting.Tab(api, canEdit)
        });
    }

    // ------------------------------------------------------------ 리소스 매핑

    /// <summary>
    ///     PCI·USB·디렉터리 매핑 — 이름 하나로 여러 노드의 같은 장치를 묶어 게스트가 어느 노드에서든 쓰게 한다.
    ///     노드마다 한 줄씩 "node=…,path=…" 형식으로 적는다. 디렉터리 매핑(8.3+)은 서버가 알 때만 탭을 둔다.
    /// </summary>
    public static SubTabsView Mappings(ProxmoxApiClient api, bool canEdit)
    {
        TableTab Mapping(string kind, string hintKey) =>
            new(() => api.Mappings.ListAsync(kind), MappingColumns, hintKey,
                canEdit ? MappingActions(api, kind) : null);

        return new SubTabsView(new List<SubTab>
        {
            new("DcMapping_Pci", () => Mapping("pci", "DcMapping_PciHint"), api.Mappings.KindFeature("pci")),
            new("DcMapping_Usb", () => Mapping("usb", "DcMapping_UsbHint"), api.Mappings.KindFeature("usb")),
            new("DcMapping_Dir", () => Mapping("dir", "DcMapping_DirHint"), api.Mappings.KindFeature("dir"))
        });
    }

    /// <summary>
    ///     매핑 추가·수정 — 노드마다 한 줄("node=…,path=…"/"node=…,id=…"), 설명. PCI 는 중재 장치(mdev)와
    ///     실시간 이전 가능 여부도. 수정은 서버 설정을 읽어 채우고 map 을 통째로 바꾼다.
    /// </summary>
    private static IReadOnlyList<TableAction> MappingActions(ProxmoxApiClient api, string kind)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitAsync(owner, "DcMapping_AddTitle", MappingFields(kind, null),
                    values => api.Mappings.CreateAsync(kind, values),
                    "DcMapping_Added")
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = async (row, owner) =>
                {
                    var id = row!["id"];
                    var config = await api.Mappings.GetAsync(kind, id);
                    return await SubmitAsync(owner, Loc.T("DcMapping_EditTitle", id), MappingFields(kind, config),
                        values => api.Mappings.UpdateAsync(kind, id, values), "DcMapping_Updated",
                        titleIsKey: false);
                }
            },
            DeleteAction(row => Loc.T("DcMapping_DeleteConfirm", row["id"]),
                row => api.Mappings.DeleteAsync(kind, row["id"]), "DcMapping_Deleted")
        ];
    }

    private static List<FormField> MappingFields(string kind, IReadOnlyDictionary<string, string>? config)
    {
        string V(string key) => config is not null && config.TryGetValue(key, out var v) ? v : string.Empty;
        var fields = new List<FormField>();
        if (config is null)
            fields.Add(new FormField { Key = "id", LabelKey = "Table_Name", Required = true, Trim = true });
        fields.Add(new FormField
        {
            Key = "map", Kind = FormFieldKind.Multiline, Required = true, Initial = V("map"),
            LabelKey = kind switch
            {
                "pci" => "DcMapping_MapHintPci",
                "usb" => "DcMapping_MapHintUsb",
                _ => "DcMapping_MapHintDir"
            }
        });
        if (kind == "pci")
        {
            fields.Add(new FormField { Key = "mdev", LabelKey = "DcMapping_Mdev", Kind = FormFieldKind.Bool,
                Initial = V("mdev") is "1" ? "1" : "0", Hint = Loc.T("DcMapping_MdevHint") });
            fields.Add(new FormField { Key = "live-migration-capable", LabelKey = "DcMapping_LiveMigration",
                Kind = FormFieldKind.Bool, Initial = V("live-migration-capable") is "1" ? "1" : "0",
                Advanced = true, Hint = Loc.T("DcMapping_LiveMigrationHint") });
        }

        fields.Add(new FormField { Key = "description", LabelKey = "Table_Description", Initial = V("description") });
        return fields;
    }

    // ------------------------------------------------------------ Ceph

    /// <summary>Ceph 상태(읽기 전용) — 설치·OSD·풀 관리는 노드 셸의 pveceph 나 웹 UI 에서 한다.</summary>
    public static TextEditTab CephStatus(ProxmoxApiClient api)
    {
        return new TextEditTab(async () => (await api.Cluster.CephStatusJsonAsync(), string.Empty), null,
            "DcCeph_Hint");
    }
}
