using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     데이터센터 접근 제어·풀 화면의 추가/수정/삭제 버튼. 권한 검사는 서버가 다시 하므로
///     여기서는 권한이 없을 게 뻔한 버튼만 숨긴다.
/// </summary>
internal static class AccessActions
{
    public static IReadOnlyList<TableAction> Groups(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitAsync(owner, "DcGroups_AddTitle",
                [
                    new FormField { Key = "groupid", LabelKey = "Table_Name", Required = true },
                    new FormField { Key = "comment", LabelKey = "Table_Comment" }
                ], values => api.Access.CreateGroupAsync(NonEmpty(values)), "DcGroups_Added")
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcGroups_EditTitle", row!["groupid"]),
                [
                    new FormField { Key = "comment", LabelKey = "Table_Comment",
                        Initial = row.TryGetValue("comment", out var c) ? c : "" }
                ], values => api.Access.UpdateGroupAsync(row["groupid"], values), // delete 없음
                    "DcGroups_Updated", titleIsKey: false)
            },
            DeleteAction(row => Loc.T("DcGroups_DeleteConfirm", row["groupid"]),
                row => api.Access.DeleteGroupAsync(row["groupid"]), "DcGroups_Deleted")
        ];
    }

    public static IReadOnlyList<TableAction> Pools(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitAsync(owner, "DcPools_AddTitle",
                [
                    new FormField { Key = "poolid", LabelKey = "Table_Name", Required = true },
                    new FormField { Key = "comment", LabelKey = "Table_Comment" }
                ], values => api.Pools.CreateAsync(NonEmpty(values)), "DcPools_Added")
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                // 설명만 바꾼다 — 구성원 추가·삭제와 같은 수정 요청(서버 버전에 맞는 경로는 Core 가 고른다)
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcPools_EditTitle", row!["poolid"]),
                [
                    new FormField { Key = "comment", LabelKey = "Table_Comment",
                        Initial = row.TryGetValue("comment", out var c) ? c : "" }
                ], values => api.Pools.UpdateAsync(row["poolid"],
                    new Dictionary<string, string> { ["comment"] = values["comment"] }), "DcPools_Updated",
                    titleIsKey: false)
            },
            DeleteAction(row => Loc.T("DcPools_DeleteConfirm", row["poolid"]),
                row => api.Pools.DeleteAsync(row["poolid"]), "DcPools_Deleted"),
            new TableAction
            {
                LabelKey = "DcPools_Members", IconKey = "IconList", NeedsSelection = true,
                Run = (row, owner) => Task.FromResult(TableWindow.ShowModal(owner,
                    Loc.T("DcPools_MembersTitle", row!["poolid"]), PoolMembers.Create(api, row["poolid"])))
            }
        ];
    }

    /// <param name="fixedPath">주면 그 경로의 권한만 다룬다(게스트 창의 권한 탭 — /vms/{vmid}).</param>
    public static IReadOnlyList<TableAction> Acl(ProxmoxApiClient api, string? fixedPath = null)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddAclAsync(api, owner, fixedPath)
            },
            DeleteAction(row => Loc.T("DcAcl_DeleteConfirm", row["path"], row["ugid"], row["roleid"]),
                row => api.Access.UpdateAclAsync(new Dictionary<string, string>
                {
                    ["path"] = row["path"],
                    ["roles"] = row["roleid"],
                    [AclTargetKey(row["type"])] = row["ugid"],
                    ["delete"] = "1"
                }), "DcAcl_Deleted")
        ];
    }

    /// <summary>
    ///     권한 추가(PUT access/acl) — 웹 UI 처럼 사용자·그룹·API 토큰 중 고르고(목록), 경로도 목록에서 고른다.
    ///     역할은 NoAccess 로 시작한다(첫 항목 Administrator 가 실수로 부여되지 않게).
    /// </summary>
    private static async Task<string?> AddAclAsync(ProxmoxApiClient api, Window? owner, string? fixedPath)
    {
        var roles = (await api.Access.ListRolesAsync())
            .Select(r => (r["roleid"], r["roleid"]))
            .OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var principals = await AclChoices.PrincipalsAsync(api);

        var fields = new List<FormField>();
        if (fixedPath is null)
            fields.Add(new FormField
            {
                Key = "path", LabelKey = "Table_Path", Kind = FormFieldKind.Choice, Initial = "/",
                Choices = await AclChoices.PathsAsync(api)
            });

        return await SubmitAsync(owner, "DcAcl_AddTitle",
        [
            ..fields,
            new FormField
            {
                Key = "who", LabelKey = "Table_UserOrGroup", Kind = FormFieldKind.Choice, Choices = principals,
                Required = true
            },
            new FormField
            {
                Key = "roles", LabelKey = "Table_Role", Kind = FormFieldKind.Choice, Choices = roles,
                Initial = roles.Any(r => r.Item1 == "NoAccess") ? "NoAccess" : ""
            },
            new FormField { Key = "propagate", LabelKey = "Table_Propagate", Kind = FormFieldKind.Bool, Initial = "1" }
        ], values =>
        {
            var (type, id) = AclChoices.Split(values["who"]);
            return api.Access.UpdateAclAsync(new Dictionary<string, string>
            {
                ["path"] = fixedPath ?? values["path"],
                ["roles"] = values["roles"],
                [AclTargetKey(type)] = id,
                ["propagate"] = values["propagate"]
            });
        }, "DcAcl_Added", validate: values => values["who"].Length == 0 ? Loc.T("DcAcl_PickWho") : null);
    }

    private static string AclTargetKey(string type)
    {
        return type switch
        {
            "group" => "groups",
            "token" => "tokens",
            _ => "users"
        };
    }
}
