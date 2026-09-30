using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     SDN 라우팅 정책(9.1+) — 접두사 목록(항목: 순서·허용/거부·접두사·ge·le)과 라우트 맵 항목(조건·설정 줄).
///     바꾼 뒤 SDN 을 적용해야 노드에 반영된다.
/// </summary>
internal static class SdnRouting
{
    private static readonly IReadOnlyList<TableColumn> ListColumns =
    [
        new() { Key = "id", HeaderKey = "Table_Name", Width = 0 },
        new() { Key = "state", HeaderKey = "Table_State", Width = 100 }
    ];

    private static readonly IReadOnlyList<TableColumn> EntryColumns =
    [
        new() { Key = "seq", HeaderKey = "SdnRoute_Seq", Width = 70 },
        new() { Key = "action", HeaderKey = "FirewallWindow_08", Width = 80 },
        new() { Key = "prefix", HeaderKey = "SdnRoute_Prefix", Width = 0 },
        new() { Key = "ge", HeaderKey = "SdnRoute_Ge", Width = 60 },
        new() { Key = "le", HeaderKey = "SdnRoute_Le", Width = 60 }
    ];

    private static readonly IReadOnlyList<TableColumn> MapColumns =
    [
        new() { Key = "route-map-id", HeaderKey = "Table_Name", Width = 120 },
        new() { Key = "order", HeaderKey = "SdnRoute_Seq", Width = 70 },
        new() { Key = "action", HeaderKey = "FirewallWindow_08", Width = 80 },
        new() { Key = "match", HeaderKey = "SdnRoute_Match", Width = 0 },
        new() { Key = "set", HeaderKey = "SdnRoute_Set", Width = 200 }
    ];

    private static readonly (string, string)[] Actions = [("permit", "permit"), ("deny", "deny")];

    public static SubTab Tab(ProxmoxApiClient api, bool canEdit)
    {
        return new SubTab("SdnRoute_Tab", () => new SubTabsView(
            [
                ("SdnRoute_PrefixLists", () => new TableTab(() => api.SdnRouting.ListPrefixListsAsync(), ListColumns,
                    "SdnRoute_PrefixListsHint", PrefixListActions(api, canEdit))),
                ("SdnRoute_RouteMaps", () => new TableTab(() => api.SdnRouting.ListRouteMapEntriesAsync(), MapColumns,
                    "SdnRoute_RouteMapsHint", canEdit ? RouteMapActions(api) : null))
            ]),
            api.SdnRouting.Feature(nameof(SdnRoutingApi.ListPrefixListsAsync)));
    }

    // ------------------------------------------------------------ 접두사 목록

    private static IReadOnlyList<TableAction> PrefixListActions(ProxmoxApiClient api, bool canEdit)
    {
        var entries = new TableAction
        {
            LabelKey = "SdnRoute_Entries", IconKey = "IconList", NeedsSelection = true,
            Run = (row, owner) =>
            {
                var id = row!["id"];
                return Task.FromResult(TableWindow.ShowModal(owner, Loc.T("SdnRoute_EntriesTitle", id),
                    new TableTab(() => api.SdnRouting.ListPrefixEntriesAsync(id), EntryColumns,
                        "SdnRoute_EntriesHint", canEdit ? EntryActions(api, id) : null)));
            }
        };
        if (!canEdit) return [entries];

        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitAsync(owner, "SdnRoute_AddList",
                    [new FormField { Key = "id", LabelKey = "Table_Name", Required = true, Trim = true }],
                    async values =>
                    {
                        await api.SdnRouting.CreatePrefixListAsync(values["id"]);
                        return string.Empty;
                    }, "SdnRoute_Saved")
            },
            entries,
            DeleteAction(row => Loc.T("SdnRoute_DeleteListConfirm", row["id"]),
                row => api.SdnRouting.DeletePrefixListAsync(row["id"]), "SdnRoute_Deleted")
        ];
    }

    private static IReadOnlyList<TableAction> EntryActions(ProxmoxApiClient api, string id)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => EditEntryAsync(api, id, null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditEntryAsync(api, id, row, owner)
            },
            DeleteAction(row => Loc.T("SdnRoute_DeleteEntryConfirm", Value(row, "seq")),
                row => api.SdnRouting.DeletePrefixEntryAsync(id, Value(row, "seq")), "SdnRoute_Deleted")
        ];
    }

    private static Task<string?> EditEntryAsync(ProxmoxApiClient api, string id,
        IReadOnlyDictionary<string, string>? row, Window? owner)
    {
        string V(string key) => row is null ? string.Empty : Value(row, key);
        var fields = new List<FormField>();
        if (row is null)
            fields.Add(new FormField { Key = "seq", LabelKey = "SdnRoute_Seq", Trim = true,
                Hint = Loc.T("SdnRoute_SeqHint") });
        return SubmitAsync(owner, row is null ? Loc.T("SdnRoute_AddEntry") : Loc.T("SdnRoute_EditEntry", V("seq")),
        [
            ..fields,
            new FormField { Key = "action", LabelKey = "FirewallWindow_08", Kind = FormFieldKind.Choice,
                Choices = Actions, Initial = row is null ? "permit" : V("action") },
            new FormField { Key = "prefix", LabelKey = "SdnRoute_Prefix", Initial = V("prefix"), Required = true,
                Trim = true, Hint = Loc.T("SdnRoute_PrefixHint") },
            new FormField { Key = "ge", LabelKey = "SdnRoute_Ge", Initial = V("ge"), Trim = true, Advanced = true },
            new FormField { Key = "le", LabelKey = "SdnRoute_Le", Initial = V("le"), Trim = true, Advanced = true }
        ], async values =>
        {
            if (row is null) await api.SdnRouting.CreatePrefixEntryAsync(id, NonEmpty(values));
            else await api.SdnRouting.UpdatePrefixEntryAsync(id, V("seq"), UpdateForm(values));
            return string.Empty;
        }, "SdnRoute_Saved", titleIsKey: false);
    }

    // ------------------------------------------------------------ 라우트 맵

    private static IReadOnlyList<TableAction> RouteMapActions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => EditMapAsync(api, null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditMapAsync(api, row, owner)
            },
            DeleteAction(row => Loc.T("SdnRoute_DeleteMapConfirm", Value(row, "route-map-id"), Value(row, "order")),
                row => api.SdnRouting.DeleteRouteMapEntryAsync(Value(row, "route-map-id"), Value(row, "order")),
                "SdnRoute_Deleted")
        ];
    }

    private static Task<string?> EditMapAsync(ProxmoxApiClient api, IReadOnlyDictionary<string, string>? row,
        Window? owner)
    {
        string V(string key) => row is null ? string.Empty : Value(row, key);
        var fields = new List<FormField>();
        if (row is null)
            fields.AddRange(
            [
                new FormField { Key = "route-map-id", LabelKey = "Table_Name", Required = true, Trim = true },
                new FormField { Key = "order", LabelKey = "SdnRoute_Seq", Required = true, Trim = true }
            ]);
        fields.AddRange(
        [
            new FormField { Key = "action", LabelKey = "FirewallWindow_08", Kind = FormFieldKind.Choice,
                Choices = Actions, Initial = row is null ? "permit" : V("action") },
            new FormField { Key = "match", LabelKey = "SdnRoute_Match", Kind = FormFieldKind.Multiline,
                CanLoadFile = false, Initial = Lines(V("match")), Hint = Loc.T("SdnRoute_MatchHint") },
            new FormField { Key = "set", LabelKey = "SdnRoute_Set", Kind = FormFieldKind.Multiline,
                CanLoadFile = false, Initial = Lines(V("set")), Hint = Loc.T("SdnRoute_SetHint") },
            new FormField { Key = "call", LabelKey = "SdnRoute_Call", Initial = V("call"), Trim = true,
                Advanced = true },
            new FormField { Key = "exit-action", LabelKey = "SdnRoute_ExitAction", Initial = V("exit-action"),
                Trim = true, Advanced = true }
        ]);

        return SubmitAsync(owner, row is null
            ? Loc.T("SdnRoute_AddMap")
            : Loc.T("SdnRoute_EditMap", V("route-map-id"), V("order")), fields, async values =>
        {
            var match = Split(values["match"]);
            var set = Split(values["set"]);
            var form = values.Where(kv => kv.Key is not ("match" or "set"))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            if (row is null)
            {
                await api.SdnRouting.CreateRouteMapEntryAsync(NonEmpty(form), match, set);
                return string.Empty;
            }

            // 비운 칸은 서버 기본값으로(match·set 을 모두 지웠으면 그것도)
            var delete = form.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key)
                .Concat(match.Count == 0 ? ["match"] : Array.Empty<string>())
                .Concat(set.Count == 0 ? ["set"] : Array.Empty<string>())
                .ToList();
            await api.SdnRouting.UpdateRouteMapEntryAsync(V("route-map-id"), V("order"), NonEmpty(form), match,
                set, delete);
            return string.Empty;
        }, "SdnRoute_Saved", titleIsKey: false);
    }

    /// <summary>서버가 쉼표로 이어 준 목록 값을 줄마다 하나로 — 항목 안의 쉼표(key=…,value=…)는 key= 로 나눈다.</summary>
    private static string Lines(string joined)
    {
        return string.Join('\n', joined.Split(", key=").Select((part, i) => i == 0 ? part : "key=" + part));
    }

    private static List<string> Split(string text)
    {
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
