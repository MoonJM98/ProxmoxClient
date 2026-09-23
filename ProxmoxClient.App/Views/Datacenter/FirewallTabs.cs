using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Guest.Tabs;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>데이터센터 방화벽 — 웹 UI 처럼 규칙·보안 그룹·별칭·IP 집합·옵션 하위 탭.</summary>
internal static class FirewallTabs
{
    private const string ClusterPath = "cluster/firewall";

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
            ("DcFirewall_Rules", () => new FirewallTab(api, FirewallScope.Cluster)),
            ("DcFirewall_Groups", () => new TableTab(() => api.GetTableAsync($"{ClusterPath}/groups"), GroupColumns,
                "DcFirewall_GroupsHint", GroupActions(api, canEdit))),
            ("DcFirewall_Aliases", () => Aliases(api, ClusterPath, canEdit)),
            ("DcFirewall_IpSets", () => IpSets(api, ClusterPath, canEdit)),
            ("GuestTab_Options", () => Options(api, ClusterPath, ClusterOptions))
        ]);
    }

    /// <summary>노드 방화벽 — 규칙·옵션(로그 수준·보호 기능)·로그. 별칭·IP 집합은 데이터센터 것을 쓴다.</summary>
    public static SubTabsView ForNode(ProxmoxApiClient api, string node)
    {
        var scope = FirewallScope.ForNode(node);
        return new SubTabsView(
        [
            ("DcFirewall_Rules", () => new FirewallTab(api, scope)),
            ("GuestTab_Options", () => Options(api, scope.BasePath, NodeOptions)),
            ("DcFirewall_Log", () => Log(api, scope.BasePath))
        ]);
    }

    /// <summary>게스트 방화벽 — 규칙·별칭·IP 집합·옵션(정책·MAC/IP 필터)·로그.</summary>
    public static SubTabsView ForGuest(ProxmoxApiClient api, PveResource guest, bool canEdit)
    {
        var scope = FirewallScope.ForGuest(guest.Node, guest.Kind, guest.VmId);
        return new SubTabsView(
        [
            ("DcFirewall_Rules", () => new FirewallTab(api, scope)),
            ("DcFirewall_Aliases", () => Aliases(api, scope.BasePath, canEdit)),
            ("DcFirewall_IpSets", () => IpSets(api, scope.BasePath, canEdit)),
            ("GuestTab_Options", () => Options(api, scope.BasePath, GuestFirewallOptions)),
            ("DcFirewall_Log", () => Log(api, scope.BasePath))
        ]);
    }

    private static TableTab Aliases(ProxmoxApiClient api, string basePath, bool canEdit)
    {
        return new TableTab(() => api.GetTableAsync($"{basePath}/aliases"), AliasColumns, "DcFirewall_AliasesHint",
            canEdit ? AliasActions(api, basePath) : null);
    }

    private static TableTab IpSets(ProxmoxApiClient api, string basePath, bool canEdit)
    {
        return new TableTab(() => api.GetTableAsync($"{basePath}/ipset"), IpSetColumns, "DcFirewall_IpSetsHint",
            IpSetActions(api, basePath, canEdit));
    }

    private static OptionsTab Options(ProxmoxApiClient api, string basePath, IReadOnlyList<GuestOption> options)
    {
        return new OptionsTab(options, () => api.GetObjectAsync($"{basePath}/options"),
            async changes => await api.PutActionAsync($"{basePath}/options", UpdateForm(changes)));
    }

    /// <summary>방화벽이 남긴 최근 기록(읽기 전용).</summary>
    private static TextEditTab Log(ProxmoxApiClient api, string basePath)
    {
        return new TextEditTab(async () =>
        {
            var lines = await api.GetTableAsync($"{basePath}/log?limit=500");
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
                new FirewallTab(api, FirewallScope.ForSecurityGroup(row["group"]))))
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
                ], values => api.PostActionAsync($"{ClusterPath}/groups", NonEmpty(values)), "DcFirewall_Added")
            },
            DeleteAction(row => Loc.T("DcFirewall_DeleteConfirm", row["group"]),
                row => api.DeleteActionAsync($"{ClusterPath}/groups/{Seg(row["group"])}"), "DcFirewall_Deleted")
        ];
    }

    private static IReadOnlyList<TableAction> AliasActions(ProxmoxApiClient api, string basePath)
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
                ], values => api.PostActionAsync($"{basePath}/aliases", NonEmpty(values)), "DcFirewall_Added")
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcFirewall_EditAlias", row!["name"]),
                [
                    new FormField
                    {
                        Key = "cidr", LabelKey = "DcFirewall_Cidr", Required = true, Initial = Value(row, "cidr")
                    },
                    new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Value(row, "comment") }
                ], values => api.PutActionAsync($"{basePath}/aliases/{Seg(row["name"])}", values),
                    "DcFirewall_Updated", titleIsKey: false)
            },
            DeleteAction(row => Loc.T("DcFirewall_DeleteConfirm", row["name"]),
                row => api.DeleteActionAsync($"{basePath}/aliases/{Seg(row["name"])}"), "DcFirewall_Deleted")
        ];
    }

    private static IReadOnlyList<TableAction> IpSetActions(ProxmoxApiClient api, string basePath, bool canEdit)
    {
        var entries = new TableAction
        {
            LabelKey = "DcFirewall_Entries", IconKey = "IconList", NeedsSelection = true,
            Run = (row, owner) =>
            {
                var path = $"{basePath}/ipset/{Seg(row!["name"])}";
                return Task.FromResult(TableWindow.ShowModal(owner, Loc.T("DcFirewall_EntriesTitle", row["name"]),
                    new TableTab(() => api.GetTableAsync(path), IpSetEntryColumns, "DcFirewall_EntriesHint",
                        canEdit ? IpSetEntryActions(api, path) : null)));
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
                ], values => api.PostActionAsync($"{basePath}/ipset", NonEmpty(values)), "DcFirewall_Added")
            },
            DeleteAction(row => Loc.T("DcFirewall_DeleteIpSetConfirm", row["name"]),
                row => api.DeleteActionAsync($"{basePath}/ipset/{Seg(row["name"])}?force=1"), "DcFirewall_Deleted")
        ];
    }

    private static IReadOnlyList<TableAction> IpSetEntryActions(ProxmoxApiClient api, string path)
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
                ], values => api.PostActionAsync(path, NonEmpty(values)), "DcFirewall_Added")
            },
            DeleteAction(row => Loc.T("DcFirewall_DeleteConfirm", row["cidr"]),
                row => api.DeleteActionAsync($"{path}/{Seg(row["cidr"])}"), "DcFirewall_Deleted")
        ];
    }
}
