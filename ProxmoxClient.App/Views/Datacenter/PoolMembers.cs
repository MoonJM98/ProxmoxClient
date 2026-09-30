using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>풀 구성원(게스트·저장소) — 풀 단위 권한은 여기 넣은 구성원에게 적용된다.</summary>
internal static class PoolMembers
{
    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "id", HeaderKey = "Table_Id", Width = 160 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 80 },
        new() { Key = "node", HeaderKey = "Table_Node", Width = 100 },
        new() { Key = "name", HeaderKey = "Table_Name", Width = 0 }
    ];

    /// <param name="canEdit">false 면 구성원 목록만 보인다(Pool.Allocate 가 없는 사용자).</param>
    public static TableTab Create(ProxmoxApiClient api, string pool, bool canEdit = true)
    {
        return new TableTab(() => api.Pools.MembersAsync(pool), Columns, "DcPools_MembersHint",
            canEdit ? Actions(api, pool) : null);
    }

    private static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api, string pool)
    {
        return
        [
            new TableAction
            {
                LabelKey = "DcPools_AddGuests", IconKey = "IconPlus",
                Run = async (_, owner) =>
                {
                    var guests = (await api.GetClusterResourcesAsync())
                        .Where(r => r.Kind is ResourceKind.Qemu or ResourceKind.Lxc)
                        .OrderBy(r => r.VmId)
                        .Select(r => (r.VmId.ToString(), $"{r.VmId}  {r.Name}"))
                        .ToList();
                    return await SubmitAsync(owner, "DcPools_AddGuests",
                    [
                        new FormField
                        {
                            Key = "vms", LabelKey = "NodePower_Guests", Kind = FormFieldKind.MultiChoice,
                            Choices = guests, Required = true
                        },
                        // 다른 풀에서 옮겨 오기(allow-move)는 8.1+ — 낮은 서버엔 입력 창이 빼 준다
                        new FormField { Key = "allow-move", LabelKey = "DcPools_AllowMove", Kind = FormFieldKind.Bool }
                    ], values => api.Pools.UpdateAsync(pool, values), "DcPools_MembersUpdated",
                        target: api.Pools.Feature(nameof(PoolsApi.UpdateAsync)));
                }
            },
            new TableAction
            {
                LabelKey = "DcPools_AddStorage", IconKey = "IconDatabase",
                Run = async (_, owner) =>
                {
                    var storages = (await api.Storage.ListAsync())
                        .Select(s => (Value(s, "storage"), Value(s, "storage")))
                        .ToList();
                    return await SubmitAsync(owner, "DcPools_AddStorage",
                    [
                        new FormField
                        {
                            Key = "storage", LabelKey = "Table_Storage", Kind = FormFieldKind.MultiChoice,
                            Choices = storages, Required = true
                        }
                    ], values => api.Pools.UpdateAsync(pool, values), "DcPools_MembersUpdated");
                }
            },
            new TableAction
            {
                LabelKey = "DcPools_RemoveMember", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("DcPools_RemoveMemberConfirm", row!["id"]),
                Run = async (row, _) =>
                {
                    var member = row!;
                    var form = new Dictionary<string, string> { ["delete"] = "1" };
                    if (Value(member, "type") == "storage") form["storage"] = Value(member, "storage");
                    else form["vms"] = Value(member, "vmid");
                    await api.Pools.UpdateAsync(pool, form);
                    return Loc.T("DcPools_MembersUpdated");
                }
            }
        ];
    }
}
