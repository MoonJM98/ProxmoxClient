using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     SDN 패브릭(PVE 9) — cluster/sdn/fabrics/fabric·node. OpenFabric·OSPF 를 만들고 고친다.
///     그 밖의 프로토콜(WireGuard 등)은 목록·삭제만 한다(칸 형식을 모르는 설정을 보내지 않게).
///     노드 인터페이스는 한 줄에 하나 "name=ens19[,ip=10.0.0.1/31]" 로 적고, 배열로 키를 반복해 보낸다.
/// </summary>
internal static partial class SdnFabrics
{

    internal static readonly IReadOnlyList<TableColumn> FabricColumns =
    [
        new() { Key = "id", HeaderKey = "Table_Id", Width = 110 },
        new() { Key = "protocol", HeaderKey = "DcFabric_Protocol", Width = 110 },
        new() { Key = "ip_prefix", HeaderKey = "DcFabric_Ip4Prefix", Width = 150 },
        new() { Key = "ip6_prefix", HeaderKey = "DcFabric_Ip6Prefix", Width = 170 },
        new() { Key = "area", HeaderKey = "DcFabric_Area", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> NodeColumns =
    [
        new() { Key = "node_id", HeaderKey = "Table_Node", Width = 110 },
        new() { Key = "ip", HeaderKey = "DcFabric_NodeIp", Width = 130 },
        new() { Key = "ip6", HeaderKey = "DcFabric_NodeIp6", Width = 170 },
        new() { Key = "interfaces", HeaderKey = "DcFabric_Interfaces", Width = 0 }
    ];

    private static readonly (string, string)[] Protocols = [("openfabric", "OpenFabric"), ("ospf", "OSPF")];

    internal static bool IsSupported(string protocol)
    {
        return protocol is "openfabric" or "ospf";
    }

    public static IReadOnlyList<TableAction> Fabrics(ProxmoxApiClient api)
    {
        return
        [
            new TableAction { LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddAsync(api, owner) },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditAsync(api, row!, owner)
            },
            new TableAction
            {
                LabelKey = "DcFabric_Nodes", IconKey = "IconServer", NeedsSelection = true,
                Run = (row, owner) => Task.FromResult(TableWindow.ShowModal(owner,
                    Loc.T("DcFabric_NodesTitle", row!["id"]),
                    new TableTab(() => api.Sdn.ListFabricNodesAsync(row["id"]), NodeColumns,
                        "DcFabric_NodesHint", IsSupported(Value(row, "protocol"))
                            ? NodeActions(api, row["id"], row["protocol"])
                            : null)))
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("DcFabric_DeleteConfirm", row!["id"]),
                Run = async (row, _) =>
                {
                    await api.Sdn.DeleteFabricAsync(row!["id"]);
                    return Loc.T("DcFabric_Deleted");
                }
            }
        ];
    }

    private static async Task<string?> AddAsync(ProxmoxApiClient api, Window? owner)
    {
        var choose = new FormDialog(Loc.T("DcFabric_AddTitle"),
        [
            new FormField { Key = "id", LabelKey = "Table_Id", Required = true, Hint = Loc.T("DcFabric_IdHint") },
            new FormField { Key = "protocol", LabelKey = "DcFabric_Protocol", Kind = FormFieldKind.Choice,
                Choices = Protocols, Initial = "openfabric" }
        ], values => IdPattern().IsMatch(values["id"]) ? null : Loc.T("DcFabric_IdHint")) { Owner = owner };
        if (choose.ShowDialog() != true || choose.Result is not { } picked) return null;

        var (id, protocol) = (picked["id"], picked["protocol"]);
        return await SubmitAsync(owner, Loc.T("DcFabric_AddTypeTitle", id, protocol),
            FabricFields(protocol, new Dictionary<string, string>()).ToList(), values =>
            {
                var form = NonEmpty(values);
                form["id"] = id;
                form["protocol"] = protocol;
                return api.Sdn.CreateFabricAsync(form);
            }, "DcFabric_Added", false, ValidateFabric);
    }

    private static async Task<string?> EditAsync(ProxmoxApiClient api, IReadOnlyDictionary<string, string> row,
        Window? owner)
    {
        var protocol = Value(row, "protocol");
        if (!IsSupported(protocol)) return Loc.T("DcFabric_Unsupported", protocol);

        var config = await api.Sdn.GetFabricAsync(row["id"]);
        return await SubmitAsync(owner, Loc.T("DcFabric_EditTitle", row["id"]),
            FabricFields(protocol, config).ToList(),
            values => api.Sdn.UpdateFabricAsync(row["id"],
                UpdatePairs(values, protocol, new HashSet<string>())),
            "DcFabric_Updated", false, ValidateFabric);
    }

    private static IEnumerable<FormField> FabricFields(string protocol, IReadOnlyDictionary<string, string> config)
    {
        FormField Text(string key, string labelKey, string? hintKey = null, bool required = false) =>
            new() { Key = key, LabelKey = labelKey, Initial = Value(config, key), Required = required,
                Hint = hintKey is null ? null : Loc.T(hintKey) };

        yield return Text("ip_prefix", "DcFabric_Ip4Prefix", "DcFabric_PrefixHint");
        if (protocol == "ospf")
        {
            yield return Text("area", "DcFabric_Area", "DcFabric_AreaHint", true);
            yield break;
        }

        yield return Text("ip6_prefix", "DcFabric_Ip6Prefix", "DcFabric_PrefixHint");
        yield return Text("hello_interval", "DcFabric_Hello", "DcFabric_HelloHint");
        yield return Text("csnp_interval", "DcFabric_Csnp", "DcFabric_CsnpHint");
    }

    internal static string? ValidateFabric(IReadOnlyDictionary<string, string> values)
    {
        foreach (var key in new[] { "hello_interval", "csnp_interval" })
            if (Value(values, key) is { Length: > 0 } v && !(int.TryParse(v, out var n) && n > 0))
                return Loc.T("DcMetrics_BadNumber");
        foreach (var key in new[] { "ip_prefix", "ip6_prefix" })
            if (Value(values, key) is { Length: > 0 } v && !v.Contains('/'))
                return Loc.T("DcFabric_PrefixHint");
        return values.ContainsKey("ip6_prefix") && Value(values, "ip_prefix").Length == 0
                                                 && Value(values, "ip6_prefix").Length == 0
            ? Loc.T("DcFabric_NeedPrefix")
            : null;
    }

    /// <summary>
    ///     수정 값 → 요청 쌍: 프로토콜(구성 형식 태그)을 함께 보내고, 비운 칸은 delete 를 키 반복(배열)으로,
    ///     arrayKeys 의 값은 줄마다 키를 반복한다.
    /// </summary>
    internal static List<KeyValuePair<string, string>> UpdatePairs(IReadOnlyDictionary<string, string> values,
        string protocol, IReadOnlySet<string> arrayKeys)
    {
        var pairs = new List<KeyValuePair<string, string>> { new("protocol", protocol) };
        foreach (var (key, value) in values)
        {
            if (value.Trim().Length == 0)
                pairs.Add(new KeyValuePair<string, string>("delete", key));
            else if (arrayKeys.Contains(key))
                pairs.AddRange(Lines(value).Select(line => new KeyValuePair<string, string>(key, line)));
            else
                pairs.Add(new KeyValuePair<string, string>(key, value.Trim()));
        }

        return pairs;
    }

    internal static IEnumerable<string> Lines(string text)
    {
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,7}$")]
    private static partial Regex IdPattern();
}
