using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     HA 그룹(PVE 8 방식) — 리소스를 어느 노드에서 우선 실행할지 정한다. PVE 9 에서는 HA 규칙으로 바뀌어
///     서버가 그룹 기능을 막을 수 있다(그때는 규칙 탭을 쓴다).
/// </summary>
internal static class HaGroupActions
{
    public static IReadOnlyList<TableColumn> Columns { get; } =
    [
        new() { Key = "group", HeaderKey = "Table_Name", Width = 140 },
        new() { Key = "nodes", HeaderKey = "Table_Nodes", Width = 0 },
        new() { Key = "restricted", HeaderKey = "DcHaGroups_Restricted", Width = 80, Format = TableFormats.Flag },
        new() { Key = "nofailback", HeaderKey = "DcHaGroups_NoFailback", Width = 90, Format = TableFormats.Flag },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 160 }
    ];

    public static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => EditAsync(api, null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditAsync(api, row, owner)
            },
            DeleteAction(row => Loc.T("DcHaGroups_DeleteConfirm", row["group"]),
                row => api.DeleteActionAsync($"cluster/ha/groups/{Seg(row["group"])}"), "DcHaGroups_Deleted")
        ];
    }

    private static Task<string?> EditAsync(ProxmoxApiClient api, IReadOnlyDictionary<string, string>? row,
        Window? owner)
    {
        string Initial(string key, string fallback = "") => row is null ? fallback : Value(row, key);

        var fields = new List<FormField>();
        if (row is null) fields.Add(new FormField { Key = "group", LabelKey = "Table_Name", Required = true });
        fields.AddRange(
        [
            new FormField
            {
                Key = "nodes", LabelKey = "DcHaRules_NodesHint", Required = true, Initial = Initial("nodes")
            },
            new FormField
            {
                Key = "restricted", LabelKey = "DcHaGroups_Restricted", Kind = FormFieldKind.Bool,
                Initial = Initial("restricted", "0")
            },
            new FormField
            {
                Key = "nofailback", LabelKey = "DcHaGroups_NoFailback", Kind = FormFieldKind.Bool,
                Initial = Initial("nofailback", "0")
            },
            new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Initial("comment") }
        ]);

        var title = row is null ? Loc.T("DcHaGroups_AddTitle") : Loc.T("DcHaGroups_EditTitle", row["group"]);
        return SubmitAsync(owner, title, fields, values =>
        {
            if (row is not null)
                return api.PutActionAsync($"cluster/ha/groups/{Seg(row["group"])}", UpdateForm(values));

            var form = NonEmpty(values);
            form["type"] = "group";
            return api.PostActionAsync("cluster/ha/groups", form);
        }, row is null ? "DcHaGroups_Added" : "DcHaGroups_Updated", titleIsKey: false);
    }
}
