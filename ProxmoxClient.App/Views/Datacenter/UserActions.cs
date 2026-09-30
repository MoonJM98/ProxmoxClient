using System.Globalization;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     사용자(로그인 계정) 추가·수정·암호 — 웹 UI dc/UserEdit.js·PasswordEdit 와 같은 칸과 규칙.
///     암호는 PVE 영역에서만 쓰며 8자 이상 + 확인 칸. 그룹은 목록에서 고르고, 만료일은 날짜(비우면 만료 없음).
///     수정할 때는 서버에서 사용자를 다시 읽어 그룹을 채우고, 바뀐 경우에만 groups 를 보낸다
///     (groups 를 보내면 서버는 그 목록으로 통째로 바꾸므로 빈 값이 가면 모든 그룹에서 빠진다).
/// </summary>
internal static class UserActions
{
    public const int MinPasswordLength = 8;
    private const string DateFormat = "yyyy-MM-dd";

    /// <param name="canEdit">User.Modify — 없으면 자기 암호 변경과 자기 API 토큰만(웹 UI 도 누구에게나 보인다).</param>
    public static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api, bool canEdit)
    {
        var self = new List<TableAction>
        {
            new()
            {
                LabelKey = "DcUsers_Password", IconKey = "IconKeyboard", NeedsSelection = true,
                Run = (row, owner) => ChangePasswordAsync(api, row!["userid"], owner)
            },
            AccessExtras.TokensAction(api)
        };
        if (!canEdit) return self;

        return
        [
            new TableAction { LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddAsync(api, owner) },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditAsync(api, row!["userid"], owner)
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("DcUsers_DeleteConfirm", row!["userid"]),
                Run = async (row, _) =>
                {
                    // root@pam 은 서버가 지울 수 없게 막는다 — 요청 전에 알려 준다
                    if (row!["userid"] == "root@pam") return Loc.T("DcUsers_RootProtected");
                    await api.Users.DeleteAsync(row["userid"]);
                    return Loc.T("DcUsers_Deleted");
                }
            },
            ..self,
            UnlockTfaAction(api),
            PermissionsAction(api, "userid")
        ];
    }

    /// <summary>
    ///     실제 권한 보기(access/permissions?userid=) — 경로별 권한 목록. 토큰 창에서도 쓴다(<paramref name="idKey" />).
    /// </summary>
    public static TableAction PermissionsAction(ProxmoxApiClient api, string idKey, string? userPrefix = null)
    {
        return new TableAction
        {
            LabelKey = "DcUsers_Permissions", IconKey = "IconCheck", NeedsSelection = true,
            Run = (row, owner) =>
            {
                var id = userPrefix is null ? row![idKey] : $"{userPrefix}!{row![idKey]}";
                return Task.FromResult(TableWindow.ShowModal(owner, Loc.T("DcUsers_PermissionsTitle", id),
                    new TableTab(() => PermissionRowsAsync(api, id), PermissionColumns, "DcUsers_PermissionsHint")));
            }
        };
    }

    private static readonly IReadOnlyList<TableColumn> PermissionColumns =
    [
        new() { Key = "path", HeaderKey = "Table_Path", Width = 200 },
        new() { Key = "privs", HeaderKey = "DcRoles_Privs", Width = 0 }
    ];

    /// <summary>2단계 인증 잠금 풀기(PVE 8.0+ — 낮은 서버엔 버튼이 없다).</summary>
    private static TableAction UnlockTfaAction(ProxmoxApiClient api)
    {
        return new TableAction
        {
            LabelKey = "DcUsers_UnlockTfa", IconKey = "IconShield", NeedsSelection = true,
            Requires = api.Users.Feature(nameof(UsersApi.UnlockTfaAsync)),
            Confirm = row => Loc.T("DcUsers_UnlockTfaConfirm", row!["userid"]),
            Run = async (row, _) =>
            {
                await api.Users.UnlockTfaAsync(row!["userid"]);
                return Loc.T("DcUsers_TfaUnlocked", row["userid"]);
            }
        };
    }

    /// <summary>응답은 {경로: {권한: 1|0}} — 경로마다 한 줄, 권한은 이름순(전파 안 되는 권한은 * 표시 없음).</summary>
    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> PermissionRowsAsync(
        ProxmoxApiClient api, string id)
    {
        var map = await api.Users.PermissionsAsync(id);
        return map.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["path"] = kv.Key, ["privs"] = PrivilegeList(kv.Value)
            }).ToList();
    }

    /// <summary>{"VM.Audit":1,"Sys.Audit":1} → "Sys.Audit, VM.Audit".</summary>
    private static string PrivilegeList(string json)
    {
        var props = Core.Models.PropertyString.Parse(Core.Models.PropertyString.FromJsonObject(json));
        return string.Join(", ", props.Items.Select(i => i.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------ 추가

    private static async Task<string?> AddAsync(ProxmoxApiClient api, Window? owner)
    {
        var realms = (await api.Realms.ListAsync()).Select(r => (r["realm"], r["realm"])).ToList();
        var groups = await GroupChoicesAsync(api);

        return await SubmitAsync(owner, "DcUsers_AddTitle",
        [
            new FormField { Key = "name", LabelKey = "DcUsers_UserName", Required = true, Trim = true },
            new FormField
            {
                Key = "realm", LabelKey = "DcTab_Realms", Kind = FormFieldKind.Choice, Choices = realms,
                Initial = realms.Any(r => r.Item1 == "pve") ? "pve" : realms.FirstOrDefault().Item1 ?? ""
            },
            new FormField { Key = "password", LabelKey = "DcUsers_InitialPassword", Kind = FormFieldKind.Password,
                Hint = Loc.T("DcUsers_PasswordPveOnly", MinPasswordLength) },
            new FormField { Key = "confirm", LabelKey = "DcUsers_ConfirmPassword", Kind = FormFieldKind.Password },
            new FormField { Key = "groups", LabelKey = "DcTab_Groups", Kind = FormFieldKind.MultiChoice,
                Choices = groups },
            ExpireField(""),
            new FormField { Key = "enable", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool, Initial = "1" },
            new FormField { Key = "comment", LabelKey = "Table_Comment" },
            new FormField { Key = "firstname", LabelKey = "Table_FirstName" },
            new FormField { Key = "lastname", LabelKey = "Table_LastName" },
            new FormField { Key = "email", LabelKey = "Table_Email", Trim = true },
            new FormField { Key = "keys", LabelKey = "DcUsers_KeyIds", Advanced = true, Trim = true,
                Hint = Loc.T("DcUsers_KeyIdsHint") }
        ], values =>
        {
            var form = NonEmpty(values);
            foreach (var key in new[] { "name", "realm", "password", "confirm", "expire" }) form.Remove(key);
            form["userid"] = $"{values["name"].Trim()}@{values["realm"]}";
            form["enable"] = values["enable"];
            // 암호는 PVE 영역만 — PAM·LDAP 등은 그 인증 시스템의 암호를 쓴다
            if (values["realm"] == "pve" && values["password"].Length > 0) form["password"] = values["password"];
            if (ExpireEpoch(values["expire"]) is { } expire && expire != "0") form["expire"] = expire;
            return api.Users.CreateAsync(form);
        }, "DcUsers_Added", validate: values => ValidateNew(values));
    }

    private static string? ValidateNew(IReadOnlyDictionary<string, string> values)
    {
        if (values["name"].Trim().Contains('@')) return Loc.T("DcUsers_NameNoRealm");
        if (values["realm"] == "pve" && PasswordProblem(values["password"], values["confirm"]) is { } problem)
            return problem;
        return ValidateCommon(values);
    }

    private static string? ValidateCommon(IReadOnlyDictionary<string, string> values)
    {
        if (ExpireEpoch(values["expire"]) is null) return Loc.T("DcUsers_BadExpire", DateFormat);
        return values["email"].Trim() is { Length: > 0 } email && !email.Contains('@') ? Loc.T("DcOpt_BadEmail") : null;
    }

    /// <summary>웹 UI 와 같이 8자 이상 + 확인 칸 일치.</summary>
    public static string? PasswordProblem(string password, string confirm)
    {
        if (password.Length < MinPasswordLength) return Loc.T("DcUsers_PasswordShort", MinPasswordLength);
        return password == confirm ? null : Loc.T("Ct_PasswordMismatch");
    }

    // ------------------------------------------------------------ 수정

    private static async Task<string?> EditAsync(ProxmoxApiClient api, string userId, Window? owner)
    {
        // 목록 행에는 그룹·키가 없을 수 있으므로(서버 버전·full 여부) 사용자 한 명을 다시 읽는다
        var user = await api.Users.GetAsync(userId);
        string Value(string key) => user.TryGetValue(key, out var v) ? v : string.Empty;
        var currentGroups = NormalizeList(Value("groups"));
        var groups = await GroupChoicesAsync(api);

        return await SubmitAsync(owner, Loc.T("DcUsers_EditTitle", userId),
        [
            new FormField { Key = "groups", LabelKey = "DcTab_Groups", Kind = FormFieldKind.MultiChoice,
                Choices = groups, Initial = currentGroups },
            ExpireField(Value("expire")),
            new FormField { Key = "enable", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
                Initial = Value("enable") is "0" ? "0" : "1" },
            new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Value("comment") },
            new FormField { Key = "firstname", LabelKey = "Table_FirstName", Initial = Value("firstname") },
            new FormField { Key = "lastname", LabelKey = "Table_LastName", Initial = Value("lastname") },
            new FormField { Key = "email", LabelKey = "Table_Email", Initial = Value("email"), Trim = true },
            new FormField { Key = "keys", LabelKey = "DcUsers_KeyIds", Advanced = true, Trim = true,
                Initial = Value("keys"), Hint = Loc.T("DcUsers_KeyIdsHint") }
        ], values =>
        {
            var form = values.Where(kv => kv.Key is not ("groups" or "expire"))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            if (NormalizeList(values["groups"]) != currentGroups) form["groups"] = NormalizeList(values["groups"]);
            form["expire"] = ExpireEpoch(values["expire"]) ?? "0";
            // 이 API 에는 delete 가 없다 — 비운 칸은 빈 문자열로 보내 지운다(웹 UI 와 같다)
            return api.Users.UpdateAsync(userId, form);
        }, "DcUsers_Updated", titleIsKey: false, validate: ValidateCommon);
    }

    /// <summary>"a, b" 또는 "a,b" → "a,b"(이름순) — 바뀌었는지 비교용.</summary>
    private static string NormalizeList(string text)
    {
        return string.Join(',', text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(g => g, StringComparer.Ordinal));
    }

    private static async Task<IReadOnlyList<(string, string)>> GroupChoicesAsync(ProxmoxApiClient api)
    {
        return (await api.Access.ListGroupsAsync()).Select(r => r["groupid"])
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase).Select(g => (g, g)).ToList();
    }

    // ------------------------------------------------------------ 만료일

    private static FormField ExpireField(string epoch)
    {
        var initial = long.TryParse(epoch, out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime
                .ToString(DateFormat, CultureInfo.InvariantCulture)
            : string.Empty;
        return new FormField { Key = "expire", LabelKey = "Table_Expires", Initial = initial, Trim = true,
            Hint = Loc.T("DcUsers_ExpireHint", DateFormat) };
    }

    /// <summary>"2026-12-31" → 그날 0시(현지)의 epoch 초, 빈칸 → "0"(만료 없음), 형식 오류 → null.</summary>
    internal static string? ExpireEpoch(string text)
    {
        if (text.Trim().Length == 0) return "0";
        return DateTime.TryParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var date)
            ? new DateTimeOffset(date).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
            : null;
    }

    // ------------------------------------------------------------ 암호

    /// <summary>
    ///     암호 변경(PUT access/password) — PAM·PVE 영역만. root@pam 이 아니면 내 암호(confirmation-password)가
    ///     필요하다(서버 규칙). 새 암호는 8자 이상 + 확인.
    /// </summary>
    private static async Task<string?> ChangePasswordAsync(ProxmoxApiClient api, string userId, Window? owner)
    {
        var realm = userId[(userId.LastIndexOf('@') + 1)..];
        if (realm is not ("pam" or "pve")) return Loc.T("DcUsers_PasswordRealmOnly", realm);

        var isRoot = string.Equals(api.Profile.UserName, "root@pam", StringComparison.Ordinal);
        // 내 암호 확인(confirmation-password)은 8.1+ — 낮은 서버엔 입력 창이 이 칸을 빼 준다(target)
        return await SubmitAsync(owner, Loc.T("DcUsers_PasswordTitle", userId),
        [
            new FormField { Key = "confirmation-password", LabelKey = "DcUsers_MyPassword",
                Kind = FormFieldKind.Password, Required = !isRoot,
                Hint = Loc.T("DcUsers_MyPasswordHint", api.Profile.UserName) },
            new FormField { Key = "password", LabelKey = "DcUsers_NewPassword", Kind = FormFieldKind.Password,
                Required = true },
            new FormField { Key = "confirm", LabelKey = "DcUsers_ConfirmPassword", Kind = FormFieldKind.Password,
                Required = true }
        ], async values =>
        {
            var form = NonEmpty(values);
            form.Remove("confirm");
            return await api.Users.ChangePasswordAsync(userId, form);
        }, "DcUsers_PasswordChanged", titleIsKey: false,
            validate: values => PasswordProblem(values["password"], values["confirm"]),
            target: api.Users.Feature(nameof(UsersApi.ChangePasswordAsync)));
    }
}
