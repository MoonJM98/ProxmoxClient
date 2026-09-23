using System.Net.Http;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>접근 제어 부가 기능 — 사용자별 API 토큰, 2단계 인증 목록, 사용자 정의 역할, 인증 영역 추가·동기화.</summary>
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
        new() { Key = "enable", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.Flag },
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
                var path = $"access/users/{Seg(user)}/token";
                return Task.FromResult(TableWindow.ShowModal(owner, Loc.T("DcTokens_Title", user),
                    new TableTab(() => api.GetTableAsync(path), TokenColumns, "DcTokens_Hint",
                        TokenActions(api, path, user))));
            }
        };
    }

    private static IReadOnlyList<TableAction> TokenActions(ProxmoxApiClient api, string path, string user)
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
                        new FormField { Key = "comment", LabelKey = "Table_Comment" }
                    ]) { Owner = owner };
                    if (dialog.ShowDialog() != true || dialog.Result is not { } values) return null;

                    var form = NonEmpty(values);
                    form.Remove("tokenid");
                    form["privsep"] = values["privsep"];
                    var created = await api.SendForObjectAsync(HttpMethod.Post,
                        $"{path}/{Seg(values["tokenid"])}", form);

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
                    new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Value(row, "comment") }
                ], values => api.PutActionAsync($"{path}/{Seg(row["tokenid"])}", UpdateForm(values)),
                    "DcTokens_Updated", titleIsKey: false)
            },
            DeleteAction(row => Loc.T("DcTokens_DeleteConfirm", row["tokenid"]),
                row => api.DeleteActionAsync($"{path}/{Seg(row["tokenid"])}"), "DcTokens_Deleted")
        ];
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
            new TableAction
            {
                LabelKey = "NodeApt_Toggle", IconKey = "IconSwitch", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcTfa_ToggleTitle", row!["userid"], row["id"]),
                    [Password()], values =>
                    {
                        var form = NonEmpty(values);
                        form["enable"] = Value(row, "enable") is "0" or "false" ? "1" : "0";
                        return api.PutActionAsync($"access/tfa/{Seg(row["userid"])}/{Seg(row["id"])}", form);
                    }, "DcTfa_Toggled", titleIsKey: false)
            },
            // 삭제 요청은 본문 없이 주소로만 보내므로 암호를 함께 보내지 않는다(주소는 로그에 남을 수 있다)
            DeleteAction(row => Loc.T("DcTfa_DeleteConfirm", row["userid"], row["id"]),
                row => api.DeleteActionAsync($"access/tfa/{Seg(row["userid"])}/{Seg(row["id"])}"),
                "DcTfa_Deleted")
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
                row => api.DeleteActionAsync($"access/roles/{Seg(row["roleid"])}"), "DcRoles_Deleted")
        ];
    }

    /// <summary>역할 추가/편집 — 권한은 Administrator 역할이 가진 전체 목록에서 고른다. 기본 제공 역할은 고칠 수 없다.</summary>
    private static async Task<string?> EditRoleAsync(ProxmoxApiClient api, IReadOnlyDictionary<string, string>? row,
        Window? owner)
    {
        if (row is not null && Value(row, "special") is "1" or "true") return Loc.T("DcRoles_BuiltInReadOnly");

        var allPrivileges = (await api.GetObjectAsync("access/roles/Administrator")).Keys
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
                ? api.PostActionAsync("access/roles", NonEmpty(values))
                : api.PutActionAsync($"access/roles/{Seg(row["roleid"])}",
                    new Dictionary<string, string> { ["privs"] = values["privs"] }),
            row is null ? "DcRoles_Added" : "DcRoles_Updated", titleIsKey: false);
    }

    // ------------------------------------------------------------ 인증 영역

    public static IReadOnlyList<TableAction> RealmActions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddRealmAsync(api, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcRealms_EditTitle", row!["realm"]),
                [
                    new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Value(row, "comment") },
                    new FormField
                    {
                        Key = "default", LabelKey = "DcRealms_Default", Kind = FormFieldKind.Bool,
                        Initial = Value(row, "default") is "1" ? "1" : "0"
                    }
                ], values => api.PutActionAsync($"access/domains/{Seg(row["realm"])}", UpdateForm(values)),
                    "DcRealms_Updated", titleIsKey: false)
            },
            new TableAction
            {
                LabelKey = "DcRealms_Sync", IconKey = "IconRefresh", NeedsSelection = true,
                Run = (row, owner) => SyncRealmAsync(api, row!, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("DcRealms_DeleteConfirm", row!["realm"]),
                Run = async (row, _) =>
                {
                    var realm = row!;
                    // pam·pve 는 서버가 지울 수 없게 막는다 — 요청 전에 알려 준다
                    if (Value(realm, "type") is "pam" or "pve") return Loc.T("DcRealms_BuiltInReadOnly");

                    await api.DeleteActionAsync($"access/domains/{Seg(realm["realm"])}");
                    return Loc.T("DcRealms_Deleted");
                }
            }
        ];
    }

    /// <summary>LDAP·AD 영역의 사용자·그룹을 서버와 맞춘다(OpenID 는 로그인할 때 만들어지므로 동기화가 없다).</summary>
    private static Task<string?> SyncRealmAsync(ProxmoxApiClient api, IReadOnlyDictionary<string, string> row,
        Window? owner)
    {
        if (Value(row, "type") is not ("ldap" or "ad"))
            return Task.FromResult<string?>(Loc.T("DcRealms_SyncUnsupported"));

        return SubmitTaskAsync(api, owner, Loc.T("DcRealms_SyncTitle", row["realm"]),
        [
            new FormField
            {
                Key = "scope", LabelKey = "DcRealms_SyncScope", Kind = FormFieldKind.Choice, Initial = "both",
                Choices = [("both", "DcRealms_SyncBoth"), ("users", "DcTab_Users"), ("groups", "DcTab_Groups")]
            },
            new FormField
            {
                Key = "enable-new", LabelKey = "DcRealms_EnableNew", Kind = FormFieldKind.Bool, Initial = "1"
            },
            new FormField { Key = "dry-run", LabelKey = "DcRealms_DryRun", Kind = FormFieldKind.Bool }
        ], values => api.PostActionAsync($"access/domains/{Seg(row["realm"])}/sync", values), "DcRealms_Synced");
    }

    /// <summary>인증 영역 추가 — 유형(LDAP·AD·OpenID)을 먼저 고른 뒤 그 유형에 필요한 칸만 받는다.</summary>
    private static async Task<string?> AddRealmAsync(ProxmoxApiClient api, Window? owner)
    {
        var choose = new FormDialog(Loc.T("DcRealms_AddTitle"),
        [
            new FormField
            {
                Key = "type", LabelKey = "Table_Type", Kind = FormFieldKind.Choice, Initial = "ldap",
                Choices = [("ldap", "LDAP"), ("ad", "Active Directory"), ("openid", "OpenID Connect")]
            }
        ]) { Owner = owner };
        if (choose.ShowDialog() != true || choose.Result is not { } picked) return null;

        var type = picked["type"];
        var fields = new List<FormField> { new() { Key = "realm", LabelKey = "Table_Name", Required = true } };
        fields.AddRange(type switch
        {
            "openid" =>
            [
                new FormField { Key = "issuer-url", LabelKey = "DcRealms_IssuerUrl", Required = true },
                new FormField { Key = "client-id", LabelKey = "DcRealms_ClientId", Required = true },
                new FormField { Key = "client-key", LabelKey = "DcRealms_ClientKey", Kind = FormFieldKind.Password },
                new FormField { Key = "username-claim", LabelKey = "DcRealms_UsernameClaim" },
                new FormField { Key = "scopes", LabelKey = "DcRealms_Scopes", Initial = "email profile" },
                new FormField { Key = "autocreate", LabelKey = "DcRealms_AutoCreate", Kind = FormFieldKind.Bool }
            ],
            _ => DirectoryFields(type)
        });
        fields.Add(new FormField { Key = "comment", LabelKey = "Table_Comment" });
        fields.Add(new FormField { Key = "default", LabelKey = "DcRealms_Default", Kind = FormFieldKind.Bool });

        return await SubmitAsync(owner, Loc.T("DcRealms_AddTypeTitle", type.ToUpperInvariant()), fields, values =>
        {
            var form = NonEmpty(values);
            form["type"] = type;
            return api.PostActionAsync("access/domains", form);
        }, "DcRealms_Added", titleIsKey: false);
    }

    private static List<FormField> DirectoryFields(string type)
    {
        var fields = new List<FormField>();
        if (type == "ad")
            fields.Add(new FormField { Key = "domain", LabelKey = "DcRealms_Domain", Required = true });
        else
        {
            fields.Add(new FormField { Key = "base_dn", LabelKey = "DcRealms_BaseDn", Required = true });
            fields.Add(new FormField
            {
                Key = "user_attr", LabelKey = "DcRealms_UserAttr", Initial = "uid", Required = true
            });
        }

        fields.AddRange(
        [
            new FormField { Key = "server1", LabelKey = "DcRealms_Server1", Required = true },
            new FormField { Key = "server2", LabelKey = "DcRealms_Server2" },
            new FormField { Key = "port", LabelKey = "DcRealms_Port" },
            new FormField
            {
                Key = "mode", LabelKey = "DcRealms_Mode", Kind = FormFieldKind.Choice, Initial = "ldap",
                Choices = [("ldap", "LDAP"), ("ldaps", "LDAPS"), ("ldap+starttls", "LDAP + STARTTLS")]
            },
            new FormField { Key = "verify", LabelKey = "DcRealms_Verify", Kind = FormFieldKind.Bool },
            new FormField { Key = "bind_dn", LabelKey = "DcRealms_BindDn" },
            new FormField { Key = "password", LabelKey = "DcStorage_Password", Kind = FormFieldKind.Password }
        ]);
        return fields;
    }

    private static string FormatExpire(string raw)
    {
        return raw is "" or "0" ? string.Empty : TableFormats.EpochDate(raw);
    }
}
