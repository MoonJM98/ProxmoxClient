using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
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

    private static readonly IReadOnlyList<TableColumn> SubnetColumns =
    [
        new() { Key = "cidr", HeaderKey = "Table_Cidr", Width = 180 },
        new() { Key = "gateway", HeaderKey = "Table_Gateway", Width = 150 },
        new() { Key = "snat", HeaderKey = "DcSdn_Snat", Width = 60, Format = TableFormats.Flag },
        new() { Key = "state", HeaderKey = "Table_State", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> MappingColumns =
    [
        new() { Key = "id", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "map", HeaderKey = "DcMapping_Map", Width = 0 },
        new() { Key = "description", HeaderKey = "Table_Description", Width = 200 }
    ];

    // ------------------------------------------------------------ 클러스터

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

        if (canEdit)
        {
            actions.Add(new TableAction
            {
                LabelKey = "DcCluster_Create", IconKey = "IconPlus",
                Run = (_, owner) => SubmitTaskAsync(api, owner, Loc.T("DcCluster_Create"),
                [
                    new FormField { Key = "clustername", LabelKey = "DcCluster_Name", Required = true },
                    new FormField { Key = "link0", LabelKey = "DcCluster_Link0" }
                ], values => api.PostActionAsync("cluster/config", NonEmpty(values)), "DcCluster_Created")
            });
            actions.Add(new TableAction
            {
                LabelKey = "DcCluster_Join", IconKey = "IconLogIn",
                Run = (_, owner) => JoinAsync(api, owner)
            });
        }

        return new TableTab(() => api.GetTableAsync("cluster/config/nodes"), ClusterNodeColumns, "DcCluster_Hint",
            actions);
    }

    /// <summary>
    ///     이 노드를 다른 클러스터에 가입시킨다 — 그 클러스터의 가입 정보와 root 암호가 필요하다.
    ///     가입하면 이 노드의 게스트 설정이 바뀌므로 게스트가 없는 새 노드에서만 한다(서버도 확인한다).
    /// </summary>
    private static Task<string?> JoinAsync(ProxmoxApiClient api, Window? owner)
    {
        return SubmitTaskAsync(api, owner, Loc.T("DcCluster_Join"),
        [
            new FormField
            {
                Key = "info", LabelKey = "DcCluster_JoinInfo", Kind = FormFieldKind.Multiline, Required = true
            },
            new FormField
            {
                Key = "password", LabelKey = "DcCluster_PeerPassword", Kind = FormFieldKind.Password, Required = true
            },
            new FormField { Key = "link0", LabelKey = "DcCluster_Link0" }
        ], values =>
        {
            var info = ProxmoxApiClient.ParseClusterJoinInfo(values["info"])!;
            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hostname"] = info.IpAddress, ["fingerprint"] = info.Fingerprint, ["password"] = values["password"]
            };
            if (values["link0"].Length > 0) form["link0"] = values["link0"];
            return api.PostActionAsync("cluster/config/join", form);
        }, "DcCluster_Joined",
            values => ProxmoxApiClient.ParseClusterJoinInfo(values["info"]) is null
                ? Loc.T("DcCluster_BadJoinInfo")
                : null);
    }

    // ------------------------------------------------------------ SDN

    public static SubTabsView Sdn(ProxmoxApiClient api, bool canEdit)
    {
        var apply = new TableAction
        {
            LabelKey = "NodeNetwork_Apply", IconKey = "IconCheck",
            Confirm = _ => Loc.T("DcSdn_ApplyConfirm"),
            Run = async (_, _) => await RunTaskAsync(api,
                api.PutActionAsync("cluster/sdn", new Dictionary<string, string>()), "DcSdn_Applied")
        };

        return new SubTabsView(
        [
            ("DcSdn_Zones", () => new TableTab(() => api.GetTableAsync("cluster/sdn/zones?pending=1"), ZoneColumns,
                "DcSdn_ZonesHint", canEdit ? [..ZoneActions(api), apply] : null)),
            ("DcSdn_Vnets", () => new TableTab(() => api.GetTableAsync("cluster/sdn/vnets?pending=1"), VnetColumns,
                "DcSdn_VnetsHint", canEdit ? [..VnetActions(api), apply] : null)),
            ("DcSdn_Controllers", () => SdnExtras.Controllers(api, canEdit)),
            ("DcSdn_Ipam", () => SdnExtras.Ipams(api, canEdit)),
            ("DcSdn_Dns", () => SdnExtras.Dns(api, canEdit))
        ]);
    }

    private static IReadOnlyList<TableAction> ZoneActions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddZoneAsync(api, owner)
            },
            DeleteAction(row => Loc.T("DcSdn_DeleteConfirm", row["zone"]),
                row => api.DeleteActionAsync($"cluster/sdn/zones/{Seg(row["zone"])}"), "DcSdn_Deleted")
        ];
    }

    /// <summary>SDN 영역 — Simple·VLAN·QinQ·VXLAN·EVPN. EVPN 은 먼저 EVPN 컨트롤러를 만들어 둬야 한다.</summary>
    private static async Task<string?> AddZoneAsync(ProxmoxApiClient api, Window? owner)
    {
        var choose = new FormDialog(Loc.T("DcSdn_AddZone"),
        [
            new FormField
            {
                Key = "type", LabelKey = "Table_Type", Kind = FormFieldKind.Choice, Initial = "simple",
                Choices =
                [
                    ("simple", "Simple"), ("vlan", "VLAN"), ("qinq", "QinQ"), ("vxlan", "VXLAN"), ("evpn", "EVPN")
                ]
            }
        ]) { Owner = owner };
        if (choose.ShowDialog() != true || choose.Result is not { } picked) return null;

        var type = picked["type"];
        var fields = new List<FormField> { new() { Key = "zone", LabelKey = "Table_Name", Required = true } };
        if (type is "vlan" or "qinq")
            fields.Add(new FormField { Key = "bridge", LabelKey = "DcSdn_Bridge", Required = true, Initial = "vmbr0" });
        if (type == "qinq") fields.Add(new FormField { Key = "tag", LabelKey = "DcSdn_Tag", Required = true });
        if (type == "vxlan") fields.Add(new FormField { Key = "peers", LabelKey = "DcSdn_Peers", Required = true });
        if (type == "evpn")
        {
            var controllers = (await api.GetTableAsync("cluster/sdn/controllers"))
                .Where(c => Value(c, "type") == "evpn")
                .Select(c => (Value(c, "controller"), Value(c, "controller")))
                .ToList();
            fields.Add(new FormField
            {
                Key = "controller", LabelKey = "DcSdn_Controller", Kind = FormFieldKind.Choice, Choices = controllers,
                Required = true
            });
            fields.Add(new FormField { Key = "vrf-vxlan", LabelKey = "DcSdn_VrfVxlan", Required = true });
            fields.Add(new FormField { Key = "exitnodes", LabelKey = "DcSdn_ExitNodes" });
        }

        fields.Add(new FormField { Key = "mtu", LabelKey = "NodeNetwork_Mtu" });
        fields.Add(new FormField { Key = "nodes", LabelKey = "DcStorage_Nodes" });

        return await SubmitAsync(owner, Loc.T("DcSdn_AddZoneType", type), fields, values =>
        {
            var form = NonEmpty(values);
            form["type"] = type;
            return api.PostActionAsync("cluster/sdn/zones", form);
        }, "DcSdn_Added", titleIsKey: false);
    }

    private static IReadOnlyList<TableAction> VnetActions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = async (_, owner) =>
                {
                    var zones = (await api.GetTableAsync("cluster/sdn/zones"))
                        .Select(z => (Value(z, "zone"), Value(z, "zone")))
                        .ToList();
                    return await SubmitAsync(owner, "DcSdn_AddVnet",
                    [
                        new FormField { Key = "vnet", LabelKey = "Table_Name", Required = true },
                        new FormField
                        {
                            Key = "zone", LabelKey = "DcSdn_Zone", Kind = FormFieldKind.Choice, Choices = zones,
                            Required = true
                        },
                        new FormField { Key = "tag", LabelKey = "DcSdn_Tag" },
                        new FormField { Key = "alias", LabelKey = "DcSdn_Alias" },
                        new FormField
                        {
                            Key = "vlanaware", LabelKey = "NodeNetwork_VlanAware", Kind = FormFieldKind.Bool
                        }
                    ], values => api.PostActionAsync("cluster/sdn/vnets", NonEmpty(values)), "DcSdn_Added");
                }
            },
            new TableAction
            {
                LabelKey = "DcSdn_Subnets", IconKey = "IconList", NeedsSelection = true,
                Run = (row, owner) =>
                {
                    var path = $"cluster/sdn/vnets/{Seg(row!["vnet"])}/subnets";
                    return Task.FromResult(TableWindow.ShowModal(owner, Loc.T("DcSdn_SubnetsTitle", row["vnet"]),
                        new TableTab(() => api.GetTableAsync($"{path}?pending=1"), SubnetColumns, "DcSdn_SubnetsHint",
                            SubnetActions(api, path))));
                }
            },
            DeleteAction(row => Loc.T("DcSdn_DeleteConfirm", row["vnet"]),
                row => api.DeleteActionAsync($"cluster/sdn/vnets/{Seg(row["vnet"])}"), "DcSdn_Deleted")
        ];
    }

    private static IReadOnlyList<TableAction> SubnetActions(ProxmoxApiClient api, string path)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitAsync(owner, "DcSdn_AddSubnet",
                [
                    new FormField { Key = "subnet", LabelKey = "Table_Cidr", Required = true },
                    new FormField { Key = "gateway", LabelKey = "Table_Gateway" },
                    new FormField { Key = "snat", LabelKey = "DcSdn_Snat", Kind = FormFieldKind.Bool }
                ], values =>
                {
                    var form = NonEmpty(values);
                    form["type"] = "subnet";
                    return api.PostActionAsync(path, form);
                }, "DcSdn_Added")
            },
            // 서브넷 ID 는 "영역-주소-접두어" 형식이라 행의 subnet(ID) 필드로 지운다
            DeleteAction(row => Loc.T("DcSdn_DeleteConfirm", Value(row, "cidr")),
                row => api.DeleteActionAsync($"{path}/{Seg(Value(row, "subnet"))}"), "DcSdn_Deleted")
        ];
    }

    // ------------------------------------------------------------ 리소스 매핑

    /// <summary>
    ///     PCI·USB·디렉터리 매핑 — 이름 하나로 여러 노드의 같은 장치를 묶어 게스트가 어느 노드에서든 쓰게 한다.
    ///     노드마다 한 줄씩 "node=…,path=…" 형식으로 적는다.
    /// </summary>
    public static SubTabsView Mappings(ProxmoxApiClient api, bool canEdit)
    {
        TableTab Mapping(string kind, string hintKey) =>
            new(() => api.GetTableAsync($"cluster/mapping/{kind}"), MappingColumns, hintKey,
                canEdit ? MappingActions(api, kind) : null);

        return new SubTabsView(
        [
            ("DcMapping_Pci", () => Mapping("pci", "DcMapping_PciHint")),
            ("DcMapping_Usb", () => Mapping("usb", "DcMapping_UsbHint")),
            ("DcMapping_Dir", () => Mapping("dir", "DcMapping_DirHint"))
        ]);
    }

    private static IReadOnlyList<TableAction> MappingActions(ProxmoxApiClient api, string kind)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitAsync(owner, "DcMapping_AddTitle",
                [
                    new FormField { Key = "id", LabelKey = "Table_Name", Required = true },
                    new FormField
                    {
                        Key = "map", LabelKey = kind switch
                        {
                            "pci" => "DcMapping_MapHintPci",
                            "usb" => "DcMapping_MapHintUsb",
                            _ => "DcMapping_MapHintDir"
                        },
                        Kind = FormFieldKind.Multiline, Required = true
                    },
                    new FormField { Key = "description", LabelKey = "Table_Description" }
                ], values =>
                {
                    // 노드마다 한 줄 → map 을 줄 수만큼 반복해서 보낸다(각 줄 안의 쉼표는 그대로 둔다)
                    var pairs = values["map"]
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(line => new KeyValuePair<string, string>("map", line))
                        .Append(new KeyValuePair<string, string>("id", values["id"]))
                        .ToList();
                    if (values["description"].Length > 0)
                        pairs.Add(new KeyValuePair<string, string>("description", values["description"]));
                    return api.SendPairsAsync(System.Net.Http.HttpMethod.Post, $"cluster/mapping/{kind}", pairs);
                }, "DcMapping_Added")
            },
            DeleteAction(row => Loc.T("DcMapping_DeleteConfirm", row["id"]),
                row => api.DeleteActionAsync($"cluster/mapping/{kind}/{Seg(row["id"])}"), "DcMapping_Deleted")
        ];
    }

    // ------------------------------------------------------------ Ceph

    /// <summary>Ceph 상태(읽기 전용) — 설치·OSD·풀 관리는 노드 셸의 pveceph 나 웹 UI 에서 한다.</summary>
    public static TextEditTab CephStatus(ProxmoxApiClient api)
    {
        return new TextEditTab(async () => (await api.GetPrettyJsonAsync("cluster/ceph/status"), string.Empty), null,
            "DcCeph_Hint");
    }
}
