using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     사용자 CPU 모델(9.2+, 웹 UI 데이터센터 → Custom CPU Models) — 기준 모델에 플래그를 더하거나 빼서 이름을 붙인다.
///     VM 은 CPU 종류로 "custom-이름" 을 고른다.
/// </summary>
internal static class CpuModelsTab
{
    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "cputype", HeaderKey = "Table_Name", Width = 150 },
        new() { Key = "reported-model", HeaderKey = "CpuModel_Base", Width = 150 },
        new() { Key = "flags", HeaderKey = "CpuModel_Flags", Width = 0 },
        new() { Key = "hidden", HeaderKey = "CpuModel_Hidden", Width = 70, Format = TableFormats.Flag },
        new() { Key = "phys-bits", HeaderKey = "CpuModel_PhysBits", Width = 90 }
    ];

    public static TableTab Create(ProxmoxApiClient api, bool canEdit)
    {
        return new TableTab(() => api.Cluster.CpuModelsAsync(), Columns, "CpuModel_Hint", canEdit
            ?
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
                DeleteAction(row => Loc.T("CpuModel_DeleteConfirm", row["cputype"]),
                    row => api.Cluster.DeleteCpuModelAsync(row["cputype"]), "CpuModel_Deleted"),
                CpuFlags(api)
            ]
            : [CpuFlags(api)]);
    }

    private static readonly IReadOnlyList<TableColumn> FlagColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "description", HeaderKey = "Table_Description", Width = 0 },
        new() { Key = "supported-on", HeaderKey = "CpuFlag_SupportedOn", Width = 160 }
    ];

    /// <summary>켜고 끌 수 있는 CPU 플래그(플래그 칸에 +이름·-이름 으로 적는다).</summary>
    private static TableAction CpuFlags(ProxmoxApiClient api)
    {
        return new TableAction
        {
            LabelKey = "CpuFlag_Show", IconKey = "IconList",
            Requires = api.Nodes.Feature(nameof(Core.Api.Domains.NodesApi.CpuFlagsAsync)),
            // localhost = 연결된 노드(웹 UI 와 같다) — 버전·CPU 가 다른 노드를 고르지 않게
            Run = (_, owner) => Task.FromResult(TableWindow.ShowModal(owner, Loc.T("CpuFlag_Show"),
                new TableTab(() => api.Nodes.CpuFlagsAsync("localhost"), FlagColumns, "CpuModel_FlagsHint")))
        };
    }

    private static async Task<string?> EditAsync(ProxmoxApiClient api, IReadOnlyDictionary<string, string>? row,
        Window? owner)
    {
        string V(string key) => row is null ? string.Empty : Value(row, key);
        var fields = new List<FormField>();
        if (row is null)
            fields.Add(new FormField { Key = "cputype", LabelKey = "Table_Name", Required = true, Trim = true,
                Hint = Loc.T("CpuModel_NameHint") });
        fields.AddRange(
        [
            new FormField { Key = "reported-model", LabelKey = "CpuModel_Base", Initial = V("reported-model"),
                Trim = true, Hint = Loc.T("CpuModel_BaseHint") },
            new FormField { Key = "flags", LabelKey = "CpuModel_Flags", Initial = V("flags"), Trim = true,
                Hint = Loc.T("CpuModel_FlagsHint") },
            new FormField { Key = "hidden", LabelKey = "CpuModel_Hidden", Kind = FormFieldKind.Bool,
                Initial = V("hidden") is "1" ? "1" : "0" },
            new FormField { Key = "phys-bits", LabelKey = "CpuModel_PhysBits", Initial = V("phys-bits"), Trim = true,
                Advanced = true, Hint = Loc.T("CpuModel_PhysBitsHint") },
            new FormField { Key = "guest-phys-bits", LabelKey = "CpuModel_GuestPhysBits",
                Initial = V("guest-phys-bits"), Trim = true, Advanced = true },
            new FormField { Key = "hv-vendor-id", LabelKey = "CpuModel_HvVendor", Initial = V("hv-vendor-id"),
                Trim = true, Advanced = true },
            new FormField { Key = "level", LabelKey = "CpuModel_Level", Initial = V("level"), Trim = true,
                Advanced = true }
        ]);

        return await SubmitAsync(owner, row is null ? Loc.T("CpuModel_AddTitle") : Loc.T("CpuModel_EditTitle",
                row["cputype"]), fields, values => row is null
                ? api.Cluster.CreateCpuModelAsync(NonEmpty(values))
                : api.Cluster.UpdateCpuModelAsync(row["cputype"], UpdateForm(values)),
            row is null ? "CpuModel_Added" : "CpuModel_Updated", titleIsKey: false);
    }
}
