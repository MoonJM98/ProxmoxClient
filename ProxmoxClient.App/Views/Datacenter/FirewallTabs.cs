using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Guest.Tabs;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>데이터센터 방화벽 — 웹 UI 처럼 규칙·보안 그룹·별칭·IP 집합·옵션 하위 탭.</summary>
internal static class FirewallTabs
{

    private static readonly IReadOnlyList<TableColumn> GroupColumns =
    [
        new() { Key = "group", HeaderKey = "Table_Name", Width = 200 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> AliasColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 180 },
        new() { Key = "cidr", HeaderKey = "DcFirewall_Cidr", Width = 200 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> IpSetColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 200 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> IpSetEntryColumns =
    [
        new() { Key = "cidr", HeaderKey = "DcFirewall_Cidr", Width = 200 },
        new() { Key = "nomatch", HeaderKey = "DcFirewall_NoMatch", Width = 80, Format = TableFormats.Flag },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly (string, string)[] Policies =
        [("ACCEPT", "ACCEPT"), ("REJECT", "REJECT"), ("DROP", "DROP")];

    private static readonly (string, string)[] LogLevels =
    [
        ("nolog", "nolog"), ("emerg", "emerg"), ("alert", "alert"), ("crit", "crit"), ("err", "err"),
        ("warning", "warning"), ("notice", "notice"), ("info", "info"), ("debug", "debug")
    ];

    private static readonly GuestOption[] NodeOptions =
    [
        new() { Key = "enable", LabelKey = "FirewallWindow_03", Kind = OptionKind.Bool },
        new() { Key = "log_level_in", LabelKey = "DcFirewall_LogIn", Kind = OptionKind.Choice, Choices = LogLevels },
        new() { Key = "log_level_out", LabelKey = "DcFirewall_LogOut", Kind = OptionKind.Choice, Choices = LogLevels },
        new()
        {
            Key = "log_level_forward", LabelKey = "DcFirewall_LogForward", Kind = OptionKind.Choice, Choices = LogLevels
        },
        new() { Key = "nftables", LabelKey = "DcFirewall_Nftables", Kind = OptionKind.Bool },
        new() { Key = "ndp", LabelKey = "DcFirewall_Ndp", Kind = OptionKind.Bool },
        new() { Key = "nosmurfs", LabelKey = "DcFirewall_NoSmurfs", Kind = OptionKind.Bool },
        new() { Key = "tcpflags", LabelKey = "DcFirewall_TcpFlags", Kind = OptionKind.Bool },
        new() { Key = "protection_synflood", LabelKey = "DcFirewall_SynFlood", Kind = OptionKind.Bool },
        new()
        {
            Key = "nf_conntrack_max", LabelKey = "DcFirewall_ConntrackMax", Kind = OptionKind.Text,
            EmptyLabelKey = "DcOptions_Default"
        }
    ];

    private static readonly GuestOption[] GuestFirewallOptions =
    [
        new() { Key = "enable", LabelKey = "FirewallWindow_03", Kind = OptionKind.Bool },
        new() { Key = "policy_in", LabelKey = "DcFirewall_PolicyIn", Kind = OptionKind.Choice, Choices = Policies },
        new() { Key = "policy_out", LabelKey = "DcFirewall_PolicyOut", Kind = OptionKind.Choice, Choices = Policies },
        new() { Key = "log_level_in", LabelKey = "DcFirewall_LogIn", Kind = OptionKind.Choice, Choices = LogLevels },
        new() { Key = "log_level_out", LabelKey = "DcFirewall_LogOut", Kind = OptionKind.Choice, Choices = LogLevels },
        new() { Key = "macfilter", LabelKey = "DcFirewall_MacFilter", Kind = OptionKind.Bool },
        new() { Key = "ipfilter", LabelKey = "DcFirewall_IpFilter", Kind = OptionKind.Bool },
        new() { Key = "dhcp", LabelKey = "DcFirewall_Dhcp", Kind = OptionKind.Bool },
        new() { Key = "ndp", LabelKey = "DcFirewall_Ndp", Kind = OptionKind.Bool },
        new() { Key = "radv", LabelKey = "DcFirewall_Radv", Kind = OptionKind.Bool }
    ];

    private static readonly GuestOption[] ClusterOptions =
    [
        new() { Key = "enable", LabelKey = "FirewallWindow_03", Kind = OptionKind.Bool },
        new() { Key = "policy_in", LabelKey = "DcFirewall_PolicyIn", Kind = OptionKind.Choice, Choices = Policies },
        new() { Key = "policy_out", LabelKey = "DcFirewall_PolicyOut", Kind = OptionKind.Choice, Choices = Policies },
        new()
        {
            Key = "policy_forward", LabelKey = "DcFirewall_PolicyForward", Kind = OptionKind.Choice,
            Choices = [("ACCEPT", "ACCEPT"), ("DROP", "DROP")]
        },
        new() { Key = "ebtables", LabelKey = "DcFirewall_Ebtables", Kind = OptionKind.Bool },
        new()
        {
            Key = "log_ratelimit", LabelKey = "DcFirewall_LogRateLimit", Kind = OptionKind.Text,
            EmptyLabelKey = "DcOptions_Default"
        }
    ];

    /// <summary>데이터센터 방화벽 — 규칙·보안 그룹·별칭·IP 집합·옵션.</summary>
    public static SubTabsView Create(ProxmoxApiClient api, bool canEdit)
    {
        return new SubTabsView(
        [
            ("DcFirewall_Rules", () => new FirewallTab(api, FirewallScope.Cluster, canEdit)),
            ("DcFirewall_Groups", () => new TableTab(() => api.Firewall.ListGroupsAsync(), GroupColumns,
                "DcFirewall_GroupsHint", GroupActions(api, canEdit))),
            ("DcFirewall_Aliases", () => Aliases(api, FirewallScope.Cluster, canEdit)),
            ("DcFirewall_IpSets", () => IpSets(api, FirewallScope.Cluster, canEdit)),
            ("GuestTab_Options", () => Options(api, FirewallScope.Cluster, ClusterOptions))
        ]);
    }

    private static readonly GuestOption[] VnetOptions =
    [
        new() { Key = "enable", LabelKey = "FirewallWindow_03", Kind = OptionKind.Bool },
        new()
        {
            Key = "policy_forward", LabelKey = "DcFirewall_PolicyForward", Kind = OptionKind.Choice,
            Choices = [("ACCEPT", "ACCEPT"), ("DROP", "DROP")]
        },
        new()
        {
            Key = "log_level_forward", LabelKey = "DcFirewall_LogForward", Kind = OptionKind.Choice, Choices = LogLevels
        }
    ];

    /// <summary>SDN VNet 방화벽(8.3+) — 이 VNet 을 지나는(전달) 트래픽의 규칙과 옵션.</summary>
    public static SubTabsView ForVnet(ProxmoxApiClient api, string vnet, bool canEdit)
    {
        var scope = FirewallScope.ForVnet(vnet);
        return new SubTabsView(
        [
            ("DcFirewall_Rules", () => new FirewallTab(api, scope, canEdit)),
            ("GuestTab_Options", () => Options(api, scope, VnetOptions))
        ]);
    }

    /// <summary>노드 방화벽 — 규칙·옵션(로그 수준·보호 기능)·로그. 별칭·IP 집합은 데이터센터 것을 쓴다.</summary>
    public static SubTabsView ForNode(ProxmoxApiClient api, string node, bool canEdit = true)
    {
        var scope = FirewallScope.ForNode(node);
        return new SubTabsView(
        [
            ("DcFirewall_Rules", () => new FirewallTab(api, scope, canEdit)),
            ("GuestTab_Options", () => Options(api, scope, NodeOptions)),
            ("DcFirewall_Log", () => Log(api, scope))
        ]);
    }

    /// <summary>게스트 방화벽 — 규칙·별칭·IP 집합·옵션(정책·MAC/IP 필터)·로그.</summary>
    public static SubTabsView ForGuest(ProxmoxApiClient api, PveResource guest, bool canEdit)
    {
        var scope = FirewallScope.ForGuest(guest.Node, guest.Kind, guest.VmId);
        return new SubTabsView(
        [
            ("DcFirewall_Rules", () => new FirewallTab(api, scope, canEdit)),
            ("DcFirewall_Aliases", () => Aliases(api, scope, canEdit)),
            ("DcFirewall_IpSets", () => IpSets(api, scope, canEdit)),
            ("GuestTab_Options", () => Options(api, scope, GuestFirewallOptions)),
            ("DcFirewall_Log", () => Log(api, scope))
        ]);
    }

    private static TableTab Aliases(ProxmoxApiClient api, FirewallScope scope, bool canEdit)
    {
        return new TableTab(() => api.Firewall.ListAliasesAsync(scope), AliasColumns, "DcFirewall_AliasesHint",
            canEdit ? AliasActions(api, scope) : null);
    }

    private static TableTab IpSets(ProxmoxApiClient api, FirewallScope scope, bool canEdit)
    {
        return new TableTab(() => api.Firewall.ListIpSetsAsync(scope), IpSetColumns, "DcFirewall_IpSetsHint",
            IpSetActions(api, scope, canEdit));
    }

    /// <summary>옵션 — 서버가 모르는 옵션(예: 8.3 전의 policy_forward)은 목록에 두지 않는다.</summary>
    private static OptionsTab Options(ProxmoxApiClient api, FirewallScope scope, IReadOnlyList<GuestOption> options)
    {
        return new OptionsTab(options, () => api.Firewall.GetOptionsAsync(scope),
            async changes => await api.Firewall.UpdateOptionsAsync(scope, UpdateForm(changes)),
            api.Firewall.Feature(nameof(FirewallApi.UpdateOptionsAsync)));
    }

    /// <summary>방화벽이 남긴 최근 기록(읽기 전용).</summary>
    private static TextEditTab Log(ProxmoxApiClient api, FirewallScope scope)
    {
        return new TextEditTab(async () =>
        {
            var lines = await api.Firewall.LogAsync(scope);
            return (string.Join('\n', lines.Select(l => Value(l, "t"))), string.Empty);
        }, null, "DcFirewall_LogHint");
    }

    private static IReadOnlyList<TableAction> GroupActions(ProxmoxApiClient api, bool canEdit)
    {
        var rules = new TableAction
        {
            LabelKey = "DcFirewall_Rules", IconKey = "IconList", NeedsSelection = true,
            Run = (row, owner) => Task.FromResult(TableWindow.ShowModal(owner,
                Loc.T("DcFirewall_GroupRulesTitle", row!["group"]),
                new FirewallTab(api, FirewallScope.ForSecurityGroup(row["group"]), canEdit)))
        };
        if (!canEdit) return [rules];

        return
        [
            rules,
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitAsync(owner, "DcFirewall_AddGroup",
                [
                    new FormField { Key = "group", LabelKey = "Table_Name", Required = true },
                    new FormField { Key = "comment", LabelKey = "Table_Comment" }
                ], values => api.Firewall.SaveGroupAsync(NonEmpty(values)), "DcFirewall_Added")
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcFirewall_EditGroup", row!["group"]),
                    RenameFields("group", row["group"], Value(row, "comment")),
                    values => api.Firewall.SaveGroupAsync(RenameForm("group", row["group"], values)),
                    "DcFirewall_Updated", titleIsKey: false)
            },
            DeleteAction(row => Loc.T("DcFirewall_DeleteConfirm", row["group"]),
                row => api.Firewall.DeleteGroupAsync(row["group"]), "DcFirewall_Deleted")
        ];
    }

    private static IReadOnlyList<TableAction> AliasActions(ProxmoxApiClient api, FirewallScope scope)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitAsync(owner, "DcFirewall_AddAlias",
                [
                    new FormField { Key = "name", LabelKey = "Table_Name", Required = true },
                    new FormField { Key = "cidr", LabelKey = "DcFirewall_Cidr", Required = true },
                    new FormField { Key = "comment", LabelKey = "Table_Comment" }
                ], values => api.Firewall.CreateAliasAsync(scope, NonEmpty(values)), "DcFirewall_Added")
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcFirewall_EditAlias", row!["name"]),
                [
                    new FormField { Key = "name", LabelKey = "Table_Name", Required = true, Initial = row["name"],
                        Trim = true },
                    new FormField
                    {
                        Key = "cidr", LabelKey = "DcFirewall_Cidr", Required = true, Initial = Value(row, "cidr")
                    },
                    new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Value(row, "comment") }
                ], values =>
                {
                    // 이름을 바꾸면 rename 으로 새 이름을 준다(웹 UI 와 같다)
                    var form = new Dictionary<string, string>
                    {
                        ["cidr"] = values["cidr"], ["comment"] = values["comment"]
                    };
                    if (values["name"] != row["name"]) form["rename"] = values["name"];
                    return api.Firewall.UpdateAliasAsync(scope, row["name"], form);
                }, "DcFirewall_Updated", titleIsKey: false)
            },
            DeleteAction(row => Loc.T("DcFirewall_DeleteConfirm", row["name"]),
                row => api.Firewall.DeleteAliasAsync(scope, row["name"]), "DcFirewall_Deleted")
        ];
    }

    private static IReadOnlyList<TableAction> IpSetActions(ProxmoxApiClient api, FirewallScope scope, bool canEdit)
    {
        var entries = new TableAction
        {
            LabelKey = "DcFirewall_Entries", IconKey = "IconList", NeedsSelection = true,
            Run = (row, owner) =>
            {
                var name = row!["name"];
                return Task.FromResult(TableWindow.ShowModal(owner, Loc.T("DcFirewall_EntriesTitle", name),
                    new TableTab(() => api.Firewall.ListIpSetEntriesAsync(scope, name), IpSetEntryColumns,
                        "DcFirewall_EntriesHint", canEdit ? IpSetEntryActions(api, scope, name) : null)));
            }
        };
        if (!canEdit) return [entries];

        return
        [
            entries,
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitAsync(owner, "DcFirewall_AddIpSet",
                [
                    new FormField { Key = "name", LabelKey = "Table_Name", Required = true },
                    new FormField { Key = "comment", LabelKey = "Table_Comment" }
                ], values => api.Firewall.SaveIpSetAsync(scope, NonEmpty(values)), "DcFirewall_Added")
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcFirewall_EditIpSet", row!["name"]),
                    RenameFields("name", row["name"], Value(row, "comment")),
                    values => api.Firewall.SaveIpSetAsync(scope, RenameForm("name", row["name"], values)),
                    "DcFirewall_Updated", titleIsKey: false)
            },
            DeleteAction(row => Loc.T("DcFirewall_DeleteIpSetConfirm", row["name"]),
                row => api.Firewall.DeleteIpSetAsync(scope, row["name"]), "DcFirewall_Deleted")
        ];
    }

    private static IReadOnlyList<TableAction> IpSetEntryActions(ProxmoxApiClient api, FirewallScope scope,
        string name)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitAsync(owner, "DcFirewall_AddEntry",
                [
                    new FormField { Key = "cidr", LabelKey = "DcFirewall_Cidr", Required = true },
                    new FormField { Key = "nomatch", LabelKey = "DcFirewall_NoMatch", Kind = FormFieldKind.Bool },
                    new FormField { Key = "comment", LabelKey = "Table_Comment" }
                ], values => api.Firewall.AddIpSetEntryAsync(scope, name, NonEmpty(values)), "DcFirewall_Added")
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcFirewall_EditEntry", row!["cidr"]),
                [
                    new FormField { Key = "nomatch", LabelKey = "DcFirewall_NoMatch", Kind = FormFieldKind.Bool,
                        Initial = Value(row, "nomatch") is "1" ? "1" : "0" },
                    new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Value(row, "comment") }
                ], values => api.Firewall.UpdateIpSetEntryAsync(scope, name, row["cidr"], values), "DcFirewall_Updated",
                    titleIsKey: false)
            },
            DeleteAction(row => Loc.T("DcFirewall_DeleteConfirm", row["cidr"]),
                row => api.Firewall.DeleteIpSetEntryAsync(scope, name, row["cidr"]), "DcFirewall_Deleted")
        ];
    }

    /// <summary>이름·설명 칸 — 보안 그룹·IP 집합 수정 창.</summary>
    private static List<FormField> RenameFields(string key, string name, string comment)
    {
        return
        [
            new FormField { Key = key, LabelKey = "Table_Name", Required = true, Initial = name, Trim = true },
            new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = comment }
        ];
    }

    /// <summary>
    ///     보안 그룹·IP 집합 수정은 만들기와 같은 POST 에 rename=옛 이름을 붙인다(웹 UI 와 같다).
    ///     이름이 그대로면 설명만 바뀐다.
    /// </summary>
    private static Dictionary<string, string> RenameForm(string key, string oldName,
        IReadOnlyDictionary<string, string> values)
    {
        return new Dictionary<string, string>
        {
            [key] = values[key], ["rename"] = oldName, ["comment"] = values["comment"]
        };
    }
}
