using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Guest.Tabs;
using ProxmoxClient.UI.Views.Shared;
using static ProxmoxClient.UI.Views.Shared.ActionHelpers;

namespace ProxmoxClient.UI.Views.Node;

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
                LabelKey = "NodeNetwork_AddVlan", IconKey = "IconPlus", MenuKey = "GuestLife_MoreMenu",
                Run = (_, owner) => EditAsync(api, node, "vlan", null, owner)
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
                Run = (_, _) => ApplyAsync(api, node)
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

    /// <summary>
    ///     바뀐 내용(서버가 주는 interfaces 와 interfaces.new 의 diff)을 보여 주고 확인을 받은 뒤 적용한다.
    ///     적용 작업이 실패하면(예: ifreload 오류) 실패로 알린다 — 그때는 설정이 반쯤 적용됐을 수 있다.
    /// </summary>
    private static async Task<string?> ApplyAsync(ProxmoxApiClient api, string node)
    {
        var changes = await api.Nodes.PendingNetworkChangesAsync(node);
        if (changes.Trim().Length == 0) return Loc.T("NodeNetwork_NoChanges");

        if (!await Ask.ConfirmAsync(Loc.T("NodeNetwork_ApplyDiff", ShortDiff(changes)),
                Loc.T("NodeNetwork_Apply")))
            return null;

        // 위에서 이미 확인을 받았다 — 쓰기 확인 창이 한 번 더 뜨지 않게 같은 흐름으로 표시
        using var flow = WriteConfirmation.Begin(Loc.T("NodeNetwork_Apply"), true);
        var upid = await api.Nodes.ApplyNetworkAsync(node);
        if (upid.Length == 0) return Loc.T("NodeNetwork_Applied", "OK");

        var task = await api.WaitTaskAsync(upid);
        return task.IsOk ? Loc.T("NodeNetwork_Applied", task.Status) : Loc.T("NodeNetwork_ApplyFailed", task.Status);
    }

    /// <summary>diff 를 확인 창에 맞게 줄인다(머리 줄 빼고 앞 60줄).</summary>
    internal static string ShortDiff(string diff)
    {
        var lines = diff.Replace("\r", "").Split('\n')
            .Where(l => !l.StartsWith("--- ", StringComparison.Ordinal) && !l.StartsWith("+++ ", StringComparison.Ordinal))
            .ToList();
        const int Max = 60;
        return lines.Count <= Max
            ? string.Join('\n', lines).Trim()
            : string.Join('\n', lines.Take(Max)) + $"\n… (+{lines.Count - Max})";
    }

    /// <summary>row 가 null 이면 새 인터페이스. 유형마다 필요한 칸만 보인다.</summary>
    private static Task<string?> EditAsync(ProxmoxApiClient api, string node, string type,
        IReadOnlyDictionary<string, string>? row, Control? owner)
    {
        string Initial(string key, string fallback = "") => row is null ? fallback : Value(row, key);

        var fields = new List<FormField>();
        if (row is null)
            fields.Add(new FormField
            {
                Key = "iface", LabelKey = "Table_Name", Required = true,
                Initial = type switch { "bond" => "bond0", "vlan" => "vmbr0.10", _ => "vmbr1" },
                Hint = type == "vlan" ? Loc.T("NodeNetwork_VlanNameHint") : null
            });

        fields.Add(new FormField { Key = "cidr", LabelKey = "Table_Cidr", Initial = Initial("cidr") });
        fields.Add(new FormField { Key = "gateway", LabelKey = "Table_Gateway", Initial = Initial("gateway") });
        fields.Add(new FormField { Key = "cidr6", LabelKey = "NodeNetwork_Cidr6", Initial = Initial("cidr6") });
        fields.Add(new FormField { Key = "gateway6", LabelKey = "NodeNetwork_Gateway6", Initial = Initial("gateway6") });

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
        else if (type == "vlan")
        {
            // 이름이 eno1.10 처럼 "장치.번호" 면 서버가 알아서 정한다 — 다른 이름(vlan10 등)일 때만 채운다
            fields.Add(new FormField
            {
                Key = "vlan-raw-device", LabelKey = "NodeNetwork_VlanRawDevice", Initial = Initial("vlan-raw-device")
            });
            fields.Add(new FormField { Key = "vlan-id", LabelKey = "NodeNetwork_VlanId", Initial = Initial("vlan-id") });
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
            ? Loc.T(type switch { "bond" => "NodeNetwork_AddBond", "vlan" => "NodeNetwork_AddVlan", _ => "NodeNetwork_AddBridge" })
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
        }, row is null ? "NodeNetwork_Added" : "NodeNetwork_Updated", titleIsKey: false, validate: Validate);
    }

    /// <summary>주소 칸(IPv4·IPv6 CIDR·게이트웨이)과 VLAN 번호를 미리 검사한다.</summary>
    internal static string? Validate(IReadOnlyDictionary<string, string> values)
    {
        string V(string key) => values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
        if (V("cidr").Length > 0 && !GuestInputChecks.IsCidr(V("cidr"), AddressFamily.InterNetwork))
            return Loc.T("NodeNetwork_BadCidr", V("cidr"));
        if (V("cidr6").Length > 0 && !GuestInputChecks.IsCidr(V("cidr6"), AddressFamily.InterNetworkV6))
            return Loc.T("NodeNetwork_BadCidr", V("cidr6"));
        if (V("gateway").Length > 0
            && !(IPAddress.TryParse(V("gateway"), out var gw) && gw.AddressFamily == AddressFamily.InterNetwork))
            return Loc.T("NodeNetwork_BadGateway", V("gateway"));
        if (V("gateway6").Length > 0
            && !(IPAddress.TryParse(V("gateway6"), out var gw6) && gw6.AddressFamily == AddressFamily.InterNetworkV6))
            return Loc.T("NodeNetwork_BadGateway", V("gateway6"));
        if (V("vlan-id").Length > 0 && !(int.TryParse(V("vlan-id"), out var tag) && tag is >= 1 and <= 4094))
            return Loc.T("GuestSettings_VlanRange");
        return null;
    }
}
