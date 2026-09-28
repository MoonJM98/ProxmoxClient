using System.Net.Http;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>패브릭 노드 — 노드마다 라우터 ID(ip·ip6)와 이웃과 잇는 인터페이스.</summary>
internal static partial class SdnFabrics
{
    private static readonly IReadOnlySet<string> NodeArrays = new HashSet<string> { "interfaces" };

    private static IReadOnlyList<TableAction> NodeActions(ProxmoxApiClient api, string fabric, string protocol)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => AddNodeAsync(api, fabric, protocol, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditNodeAsync(api, fabric, row!["node_id"], protocol, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("DcFabric_NodeDeleteConfirm", row!["node_id"]),
                Run = async (row, _) =>
                {
                    await api.Sdn.DeleteFabricNodeAsync(fabric, row!["node_id"]);
                    return Loc.T("DcFabric_NodeDeleted");
                }
            }
        ];
    }

    private static async Task<string?> AddNodeAsync(ProxmoxApiClient api, string fabric, string protocol,
        Window? owner)
    {
        var nodes = (await api.GetNodesAsync()).Select(n => n.Node).Where(n => n.Length > 0)
            .Order(StringComparer.Ordinal).Select(n => (n, n)).ToList();
        IReadOnlyList<FormField> fields =
        [
            new FormField { Key = "node_id", LabelKey = "Table_Node", Kind = FormFieldKind.Choice, Choices = nodes,
                Initial = nodes.Count > 0 ? nodes[0].Item1 : string.Empty, Required = true },
            ..NodeFields(protocol, new Dictionary<string, string>())
        ];
        return await SubmitAsync(owner, "DcFabric_NodeAddTitle", fields, values =>
        {
            var pairs = NodePairs(values, protocol).Where(kv => kv.Key != "delete").ToList();
            return api.Sdn.CreateFabricNodeAsync(fabric, pairs);
        }, "DcFabric_NodeAdded", validate: values => ValidateNode(values, protocol));
    }

    private static async Task<string?> EditNodeAsync(ProxmoxApiClient api, string fabric, string nodeId,
        string protocol, Window? owner)
    {
        var config = await api.Sdn.GetFabricNodeAsync(fabric, nodeId);
        var node = Value(config, "node_id");
        return await SubmitAsync(owner, Loc.T("DcFabric_NodeEditTitle", node),
            NodeFields(protocol, config).ToList(),
            values => api.Sdn.UpdateFabricNodeAsync(fabric, nodeId, NodePairs(values, protocol)),
            "DcFabric_NodeUpdated", false, values => ValidateNode(values, protocol));
    }

    private static IEnumerable<FormField> NodeFields(string protocol, IReadOnlyDictionary<string, string> config)
    {
        yield return new FormField { Key = "ip", LabelKey = "DcFabric_NodeIp", Initial = Value(config, "ip"),
            Hint = Loc.T("DcFabric_NodeIpHint") };
        if (protocol == "openfabric")
            yield return new FormField { Key = "ip6", LabelKey = "DcFabric_NodeIp6", Initial = Value(config, "ip6") };
        yield return new FormField { Key = "interfaces", LabelKey = "DcFabric_Interfaces",
            Kind = FormFieldKind.Multiline, CanLoadFile = false, Required = true,
            Initial = string.Join('\n', Lines(Value(config, "interfaces")).Select(PropertyString.FromJsonObject)),
            Hint = Loc.T(protocol == "ospf" ? "DcFabric_InterfacesHintOspf" : "DcFabric_InterfacesHint") };
    }

    /// <summary>인터페이스 줄 → "name=…[,ip=…]" (이름만 적은 줄은 name= 을 붙인다).</summary>
    internal static List<KeyValuePair<string, string>> NodePairs(IReadOnlyDictionary<string, string> values,
        string protocol)
    {
        var normalized = values.ToDictionary(kv => kv.Key,
            kv => kv.Key == "interfaces"
                ? string.Join('\n', Lines(kv.Value).Select(l => l.Contains('=') ? l : $"name={l}"))
                : kv.Value, StringComparer.Ordinal);
        return UpdatePairs(normalized, protocol, NodeArrays);
    }

    internal static string? ValidateNode(IReadOnlyDictionary<string, string> values, string protocol)
    {
        if (Value(values, "ip").Length == 0 && Value(values, "ip6").Length == 0)
            return Loc.T("DcFabric_NeedNodeIp");
        foreach (var line in Lines(Value(values, "interfaces")))
        {
            var p = PropertyString.Parse(line.Contains('=') ? line : $"name={line}");
            if (p.Get("name").Length == 0) return Loc.T("DcFabric_BadInterface", line);
            if (protocol == "ospf" && p.Get("ip6").Length > 0) return Loc.T("DcFabric_BadInterface", line);
        }

        return null;
    }
}
