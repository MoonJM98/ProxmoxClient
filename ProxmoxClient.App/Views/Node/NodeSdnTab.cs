using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.App.Views.Node;

/// <summary>
///     노드의 SDN 상태(9.0+, 웹 UI 노드 → SDN) — 영역별 브리지·IP VRF, VNet 별 MAC VRF, 패브릭 경로·이웃·인터페이스.
///     목록에서 하나를 고르고 보고 싶은 상태 버튼을 누른다.
/// </summary>
internal static class NodeSdnTab
{
    private static readonly IReadOnlyList<TableColumn> NameColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> BridgeColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 140 },
        new() { Key = "ports", HeaderKey = "NodeSdn_Ports", Width = 0 },
        new() { Key = "vlan_filtering", HeaderKey = "NodeSdn_VlanFiltering", Width = 110 }
    ];

    private static readonly IReadOnlyList<TableColumn> IpVrfColumns =
    [
        new() { Key = "ip", HeaderKey = "Table_Address", Width = 180 },
        new() { Key = "protocol", HeaderKey = "NodeSdn_Protocol", Width = 90 },
        new() { Key = "metric", HeaderKey = "NodeSdn_Metric", Width = 70 },
        new() { Key = "nexthops", HeaderKey = "NodeSdn_NextHops", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> MacVrfColumns =
    [
        new() { Key = "mac", HeaderKey = "GuestAgent_Mac", Width = 160 },
        new() { Key = "ip", HeaderKey = "Table_Address", Width = 180 },
        new() { Key = "nexthop", HeaderKey = "NodeSdn_NextHops", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> RouteColumns =
    [
        new() { Key = "route", HeaderKey = "NodeSdn_Route", Width = 200 },
        new() { Key = "via", HeaderKey = "NodeSdn_Via", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> NeighborColumns =
    [
        new() { Key = "neighbor", HeaderKey = "NodeSdn_Neighbor", Width = 200 },
        new() { Key = "status", HeaderKey = "Table_State", Width = 110 },
        new() { Key = "uptime", HeaderKey = "Table_Uptime", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> InterfaceColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 110 },
        new() { Key = "state", HeaderKey = "Table_State", Width = 0 }
    ];

    public static SubTabsView Create(ProxmoxApiClient api, string node)
    {
        return new SubTabsView(
        [
            ("DcSdn_Zones", () => new TableTab(() => Named(api.Sdn.ListAsync("zones"), "zone"), NameColumns,
                "NodeSdn_ZonesHint",
            [
                View("NodeSdn_Bridges", BridgeColumns, row => api.Sdn.ZoneBridgesAsync(node, row["name"])),
                View("NodeSdn_IpVrf", IpVrfColumns, row => api.Sdn.ZoneIpVrfAsync(node, row["name"]))
            ])),
            ("DcSdn_Vnets", () => new TableTab(() => Named(api.Sdn.ListAsync("vnets"), "vnet"), NameColumns,
                "NodeSdn_VnetsHint",
                [View("NodeSdn_MacVrf", MacVrfColumns, row => api.Sdn.VnetMacVrfAsync(node, row["name"]))])),
            ("DcSdn_Fabrics", () => new TableTab(() => Named(api.Sdn.ListFabricsAsync(), "id"), NameColumns,
                "NodeSdn_FabricsHint",
            [
                View("NodeSdn_Routes", RouteColumns, row => api.Sdn.FabricRoutesAsync(node, row["name"])),
                View("NodeSdn_Neighbors", NeighborColumns, row => api.Sdn.FabricNeighborsAsync(node, row["name"])),
                View("NodeSdn_Interfaces", InterfaceColumns,
                    row => api.Sdn.FabricInterfacesAsync(node, row["name"]))
            ]))
        ]);
    }

    /// <summary>
    ///     목록의 이름·종류 열을 name·type 으로 맞춘다(영역은 zone, VNet 은 vnet, 패브릭은 id — 패브릭 종류는 protocol).
    /// </summary>
    private static async Task<IReadOnlyList<Row>> Named(Task<IReadOnlyList<Row>> load, string key)
    {
        return (await load).Select(r => (Row)new Dictionary<string, string>(r)
            {
                ["name"] = r.TryGetValue(key, out var name) ? name : string.Empty,
                ["type"] = r.TryGetValue("type", out var type) ? type
                    : r.TryGetValue("protocol", out var protocol) ? protocol : string.Empty
            })
            .ToList();
    }

    private static TableAction View(string labelKey, IReadOnlyList<TableColumn> columns,
        Func<Row, Task<IReadOnlyList<Row>>> load)
    {
        return new TableAction
        {
            LabelKey = labelKey, IconKey = "IconList", NeedsSelection = true,
            Run = (row, owner) => Task.FromResult(TableWindow.ShowModal(owner,
                $"{Loc.T(labelKey)} — {row!["name"]}", new TableTab(() => load(row), columns, "NodeSdn_StatusHint")))
        };
    }
}
