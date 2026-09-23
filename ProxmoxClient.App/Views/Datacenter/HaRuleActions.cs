using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     HA 규칙(PVE 9 이상, HA 그룹을 대신한다) — 노드 선호(node-affinity)와 리소스 동거/분리(resource-affinity).
/// </summary>
internal static class HaRuleActions
{
    private const string NodeAffinity = "node-affinity";
    private const string ResourceAffinity = "resource-affinity";

    public static IReadOnlyList<TableColumn> Columns { get; } =
    [
        new() { Key = "rule", HeaderKey = "Table_Name", Width = 130 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 130 },
        new() { Key = "resources", HeaderKey = "DcHaRules_Resources", Width = 0 },
        new() { Key = "nodes", HeaderKey = "Table_Nodes", Width = 150 },
        new() { Key = "affinity", HeaderKey = "DcHaRules_Affinity", Width = 80 },
        new() { Key = "strict", HeaderKey = "DcHaRules_Strict", Width = 60, Format = TableFormats.Flag },
        new() { Key = "disable", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.InverseFlag },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 150 }
    ];

    public static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "DcHaRules_AddNode", IconKey = "IconServer",
                Run = (_, owner) => EditAsync(api, NodeAffinity, null, owner)
            },
            new TableAction
            {
                LabelKey = "DcHaRules_AddResource", IconKey = "IconCopy",
                Run = (_, owner) => EditAsync(api, ResourceAffinity, null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditAsync(api, Value(row!, "type"), row, owner)
            },
            DeleteAction(row => Loc.T("DcHaRules_DeleteConfirm", row["rule"]),
                row => api.DeleteActionAsync($"cluster/ha/rules/{Seg(row["rule"])}"), "DcHaRules_Deleted")
        ];
    }

    /// <summary>row 가 null 이면 새 규칙. 규칙 유형은 만든 뒤 바꿀 수 없어 편집 때는 행의 유형을 그대로 쓴다.</summary>
    private static Task<string?> EditAsync(ProxmoxApiClient api, string type,
        IReadOnlyDictionary<string, string>? row, Window? owner)
    {
        string Initial(string key, string fallback = "") => row is null ? fallback : Value(row, key);

        var fields = new List<FormField>();
        if (row is null) fields.Add(new FormField { Key = "rule", LabelKey = "Table_Name", Required = true });
        fields.Add(new FormField
        {
            Key = "resources", LabelKey = "DcHaRules_ResourcesHint", Required = true, Initial = Initial("resources")
        });

        if (type == NodeAffinity)
        {
            fields.Add(new FormField
            {
                Key = "nodes", LabelKey = "DcHaRules_NodesHint", Required = true, Initial = Initial("nodes")
            });
            fields.Add(new FormField
            {
                Key = "strict", LabelKey = "DcHaRules_Strict", Kind = FormFieldKind.Bool,
                Initial = Initial("strict", "0")
            });
        }
        else
        {
            fields.Add(new FormField
            {
                Key = "affinity", LabelKey = "DcHaRules_Affinity", Kind = FormFieldKind.Choice,
                Initial = Initial("affinity", "positive"),
                Choices = [("positive", "DcHaRules_Together"), ("negative", "DcHaRules_Apart")]
            });
        }

        fields.Add(new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Initial("comment") });
        fields.Add(new FormField
        {
            Key = "enabled", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
            Initial = Initial("disable") is "1" ? "0" : "1"
        });

        var title = row is null
            ? Loc.T(type == NodeAffinity ? "DcHaRules_AddNode" : "DcHaRules_AddResource")
            : Loc.T("DcHaRules_EditTitle", row["rule"]);

        return SubmitAsync(owner, title, fields, values =>
        {
            var edited = values.Where(kv => kv.Key != "enabled")
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            edited["disable"] = values["enabled"] == "1" ? "0" : "1";

            if (row is null)
            {
                var form = NonEmpty(edited);
                form["type"] = type;
                return api.PostActionAsync("cluster/ha/rules", form);
            }

            var update = UpdateForm(edited);
            update["type"] = type;
            return api.PutActionAsync($"cluster/ha/rules/{Seg(row["rule"])}", update);
        }, row is null ? "DcHaRules_Added" : "DcHaRules_Updated", titleIsKey: false);
    }
}
