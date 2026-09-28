using System.Net.Http;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>접근 제어 부가 기능 — 사용자별 API 토큰, 2단계 인증 목록, 사용자 정의 역할(인증 영역은 RealmActions).</summary>
internal static class AccessExtras
{
    private static readonly IReadOnlyList<TableColumn> TokenColumns =
    [
        new() { Key = "tokenid", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "privsep", HeaderKey = "DcTokens_PrivSep", Width = 90, Format = TableFormats.Flag },
        new() { Key = "expire", HeaderKey = "Table_Expires", Width = 140, Format = FormatExpire },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    public static IReadOnlyList<TableColumn> TfaColumns { get; } =
    [
        new() { Key = "userid", HeaderKey = "Table_UserId", Width = 150 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "description", HeaderKey = "Table_Description", Width = 0 },
        // enable 이 없으면 켜진 상태(서버 기본값)
        new() { Key = "enable", HeaderKey = "Table_Enabled", Width = 60, Format = v => v is "0" or "false" ? "" : "✓" },
        new() { Key = "created", HeaderKey = "DcTfa_Created", Width = 140, Format = TableFormats.EpochDate }
    ];

    // ------------------------------------------------------------ API 토큰

    /// <summary>사용자 탭의 'API 토큰' 버튼 — 고른 사용자의 토큰을 따로 띄운 창에서 관리한다.</summary>
    public static TableAction TokensAction(ProxmoxApiClient api)
    {
        return new TableAction
        {
            LabelKey = "DcTokens_Button", IconKey = "IconKeyboard", NeedsSelection = true,
            Run = (row, owner) =>
            {
                var user = row!["userid"];
                return Task.FromResult(TableWindow.ShowModal(owner, Loc.T("DcTokens_Title", user),
                    new TableTab(() => api.Users.ListTokensAsync(user), TokenColumns, "DcTokens_Hint",
                        TokenActions(api, user))));
            }
        };
    }

    /// <summary>토큰 만료일 — 사용자와 같은 날짜 형식(비우면 만료 없음).</summary>
    private static FormField TokenExpireField(string epoch)
    {
        var initial = long.TryParse(epoch, out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        return new FormField { Key = "expire", LabelKey = "Table_Expires", Initial = initial, Trim = true,
            Hint = Loc.T("DcUsers_ExpireHint", "yyyy-MM-dd") };
    }

    private static string? ValidateExpire(IReadOnlyDictionary<string, string> values)
    {
        return UserActions.ExpireEpoch(values["expire"]) is null ? Loc.T("DcUsers_BadExpire", "yyyy-MM-dd") : null;
    }

    private static IReadOnlyList<TableAction> TokenActions(ProxmoxApiClient api, string user)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = async (_, owner) =>
                {
                    var dialog = new FormDialog(Loc.T("DcTokens_AddTitle", user),
                    [
                        new FormField { Key = "tokenid", LabelKey = "Table_Name", Required = true },
                        new FormField
                        {
                            Key = "privsep", LabelKey = "DcTokens_PrivSep", Kind = FormFieldKind.Bool, Initial = "1"
                        },
                        TokenExpireField(""),
                        new FormField { Key = "comment", LabelKey = "Table_Comment" }
                    ], ValidateExpire) { Owner = owner };
                    if (dialog.ShowDialog() != true || dialog.Result is not { } values) return null;

                    var form = NonEmpty(values);
                    form.Remove("tokenid");
                    form.Remove("expire");
                    form["privsep"] = values["privsep"];
                    if (UserActions.ExpireEpoch(values["expire"]) is { } expire and not "0") form["expire"] = expire;
                    var created = await api.Users.CreateTokenAsync(user, values["tokenid"], form);

                    // 비밀 값은 지금 한 번만 받을 수 있다 — 복사할 수 있게 글 창으로 보여 준다
                    TextViewWindow.ShowModal(owner, Loc.T("DcTokens_SecretTitle"),
                        Loc.T("DcTokens_Secret", Value(created, "full-tokenid"), Value(created, "value")));
                    return Loc.T("DcTokens_Added");
                }
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcTokens_EditTitle", row!["tokenid"]),
                [
                    new FormField
                    {
                        Key = "privsep", LabelKey = "DcTokens_PrivSep", Kind = FormFieldKind.Bool,
                        Initial = Value(row, "privsep") is "0" ? "0" : "1"
                    },
                    TokenExpireField(Value(row, "expire")),
                    new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Value(row, "comment") }
                ], values =>
                {
                    var form = values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                    form["expire"] = UserActions.ExpireEpoch(values["expire"]) ?? "0";
                    // 9.0 전 서버는 delete 를 몰라 Core 가 빈 값·0 으로 바꿔 보낸다
                    return api.Users.UpdateTokenAsync(user, row["tokenid"], UpdateForm(form));
                }, "DcTokens_Updated", titleIsKey: false, validate: ValidateExpire)
            },
            RegenerateAction(api, user),
            UserActions.PermissionsAction(api, "tokenid", user),
            DeleteAction(row => Loc.T("DcTokens_DeleteConfirm", row["tokenid"]),
                row => api.Users.DeleteTokenAsync(user, row["tokenid"]), "DcTokens_Deleted")
        ];
    }

    /// <summary>
    ///     같은 토큰 ID·권한을 둔 채 비밀 값만 바꾼다(PVE 9.1+ — 낮은 서버엔 버튼이 없다).
    ///     이전 비밀을 쓰던 곳은 바로 막힌다.
    /// </summary>
    private static TableAction RegenerateAction(ProxmoxApiClient api, string user)
    {
        return new TableAction
        {
            LabelKey = "DcTokens_Regenerate", IconKey = "IconRefresh", NeedsSelection = true,
            Requires = api.Users.Feature(nameof(UsersApi.RegenerateTokenAsync), "regenerate"),
            Confirm = row => Loc.T("DcTokens_RegenerateConfirm", row!["tokenid"]),
            Run = async (row, owner) =>
            {
                var secret = await api.Users.RegenerateTokenAsync(user, row!["tokenid"]);
                if (secret.Length == 0) return Loc.T("DcTokens_RegenerateUnsupported");
                TextViewWindow.ShowModal(owner, Loc.T("DcTokens_SecretTitle"),
                    Loc.T("DcTokens_Secret", $"{user}!{row["tokenid"]}", secret));
                return Loc.T("DcTokens_Regenerated");
            }
        };
    }

    // ------------------------------------------------------------ 2단계 인증

    /// <summary>등록된 2단계 인증 항목 — 켜기/끄기와 삭제(새 항목 등록은 <see cref="TfaRegistration" />).</summary>
    public static IReadOnlyList<TableAction> TfaActions(ProxmoxApiClient api)
    {
        FormField Password() => new()
        {
            Key = "password", LabelKey = "DcUsers_MyPassword", Kind = FormFieldKind.Password
        };

        return
        [
            // 수정 — 설명과 사용 여부(웹 UI 의 TFA 편집 창). 서버는 root@pam 이 아니면 내 암호를 요구한다
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcTfa_ToggleTitle", row!["userid"], row["id"]),
                [
                    new FormField { Key = "description", LabelKey = "Table_Description",
                        Initial = Value(row, "description") },
                    new FormField { Key = "enable", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
                        Initial = Value(row, "enable") is "0" or "false" ? "0" : "1" },
                    Password()
                ], values =>
                {
                    var form = new Dictionary<string, string>(values);
                    if (form["password"].Length == 0) form.Remove("password");
                    return api.Tfa.UpdateAsync(row["userid"], row["id"], form);
                }, "DcTfa_Toggled", titleIsKey: false)
            },
            // 서버는 root@pam 이 아니면 내 암호를 요구한다 — DELETE 는 본문이 없어 웹 UI 처럼 주소(HTTPS)에 싣는다
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcTfa_DeleteConfirm", row!["userid"], row["id"]),
                    [Password()], values => api.Tfa.DeleteAsync(row["userid"], row["id"], values["password"]),
                    "DcTfa_Deleted", titleIsKey: false)
            }
        ];
    }

    // ------------------------------------------------------------ 역할

    public static IReadOnlyList<TableAction> RoleActions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => EditRoleAsync(api, null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditRoleAsync(api, row, owner)
            },
            DeleteAction(row => Loc.T("DcRoles_DeleteConfirm", row["roleid"]),
                row => api.Access.DeleteRoleAsync(row["roleid"]), "DcRoles_Deleted")
        ];
    }

    /// <summary>역할 추가/편집 — 권한은 Administrator 역할이 가진 전체 목록에서 고른다. 기본 제공 역할은 고칠 수 없다.</summary>
    private static async Task<string?> EditRoleAsync(ProxmoxApiClient api, IReadOnlyDictionary<string, string>? row,
        Window? owner)
    {
        if (row is not null && Value(row, "special") is "1" or "true") return Loc.T("DcRoles_BuiltInReadOnly");

        var allPrivileges = (await api.Access.GetRoleAsync("Administrator")).Keys
            .Order(StringComparer.Ordinal)
            .Select(p => (p, p))
            .ToList();

        var fields = new List<FormField>();
        if (row is null) fields.Add(new FormField { Key = "roleid", LabelKey = "Table_Name", Required = true });
        fields.Add(new FormField
        {
            Key = "privs", LabelKey = "Table_Privileges", Kind = FormFieldKind.MultiChoice, Choices = allPrivileges,
            Initial = row is null ? string.Empty : Value(row, "privs").Replace(" ", "")
        });

        var title = row is null ? Loc.T("DcRoles_AddTitle") : Loc.T("DcRoles_EditTitle", row["roleid"]);
        return await SubmitAsync(owner, title, fields, values => row is null
                ? api.Access.CreateRoleAsync(NonEmpty(values))
                : api.Access.UpdateRoleAsync(row["roleid"],
                    new Dictionary<string, string> { ["privs"] = values["privs"] }),
            row is null ? "DcRoles_Added" : "DcRoles_Updated", titleIsKey: false,
            validate: values => values["privs"].Length == 0 ? Loc.T("DcRoles_NeedPrivs") : null);
    }

    private static string FormatExpire(string raw)
    {
        return raw is "" or "0" ? string.Empty : TableFormats.EpochDate(raw);
    }
}
