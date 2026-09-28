using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>
///     방화벽 규칙 목록(웹 UI grid/FirewallRules.js) — 추가·복사·보안 그룹 넣기·수정·삭제·위/아래 이동.
///     규칙 칸: 방향·동작·매크로·인터페이스·프로토콜·출발지/포트·목적지/포트·ICMP 유형·로그 수준·설명·사용.
///     데이터센터·노드·게스트·보안 그룹이 범위(<see cref="FirewallScope" />)만 달리해 함께 쓴다.
/// </summary>
internal static class FirewallRules
{
    private static readonly (string, string)[] Directions =
        [("in", "Choice_FwIn"), ("out", "Choice_FwOut"), ("forward", "Choice_FwForward")];

    /// <summary>SDN VNet 방화벽은 지나가는 트래픽만 다룬다(8.3).</summary>
    private static readonly (string, string)[] ForwardOnly = [("forward", "Choice_FwForward")];

    private static readonly (string, string)[] LogLevels =
    [
        ("", "FwRule_LogDefault"), ("nolog", "nolog"), ("emerg", "emerg"), ("alert", "alert"), ("crit", "crit"),
        ("err", "err"), ("warning", "warning"), ("notice", "notice"), ("info", "info"), ("debug", "debug")
    ];

    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "pos", HeaderKey = "FirewallWindow_06", Width = 44 },
        new() { Key = "enable", HeaderKey = "FirewallWindow_05", Width = 50, Format = TableFormats.Flag },
        new() { Key = "type", HeaderKey = "FirewallWindow_07", Width = 64 },
        new() { Key = "action", HeaderKey = "FirewallWindow_08", Width = 90 },
        new() { Key = "macro", HeaderKey = "FirewallWindow_09", Width = 80 },
        new() { Key = "iface", HeaderKey = "FwRule_Iface", Width = 70 },
        new() { Key = "proto", HeaderKey = "FirewallWindow_10", Width = 60 },
        new() { Key = "source", HeaderKey = "FirewallWindow_12", Width = 120 },
        new() { Key = "sport", HeaderKey = "FwRule_SourcePort", Width = 70 },
        new() { Key = "dest", HeaderKey = "FwRule_Dest", Width = 120 },
        new() { Key = "dport", HeaderKey = "FirewallWindow_11", Width = 80 },
        new() { Key = "log", HeaderKey = "FwRule_Log", Width = 60 },
        new() { Key = "comment", HeaderKey = "FirewallWindow_13", Width = 0 }
    ];

    public static TableTab Create(ProxmoxApiClient api, FirewallScope scope, bool canEdit = true)
    {
        return new TableTab(async () => (await api.Firewall.ListRulesAsync(scope))
                .OrderBy(r => int.TryParse(Value(r, "pos"), out var p) ? p : int.MaxValue).ToList(),
            Columns, "FwRule_Hint", canEdit ? Actions(api, scope) : null);
    }

    private static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api, FirewallScope scope)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => EditAsync(api, scope, null, owner, copy: false)
            },
            new TableAction
            {
                LabelKey = "FwRule_Copy", IconKey = "IconCopy", NeedsSelection = true,
                Run = (row, owner) => IsGroup(row!)
                    ? InsertGroupAsync(api, scope, row, owner)
                    : EditAsync(api, scope, row, owner, copy: true)
            },
            new TableAction
            {
                LabelKey = "FwRule_InsertGroup", IconKey = "IconShield",
                Run = (_, owner) => InsertGroupAsync(api, scope, null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => IsGroup(row!)
                    ? EditGroupAsync(api, scope, row!, owner)
                    : EditAsync(api, scope, row, owner, copy: false)
            },
            DeleteAction(row => Loc.T("FirewallWindow_M07", Value(row, "pos"), Value(row, "action"),
                    Value(row, "type"), Value(row, "dport")),
                row => api.Firewall.DeleteRuleAsync(scope, Value(row, "pos")), "FirewallWindow_M09"),
            Move(api, scope, "FwRule_MoveUp", "IconArrowUp", -1),
            Move(api, scope, "FwRule_MoveDown", "IconArrowDown", +1)
        ];
    }

    private static bool IsGroup(IReadOnlyDictionary<string, string> row)
    {
        return Value(row, "type") == "group";
    }

    /// <summary>한 칸 위/아래로 — PUT rules/{pos} moveto(웹 UI 는 끌어 놓기로 같은 요청을 보낸다).</summary>
    private static TableAction Move(ProxmoxApiClient api, FirewallScope scope, string labelKey, string iconKey,
        int delta)
    {
        return new TableAction
        {
            LabelKey = labelKey, IconKey = iconKey, NeedsSelection = true,
            Run = async (row, _) =>
            {
                if (!int.TryParse(Value(row!, "pos"), out var pos)) return null;
                var target = pos + delta;
                if (target < 0) return Loc.T("FwRule_AlreadyTop");
                // 아래로 옮길 때 moveto 는 "그 자리 앞" 이라 한 칸 더 준다(웹 UI 와 같다)
                var moveto = delta > 0 ? target + 1 : target;
                await api.Firewall.UpdateRuleAsync(scope, pos.ToString(),
                    new Dictionary<string, string> { ["moveto"] = moveto.ToString() });
                return Loc.T("FwRule_Moved", pos, target);
            }
        };
    }

    // ------------------------------------------------------------ 규칙 추가·수정·복사

    /// <summary>출발지·목적지 칸의 목록 — 쓸 수 있는 별칭·IP 집합(웹 UI 의 IPRefSelector). 목록을 펼칠 때 읽는다.</summary>
    private static Func<IReadOnlyDictionary<string, string>, Task<IReadOnlyList<(string, string)>>> RefSuggestions(
        ProxmoxApiClient api, FirewallScope scope)
    {
        return async _ => (await api.Firewall.RefsAsync(scope))
            .Select(r => (Value(r, "ref"), $"{Value(r, "type")} {Value(r, "comment")}".Trim()))
            .Where(r => r.Item1.Length > 0)
            .OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<string?> EditAsync(ProxmoxApiClient api, FirewallScope scope,
        IReadOnlyDictionary<string, string>? row, Window? owner, bool copy)
    {
        var macros = await MacroChoicesAsync(api);
        string V(string key) => row is null ? string.Empty : Value(row, key);
        var isNew = row is null || copy;
        var refs = RefSuggestions(api, scope);

        var fields = new List<FormField>
        {
            new() { Key = "type", LabelKey = "FirewallWindow_07", Kind = FormFieldKind.Choice,
                Choices = scope.IsVnet ? ForwardOnly : Directions,
                Initial = row is not null ? V("type") : scope.IsVnet ? "forward" : "in" },
            new() { Key = "enable", LabelKey = "FirewallWindow_17", Kind = FormFieldKind.Bool,
                Initial = row is null || V("enable") == "1" ? "1" : "0" },
            new() { Key = "action", LabelKey = "FirewallWindow_08", Kind = FormFieldKind.Choice,
                Choices = ComboChoices.FirewallActions, Initial = row is null ? "ACCEPT" : V("action") },
            new() { Key = "macro", LabelKey = "FirewallWindow_09", Kind = FormFieldKind.Choice, Choices = macros,
                Initial = V("macro"), Hint = Loc.T("FwRule_MacroHint") },
            new() { Key = "iface", LabelKey = "FwRule_Iface", Initial = V("iface"), Trim = true,
                Hint = Loc.T("FwRule_IfaceHint") },
            new() { Key = "proto", LabelKey = "FirewallWindow_10", Kind = FormFieldKind.Choice,
                Choices = ComboChoices.FirewallProtocols, Initial = V("proto") },
            new() { Key = "source", LabelKey = "FirewallWindow_12", Initial = V("source"), Trim = true,
                Hint = Loc.T("FwRule_AddressHint"), Suggest = refs },
            new() { Key = "sport", LabelKey = "FwRule_SourcePort", Initial = V("sport"), Trim = true,
                Hint = Loc.T("FirewallWindow_15") },
            new() { Key = "dest", LabelKey = "FwRule_Dest", Initial = V("dest"), Trim = true,
                Hint = Loc.T("FwRule_AddressHint"), Suggest = refs },
            new() { Key = "dport", LabelKey = "FirewallWindow_11", Initial = V("dport"), Trim = true,
                Hint = Loc.T("FirewallWindow_15") },
            new() { Key = "icmp-type", LabelKey = "FwRule_IcmpType", Initial = V("icmp-type"), Trim = true,
                Hint = Loc.T("FwRule_IcmpHint") },
            new() { Key = "log", LabelKey = "FwRule_Log", Kind = FormFieldKind.Choice, Choices = LogLevels,
                Initial = V("log") },
            new() { Key = "comment", LabelKey = "FirewallWindow_13", Initial = V("comment") }
        };

        var title = row is null ? Loc.T("FirewallWindow_14")
            : copy ? Loc.T("FwRule_CopyTitle", V("pos")) : Loc.T("FwRule_EditTitle", V("pos"));
        return await SubmitAsync(owner, title, fields, values =>
        {
            var form = RuleForm(values);
            return isNew
                ? api.Firewall.CreateRuleAsync(scope, NonEmpty(form))
                : api.Firewall.UpdateRuleAsync(scope, V("pos"), UpdateForm(form));
        }, isNew ? "FirewallWindow_M05" : "FwRule_Updated", titleIsKey: false, validate: Validate);
    }

    /// <summary>
    ///     매크로를 고르면 프로토콜·포트는 매크로가 정한다 — 웹 UI 처럼 그 칸들은 보내지 않는다.
    ///     ICMP 유형은 ICMP 프로토콜일 때만.
    /// </summary>
    internal static Dictionary<string, string> RuleForm(IReadOnlyDictionary<string, string> values)
    {
        var form = values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (form["macro"].Length > 0)
        {
            form["proto"] = string.Empty;
            form["sport"] = string.Empty;
            form["dport"] = string.Empty;
        }

        if (form["proto"] is not ("icmp" or "ipv6-icmp" or "icmpv6")) form["icmp-type"] = string.Empty;
        return form;
    }

    private static string? Validate(IReadOnlyDictionary<string, string> values)
    {
        var portsNeedProto = values["macro"].Length == 0
                             && (values["sport"].Length > 0 || values["dport"].Length > 0)
                             && values["proto"] is not ("tcp" or "udp" or "sctp" or "dccp" or "udplite");
        return portsNeedProto ? Loc.T("FwRule_PortsNeedProto") : null;
    }

    private static async Task<IReadOnlyList<(string, string)>> MacroChoicesAsync(ProxmoxApiClient api)
    {
        var list = new List<(string, string)> { ("", "FwRule_NoMacro") };
        try
        {
            list.AddRange((await api.Firewall.MacrosAsync())
                .Select(m => (Value(m, "macro"), $"{Value(m, "macro")} — {Value(m, "descr")}"))
                .Where(m => m.Item1.Length > 0)
                .OrderBy(m => m.Item1, StringComparer.OrdinalIgnoreCase));
        }
        catch (ProxmoxApiException)
        {
            // 매크로 목록을 못 읽어도 규칙은 만들 수 있다
        }

        return list;
    }

    // ------------------------------------------------------------ 보안 그룹 넣기

    private static async Task<IReadOnlyList<(string, string)>> GroupChoicesAsync(ProxmoxApiClient api)
    {
        return (await api.Firewall.ListGroupsAsync())
            .Select(g => (Value(g, "group"), Value(g, "comment") is { Length: > 0 } c
                ? $"{Value(g, "group")} — {c}"
                : Value(g, "group")))
            .ToList();
    }

    /// <summary>보안 그룹을 규칙으로 넣는다(type=group, action=그룹 이름) — 복사도 같은 창.</summary>
    private static async Task<string?> InsertGroupAsync(ProxmoxApiClient api, FirewallScope scope,
        IReadOnlyDictionary<string, string>? copyFrom, Window? owner)
    {
        var groups = await GroupChoicesAsync(api);
        if (groups.Count == 0) return Loc.T("FwRule_NoGroups");
        string V(string key) => copyFrom is null ? string.Empty : Value(copyFrom, key);

        return await SubmitAsync(owner, Loc.T("FwRule_InsertGroup"), GroupFields(groups, V, copyFrom is null),
            values => api.Firewall.CreateRuleAsync(scope, NonEmpty(GroupForm(values))), "FirewallWindow_M05",
            titleIsKey: false, validate: v => v["action"].Length == 0 ? Loc.T("FwRule_PickGroup") : null);
    }

    private static async Task<string?> EditGroupAsync(ProxmoxApiClient api, FirewallScope scope,
        IReadOnlyDictionary<string, string> row, Window? owner)
    {
        var groups = await GroupChoicesAsync(api);
        return await SubmitAsync(owner, Loc.T("FwRule_EditTitle", Value(row, "pos")),
            GroupFields(groups, key => Value(row, key), false),
            values => api.Firewall.UpdateRuleAsync(scope, Value(row, "pos"), UpdateForm(GroupForm(values))),
            "FwRule_Updated", titleIsKey: false);
    }

    private static List<FormField> GroupFields(IReadOnlyList<(string, string)> groups, Func<string, string> value,
        bool isNew)
    {
        return
        [
            new FormField { Key = "action", LabelKey = "FwRule_SecurityGroup", Kind = FormFieldKind.Choice,
                Choices = groups, Initial = value("action") },
            new FormField { Key = "iface", LabelKey = "FwRule_Iface", Initial = value("iface"), Trim = true,
                Hint = Loc.T("FwRule_IfaceHint") },
            new FormField { Key = "enable", LabelKey = "FirewallWindow_17", Kind = FormFieldKind.Bool,
                Initial = isNew || value("enable") == "1" ? "1" : "0" },
            new FormField { Key = "comment", LabelKey = "FirewallWindow_13", Initial = value("comment") }
        ];
    }

    private static Dictionary<string, string> GroupForm(IReadOnlyDictionary<string, string> values)
    {
        var form = values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        form["type"] = "group";
        return form;
    }
}
