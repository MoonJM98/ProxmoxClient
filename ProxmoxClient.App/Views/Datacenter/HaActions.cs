using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     HA 화면(웹 UI ha/*) — 서버 버전에 따라 탭이 다르다: PVE 9 는 규칙(rules), PVE 8 이하는 그룹(groups).
///     리소스는 게스트를 목록에서 고르고, PVE 8 은 그룹을 목록에서, PVE 9 는 되돌아가기(failback)를 정한다.
/// </summary>
internal static class HaActions
{
    private const int RulesMajorVersion = 9;
    private const int MaxRetries = 10;

    private static readonly IReadOnlyList<(string, string)> HaStates =
    [
        ("started", "HaState_Started"), ("stopped", "HaState_Stopped"),
        ("disabled", "HaState_Disabled"), ("ignored", "HaState_Ignored")
    ];

    /// <summary>버전을 읽은 뒤 탭을 채운다 — 읽지 못하면 두 탭을 모두 보인다(서버가 알맞은 쪽만 응답).</summary>
    public static UIElement View(ProxmoxApiClient api, bool canEdit,
        Func<ProxmoxApiClient, bool, bool, UIElement> resources)
    {
        var host = new ContentControl();
        host.Loaded += async (_, _) =>
        {
            if (host.Content is not null) return;
            // 접속 때 읽어 둔 서버 버전을 쓴다(없으면 한 번 읽고, 그래도 모르면 두 방식을 모두 보인다)
            var version = await api.GetServerVersionAsync();
            host.Content = Tabs(api, canEdit, version?.Major, resources);
        };
        return host;
    }

    /// <param name="major">서버 주 버전(모르면 null — 규칙·그룹 탭을 모두 둔다).</param>
    internal static SubTabsView Tabs(ProxmoxApiClient api, bool canEdit, int? major,
        Func<ProxmoxApiClient, bool, bool, UIElement> resources)
    {
        var hasRules = major is null or >= RulesMajorVersion;
        var hasGroups = major is null or < RulesMajorVersion;
        var tabs = new List<(string, Func<UIElement>)>
        {
            ("DcHa_Status", () => DatacenterTables.HaStatus(api,
                [ManagerStatus(api), ..(canEdit ? ArmActions(api) : [])])),
            ("DcHa_Resources", () => resources(api, canEdit, major >= RulesMajorVersion))
        };
        if (hasRules) tabs.Add(("DcTab_HaRules", () => DatacenterTables.HaRules(api, canEdit)));
        if (hasGroups)
            tabs.Add(("DcHaGroups_Tab", () => new TableTab(() => api.Ha.ListGroupsAsync(),
                HaGroupActions.Columns, "DcHaGroups_Hint", canEdit ? HaGroupActions.Actions(api) : null)));
        return new SubTabsView(tabs);
    }

    /// <summary>HA 관리자 전체 상태(관리자·노드별 LRM·서비스) — 누구나 볼 수 있다.</summary>
    private static TableAction ManagerStatus(ProxmoxApiClient api)
    {
        return new TableAction
        {
            LabelKey = "DcHa_ManagerStatus", IconKey = "IconList",
            Run = async (_, owner) => TextViewWindow.ShowModal(owner, Loc.T("DcHa_ManagerStatus"),
                await api.Ha.ManagerStatusJsonAsync())
        };
    }

    /// <summary>
    ///     HA 무장 해제·재무장(9.1+) — 유지 보수 중 워치독이 노드를 재부팅하지 않도록 HA 스택을 잠시 끈다.
    /// </summary>
    private static IReadOnlyList<TableAction> ArmActions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "DcHa_Disarm", IconKey = "IconPause",
                Requires = api.Ha.Feature(nameof(Core.Api.Domains.HaApi.DisarmAsync)),
                Run = (_, owner) => SubmitTaskAsync(api, owner, Loc.T("DcHa_Disarm"),
                [
                    new FormField
                    {
                        Key = "resource-mode", LabelKey = "DcHa_DisarmMode", Kind = FormFieldKind.Choice,
                        Initial = "freeze", Choices = [("freeze", "DcHa_ModeFreeze"), ("ignore", "DcHa_ModeIgnore")],
                        Hint = Loc.T("DcHa_DisarmHint")
                    }
                ], values => api.Ha.DisarmAsync(values["resource-mode"]), "DcHa_Disarmed")
            },
            new TableAction
            {
                LabelKey = "DcHa_Arm", IconKey = "IconPlay",
                Requires = api.Ha.Feature(nameof(Core.Api.Domains.HaApi.ArmAsync)),
                Confirm = _ => Loc.T("DcHa_ArmConfirm"),
                Run = async (_, _) => await RunTaskAsync(api, api.Ha.ArmAsync(), "DcHa_Armed")
            }
        ];
    }

    /// <param name="rules">PVE 9 이상 — 그룹 대신 failback.</param>
    public static IReadOnlyList<TableAction> Resources(ProxmoxApiClient api, bool rules)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => EditAsync(api, null, rules, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditAsync(api, row, rules, owner)
            },
            MoveAction(api, "DcHa_Migrate", "IconSwitch", api.Ha.MigrateResourceAsync),
            MoveAction(api, "DcHa_Relocate", "IconRotate", api.Ha.RelocateResourceAsync),
            DeleteAction(row => Loc.T("DcHa_DeleteConfirm", row["sid"]),
                row => api.Ha.DeleteResourceAsync(row["sid"]), "DcHa_Deleted")
        ];
    }

    /// <summary>
    ///     HA 자원 옮기기 — 이전(migrate)은 켜진 채로, 재배치(relocate)는 멈췄다가 다른 노드에서 다시 시작한다.
    ///     HA 관리자가 요청을 받아 처리하므로 작업이 끝나도 실제 이동은 조금 뒤에 보일 수 있다.
    /// </summary>
    private static TableAction MoveAction(ProxmoxApiClient api, string labelKey, string iconKey,
        Func<string, string, CancellationToken, Task<string>> move)
    {
        return new TableAction
        {
            LabelKey = labelKey, IconKey = iconKey, NeedsSelection = true,
            Run = async (row, owner) =>
            {
                var sid = row!["sid"];
                var nodes = (await api.GetNodesAsync())
                    .Where(n => string.Equals(n.Status, "online", StringComparison.OrdinalIgnoreCase))
                    .Select(n => (n.Node, n.Node))
                    .OrderBy(n => n.Item1, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return await SubmitTaskAsync(api, owner, Loc.T("DcHa_MoveTitle", Loc.T(labelKey), sid),
                [
                    new FormField { Key = "node", LabelKey = "Table_Target", Kind = FormFieldKind.Choice,
                        Choices = nodes, Required = true }
                ], values => move(sid, values["node"], CancellationToken.None), "DcHa_MoveRequested");
            }
        };
    }

    private static async Task<string?> EditAsync(ProxmoxApiClient api, IReadOnlyDictionary<string, string>? row,
        bool rules, Window? owner)
    {
        string Initial(string key, string fallback = "") => row is null ? fallback : Value(row, key);
        var fields = new List<FormField>();
        if (row is null)
        {
            var managed = (await api.Ha.ListResourcesAsync()).Select(r => Value(r, "sid")).ToHashSet();
            var guests = (await api.Cluster.ResourcesAsync("vm"))
                .Where(r => Value(r, "template") is not "1")
                .Select(r => (Sid: $"{(Value(r, "type") == "lxc" ? "ct" : "vm")}:{Value(r, "vmid")}", Row: r))
                .Where(g => !managed.Contains(g.Sid))
                .OrderBy(g => int.TryParse(Value(g.Row, "vmid"), out var id) ? id : int.MaxValue)
                .Select(g => (g.Sid, $"{g.Sid} ({Value(g.Row, "name")}) — {Value(g.Row, "node")}"))
                .ToList();
            fields.Add(new FormField { Key = "sid", LabelKey = "Table_Guest", Kind = FormFieldKind.Choice,
                Choices = guests, Required = true, Hint = Loc.T("DcHa_GuestHint") });
        }

        if (rules)
            fields.Add(new FormField { Key = "failback", LabelKey = "DcHa_Failback", Kind = FormFieldKind.Bool,
                Initial = Initial("failback", "1") is "0" ? "0" : "1", Hint = Loc.T("DcHa_FailbackHint") });
        else
            fields.Add(new FormField { Key = "group", LabelKey = "Table_Group", Kind = FormFieldKind.Choice,
                Initial = Initial("group"), Choices = await GroupChoicesAsync(api) });

        fields.AddRange(
        [
            new FormField { Key = "max_restart", LabelKey = "Table_MaxRestart", Initial = Initial("max_restart", "1"),
                Trim = true, Hint = "0–10" },
            new FormField { Key = "max_relocate", LabelKey = "Table_MaxRelocate",
                Initial = Initial("max_relocate", "1"), Trim = true, Hint = "0–10" },
            new FormField { Key = "state", LabelKey = "Table_State", Kind = FormFieldKind.Choice, Choices = HaStates,
                Initial = Initial("state", "started") },
            new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Initial("comment") }
        ]);

        return await SubmitAsync(owner, row is null ? Loc.T("DcHa_AddTitle") : Loc.T("DcHa_EditTitle", row["sid"]),
            fields, values => row is null
                ? api.Ha.CreateResourceAsync(NonEmpty(values))
                : api.Ha.UpdateResourceAsync(row["sid"], UpdateForm(values)),
            row is null ? "DcHa_Added" : "DcHa_Updated", titleIsKey: false, validate: Validate);
    }

    internal static string? Validate(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("sid", out var sid) && sid.Length == 0) return Loc.T("DcHa_PickGuest");
        return new[] { "max_restart", "max_relocate" }.All(k =>
            int.TryParse(values[k], out var n) && n is >= 0 and <= MaxRetries)
            ? null
            : Loc.T("DcHa_BadRetries", MaxRetries);
    }

    private static async Task<IReadOnlyList<(string, string)>> GroupChoicesAsync(ProxmoxApiClient api)
    {
        var list = new List<(string, string)> { ("", "DcHa_NoGroup") };
        try
        {
            list.AddRange((await api.Ha.ListGroupsAsync()).Select(g => Value(g, "group"))
                .Where(g => g.Length > 0).Order(StringComparer.OrdinalIgnoreCase).Select(g => (g, g)));
        }
        catch (ProxmoxApiException)
        {
            // 그룹 목록을 못 읽어도(권한·버전) 그룹 없이 추가할 수 있다
        }

        return list;
    }
}
