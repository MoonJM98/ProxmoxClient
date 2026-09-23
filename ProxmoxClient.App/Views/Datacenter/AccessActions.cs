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
    public static IReadOnlyList<TableAction> Users(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddUserAsync(api, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditUserAsync(api, row!, owner)
            },
            new TableAction
            {
                LabelKey = "DcUsers_Password", IconKey = "IconKeyboard", NeedsSelection = true,
                Run = (row, owner) => ChangePasswordAsync(api, row!["userid"], owner)
            },
            DeleteAction(row => Loc.T("DcUsers_DeleteConfirm", row["userid"]),
                row => api.DeleteActionAsync($"access/users/{Seg(row["userid"])}"), "DcUsers_Deleted"),
            AccessExtras.TokensAction(api)
        ];
    }

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
                ], values => api.PostActionAsync("access/groups", NonEmpty(values)), "DcGroups_Added")
            },
            DeleteAction(row => Loc.T("DcGroups_DeleteConfirm", row["groupid"]),
                row => api.DeleteActionAsync($"access/groups/{Seg(row["groupid"])}"), "DcGroups_Deleted")
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
                ], values => api.PostActionAsync("pools", NonEmpty(values)), "DcPools_Added")
            },
            DeleteAction(row => Loc.T("DcPools_DeleteConfirm", row["poolid"]),
                row => api.DeleteActionAsync($"pools/{Seg(row["poolid"])}"), "DcPools_Deleted"),
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
                row => api.PutActionAsync("access/acl", new Dictionary<string, string>
                {
                    ["path"] = row["path"],
                    ["roles"] = row["roleid"],
                    [AclTargetKey(row["type"])] = row["ugid"],
                    ["delete"] = "1"
                }), "DcAcl_Deleted")
        ];
    }

    private static async Task<string?> AddUserAsync(ProxmoxApiClient api, Window? owner)
    {
        var realms = (await api.GetTableAsync("access/domains"))
            .Select(r => (r["realm"], r["realm"]))
            .ToList();

        return await SubmitAsync(owner, "DcUsers_AddTitle",
        [
            new FormField { Key = "name", LabelKey = "DcUsers_UserName", Required = true },
            new FormField
            {
                Key = "realm", LabelKey = "DcTab_Realms", Kind = FormFieldKind.Choice, Choices = realms,
                Initial = "pve"
            },
            new FormField { Key = "password", LabelKey = "DcUsers_InitialPassword", Kind = FormFieldKind.Password },
            new FormField { Key = "firstname", LabelKey = "Table_FirstName" },
            new FormField { Key = "lastname", LabelKey = "Table_LastName" },
            new FormField { Key = "email", LabelKey = "Table_Email" },
            new FormField { Key = "groups", LabelKey = "DcTab_Groups" },
            new FormField { Key = "comment", LabelKey = "Table_Comment" },
            new FormField { Key = "enable", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool, Initial = "1" }
        ], values =>
        {
            var form = NonEmpty(values);
            form.Remove("name");
            form.Remove("realm");
            form["userid"] = $"{values["name"]}@{values["realm"]}";
            form["enable"] = values["enable"];
            return api.PostActionAsync("access/users", form);
        }, "DcUsers_Added");
    }

    private static Task<string?> EditUserAsync(ProxmoxApiClient api, IReadOnlyDictionary<string, string> row,
        Window? owner)
    {
        string Value(string key) => row.TryGetValue(key, out var v) ? v : string.Empty;

        return SubmitAsync(owner, Loc.T("DcUsers_EditTitle", Value("userid")),
        [
            new FormField { Key = "firstname", LabelKey = "Table_FirstName", Initial = Value("firstname") },
            new FormField { Key = "lastname", LabelKey = "Table_LastName", Initial = Value("lastname") },
            new FormField { Key = "email", LabelKey = "Table_Email", Initial = Value("email") },
            new FormField { Key = "groups", LabelKey = "DcTab_Groups", Initial = Value("groups").Replace(" ", "") },
            new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Value("comment") },
            new FormField
            {
                Key = "enable", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
                Initial = Value("enable") is "0" ? "0" : "1"
            }
        ], values => api.PutActionAsync($"access/users/{Seg(Value("userid"))}", values), "DcUsers_Updated",
            titleIsKey: false);
    }

    private static Task<string?> ChangePasswordAsync(ProxmoxApiClient api, string userId, Window? owner)
    {
        return SubmitAsync(owner, Loc.T("DcUsers_PasswordTitle", userId),
        [
            new FormField
            {
                Key = "password", LabelKey = "DcUsers_NewPassword", Kind = FormFieldKind.Password, Required = true
            },
            new FormField
            {
                Key = "confirmation-password", LabelKey = "DcUsers_MyPassword", Kind = FormFieldKind.Password
            }
        ], values =>
        {
            var form = NonEmpty(values);
            form["userid"] = userId;
            return api.PutActionAsync("access/password", form);
        }, "DcUsers_PasswordChanged", titleIsKey: false);
    }

    private static async Task<string?> AddAclAsync(ProxmoxApiClient api, Window? owner, string? fixedPath)
    {
        var roles = (await api.GetTableAsync("access/roles"))
            .Select(r => (r["roleid"], r["roleid"]))
            .OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fields = new List<FormField>();
        if (fixedPath is null)
            fields.Add(new FormField { Key = "path", LabelKey = "Table_Path", Required = true, Initial = "/" });

        return await SubmitAsync(owner, "DcAcl_AddTitle",
        [
            ..fields,
            new FormField
            {
                Key = "type", LabelKey = "Table_Type", Kind = FormFieldKind.Choice, Initial = "user",
                Choices = [("user", "DcAcl_TypeUser"), ("group", "DcAcl_TypeGroup")]
            },
            new FormField { Key = "ugid", LabelKey = "Table_UserOrGroup", Required = true },
            new FormField { Key = "roles", LabelKey = "Table_Role", Kind = FormFieldKind.Choice, Choices = roles },
            new FormField { Key = "propagate", LabelKey = "Table_Propagate", Kind = FormFieldKind.Bool, Initial = "1" }
        ], values => api.PutActionAsync("access/acl", new Dictionary<string, string>
        {
            ["path"] = fixedPath ?? values["path"],
            ["roles"] = values["roles"],
            [AclTargetKey(values["type"])] = values["ugid"],
            ["propagate"] = values["propagate"]
        }), "DcAcl_Added");
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
