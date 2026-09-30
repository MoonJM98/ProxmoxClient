using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Node;

/// <summary>
///     노드 네트워크 편집. 서버는 바뀐 내용을 대기 설정에만 적어 두므로, '적용'을 눌러야 실제로 반영되고
///     그 전에는 '변경 취소'로 되돌릴 수 있다 — 잘못 고쳐도 바로 접속이 끊기지 않는다.
/// </summary>
internal static class NetworkActions
{
    private static readonly IReadOnlyList<(string, string)> BondModes =
    [
        ("active-backup", "active-backup"), ("balance-rr", "balance-rr"), ("balance-xor", "balance-xor"),
        ("broadcast", "broadcast"), ("802.3ad", "802.3ad (LACP)"), ("balance-tlb", "balance-tlb"),
        ("balance-alb", "balance-alb")
    ];

    public static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api, string node)
    {
        return
        [
            new TableAction
            {
                LabelKey = "NodeNetwork_AddBridge", IconKey = "IconPlus",
                Run = (_, owner) => EditAsync(api, node, "bridge", null, owner)
            },
            new TableAction
            {
                LabelKey = "NodeNetwork_AddBond", IconKey = "IconPlus",
                Run = (_, owner) => EditAsync(api, node, "bond", null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditAsync(api, node, Value(row!, "type"), row, owner)
            },
            DeleteAction(row => Loc.T("NodeNetwork_DeleteConfirm", row["iface"]),
                row => api.Nodes.DeleteInterfaceAsync(node, row["iface"]), "NodeNetwork_Deleted"),
            new TableAction
            {
                LabelKey = "NodeNetwork_Apply", IconKey = "IconCheck",
                Confirm = _ => Loc.T("NodeNetwork_ApplyConfirm"),
                Run = async (_, _) =>
                {
                    var upid = await api.Nodes.ApplyNetworkAsync(node);
                    if (upid.Length == 0) return Loc.T("NodeNetwork_Applied", "OK");

                    var task = await api.WaitTaskAsync(upid);
                    return Loc.T("NodeNetwork_Applied", task.Status);
                }
            },
            new TableAction
            {
                LabelKey = "NodeNetwork_Revert", IconKey = "IconUndo",
                Confirm = _ => Loc.T("NodeNetwork_RevertConfirm"),
                Run = async (_, _) =>
                {
                    await api.Nodes.RevertNetworkAsync(node);
                    return Loc.T("NodeNetwork_Reverted");
                }
            }
        ];
    }

    /// <summary>row 가 null 이면 새 인터페이스. 유형마다 필요한 칸만 보인다.</summary>
    private static Task<string?> EditAsync(ProxmoxApiClient api, string node, string type,
        IReadOnlyDictionary<string, string>? row, Window? owner)
    {
        string Initial(string key, string fallback = "") => row is null ? fallback : Value(row, key);

        var fields = new List<FormField>();
        if (row is null)
            fields.Add(new FormField
            {
                Key = "iface", LabelKey = "Table_Name", Required = true, Initial = type == "bond" ? "bond0" : "vmbr1"
            });

        fields.Add(new FormField { Key = "cidr", LabelKey = "Table_Cidr", Initial = Initial("cidr") });
        fields.Add(new FormField { Key = "gateway", LabelKey = "Table_Gateway", Initial = Initial("gateway") });

        if (type == "bridge")
        {
            fields.Add(new FormField
            {
                Key = "bridge_ports", LabelKey = "NodeNetwork_BridgePorts", Initial = Initial("bridge_ports")
            });
            fields.Add(new FormField
            {
                Key = "bridge_vlan_aware", LabelKey = "NodeNetwork_VlanAware", Kind = FormFieldKind.Bool,
                Initial = Initial("bridge_vlan_aware", "0")
            });
        }
        else if (type == "bond")
        {
            fields.Add(new FormField { Key = "slaves", LabelKey = "NodeNetwork_Slaves", Initial = Initial("slaves") });
            fields.Add(new FormField
            {
                Key = "bond_mode", LabelKey = "NodeNetwork_BondMode", Kind = FormFieldKind.Choice, Choices = BondModes,
                Initial = Initial("bond_mode", "active-backup")
            });
        }

        fields.Add(new FormField { Key = "mtu", LabelKey = "NodeNetwork_Mtu", Initial = Initial("mtu") });
        fields.Add(new FormField { Key = "comments", LabelKey = "Table_Comment", Initial = Initial("comments") });
        fields.Add(new FormField
        {
            Key = "autostart", LabelKey = "Table_Autostart", Kind = FormFieldKind.Bool,
            Initial = Initial("autostart", "1")
        });

        var title = row is null
            ? Loc.T(type == "bond" ? "NodeNetwork_AddBond" : "NodeNetwork_AddBridge")
            : Loc.T("NodeNetwork_EditTitle", row["iface"]);

        return SubmitAsync(owner, title, fields, values =>
        {
            if (row is null)
            {
                var form = NonEmpty(values);
                form["type"] = type;
                return api.Nodes.CreateInterfaceAsync(node, form);
            }

            var update = UpdateForm(values);
            update["type"] = type;
            return api.Nodes.UpdateInterfaceAsync(node, row["iface"], update);
        }, row is null ? "NodeNetwork_Added" : "NodeNetwork_Updated", titleIsKey: false);
    }
}
