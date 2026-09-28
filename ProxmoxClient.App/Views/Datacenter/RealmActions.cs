using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     인증 영역(웹 UI AuthEditBase·AuthEditLDAP·AuthEditAD·AuthEditOpenId·SyncWindow) — 추가·수정·동기화·삭제.
///     수정은 서버 설정을 읽어 모든 칸을 채우고(암호·클라이언트 키는 비우면 그대로), 만든 뒤 못 바꾸는 칸
///     (이름·Base DN·사용자 속성·사용자 이름 클레임)은 보여 주기만 한다. 2단계 인증 강제(tfa)와
///     동기화 기본값(sync-defaults-options)도 여기서 고친다.
/// </summary>
internal static partial class RealmActions
{
    private static readonly (string, string)[] Modes =
        [("ldap", "LDAP"), ("ldaps", "LDAPS"), ("ldap+starttls", "LDAP + STARTTLS")];

    private static readonly (string, string)[] SyncScopes =
    [
        ("", "StorageField_Default"), ("both", "DcRealms_SyncBoth"), ("users", "DcTab_Users"),
        ("groups", "DcTab_Groups")
    ];

    private static readonly (string, string)[] Vanished =
    [
        ("acl", "DcRealms_VanishedAcl"), ("entry", "DcRealms_VanishedEntry"),
        ("properties", "DcRealms_VanishedProps")
    ];

    public static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction { LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddAsync(api, owner) },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditAsync(api, row!["realm"], owner)
            },
            new TableAction
            {
                LabelKey = "DcRealms_Sync", IconKey = "IconRefresh", NeedsSelection = true,
                Run = (row, owner) => SyncAsync(api, row!, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("DcRealms_DeleteConfirm", row!["realm"]),
                Run = async (row, _) =>
                {
                    // pam·pve 는 서버가 지울 수 없게 막는다 — 요청 전에 알려 준다
                    if (Value(row!, "type") is "pam" or "pve") return Loc.T("DcRealms_BuiltInReadOnly");
                    await api.Realms.DeleteAsync(row!["realm"]);
                    return Loc.T("DcRealms_Deleted");
                }
            }
        ];
    }

    private static async Task<string?> AddAsync(ProxmoxApiClient api, Window? owner)
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
        return await SubmitAsync(owner, Loc.T("DcRealms_AddTypeTitle", type.ToUpperInvariant()),
            Fields(type, null), values =>
            {
                var form = NonEmpty(Collect(type, values));
                DropNewDefaults(form, null);
                form["type"] = type;
                return api.Realms.CreateAsync(form);
            }, "DcRealms_Added", titleIsKey: false, validate: v => Validate(type, v));
    }

    private static async Task<string?> EditAsync(ProxmoxApiClient api, string realm, Window? owner)
    {
        var config = (await api.Realms.GetAsync(realm))
            .ToDictionary(kv => kv.Key,
                kv => kv.Key is "sync-defaults-options" or "tfa" ? PropertyString.FromJsonObject(kv.Value) : kv.Value,
                StringComparer.Ordinal);
        var type = config.TryGetValue("type", out var t) ? t : "pve";
        return await SubmitAsync(owner, Loc.T("DcRealms_EditTitle", realm), Fields(type, config), values =>
        {
            var form = Collect(type, values);
            DropNewDefaults(form, config);
            foreach (var secret in new[] { "password", "client-key" })
                if (form.TryGetValue(secret, out var v) && v.Length == 0) form.Remove(secret); // 비우면 그대로
            return api.Realms.UpdateAsync(realm, UpdateForm(form));
        }, "DcRealms_Updated", titleIsKey: false, validate: v => Validate(type, v));
    }

    /// <summary>
    ///     새 PVE 에만 있는 OpenID 옵션(groups-autocreate·groups-overwrite)은 끈 채로 원래 없던 경우 보내지 않는다
    ///     — 이전 서버는 모르는 옵션이 하나라도 있으면 요청 전체를 거절한다.
    /// </summary>
    private static void DropNewDefaults(Dictionary<string, string> form, IReadOnlyDictionary<string, string>? config)
    {
        foreach (var key in new[] { "groups-autocreate", "groups-overwrite" })
            if (form.TryGetValue(key, out var v) && v != "1" && config?.ContainsKey(key) != true)
                form.Remove(key);
    }

    // ------------------------------------------------------------ 칸

    /// <summary><paramref name="config" /> 가 null 이면 추가.</summary>
    internal static List<FormField> Fields(string type, IReadOnlyDictionary<string, string>? config)
    {
        var isCreate = config is null;
        string V(string key, string fallback = "") =>
            config is not null && config.TryGetValue(key, out var v) ? v : isCreate ? fallback : string.Empty;
        FormField Text(string key, string labelKey, bool required = false, string fallback = "", string? hint = null,
            bool advanced = false) =>
            new() { Key = key, LabelKey = labelKey, Required = required, Initial = V(key, fallback), Trim = true,
                Hint = hint is null ? null : Loc.T(hint), Advanced = advanced };
        FormField Fixed(string key, string labelKey, bool required = true, string fallback = "") => isCreate
            ? Text(key, labelKey, required, fallback)
            : new FormField { Key = "_" + key, Kind = FormFieldKind.Section,
                LabelKey = $"{Loc.T(labelKey)}: {V(key)}" };
        FormField Flag(string key, string labelKey, string fallback = "0") =>
            new() { Key = key, LabelKey = labelKey, Kind = FormFieldKind.Bool,
                Initial = V(key, fallback) is "1" ? "1" : "0" };
        FormField Secret(string key, string labelKey) =>
            new() { Key = key, LabelKey = labelKey, Kind = FormFieldKind.Password,
                Hint = isCreate ? null : Loc.T("StorageHint_KeepSecret") };

        var fields = new List<FormField>();
        if (isCreate) fields.Add(Text("realm", "Table_Name", true));

        switch (type)
        {
            case "openid":
                fields.AddRange(
                [
                    Text("issuer-url", "DcRealms_IssuerUrl", true), Text("client-id", "DcRealms_ClientId", true),
                    Secret("client-key", "DcRealms_ClientKey"),
                    Fixed("username-claim", "DcRealms_UsernameClaim", false),
                    Flag("autocreate", "DcRealms_AutoCreate"),
                    Text("scopes", "DcRealms_Scopes", fallback: "email profile", advanced: true),
                    new FormField { Key = "prompt", LabelKey = "DcRealms_Prompt", Kind = FormFieldKind.Choice,
                        Advanced = true, Initial = V("prompt"),
                        Choices = [("", "StorageField_Default"), ("none", "none"), ("login", "login"),
                            ("consent", "consent"), ("select_account", "select_account")] },
                    Text("acr-values", "DcRealms_AcrValues", advanced: true),
                    Text("groups-claim", "DcRealms_GroupsClaim", advanced: true),
                    Flag("groups-autocreate", "DcRealms_GroupsAutoCreate"),
                    Flag("groups-overwrite", "DcRealms_GroupsOverwrite")
                ]);
                break;
            case "ldap" or "ad":
                fields.AddRange(DirectoryFields(type, isCreate, V, Text, Fixed, Flag, Secret));
                break;
        }

        fields.Add(Text("comment", "Table_Comment"));
        fields.Add(Flag("default", "DcRealms_Default"));
        fields.AddRange(TfaFields(V("tfa")));
        return fields;
    }

    private static IEnumerable<FormField> DirectoryFields(string type, bool isCreate, Func<string, string, string> v,
        Func<string, string, bool, string, string?, bool, FormField> text,
        Func<string, string, bool, string, FormField> fixedField, Func<string, string, string, FormField> flag,
        Func<string, string, FormField> secret)
    {
        if (type == "ad")
        {
            yield return text("domain", "DcRealms_Domain", true, "", null, false);
            yield return flag("case-sensitive", "DcRealms_CaseSensitive", "1");
        }
        else
        {
            yield return fixedField("base_dn", "DcRealms_BaseDn", true, "");
            yield return fixedField("user_attr", "DcRealms_UserAttr", true, "uid");
        }

        yield return text("server1", "DcRealms_Server1", true, "", null, false);
        yield return text("server2", "DcRealms_Server2", false, "", null, false);
        yield return text("port", "DcRealms_Port", false, "", "DcRealms_PortHint", false);
        yield return new FormField { Key = "mode", LabelKey = "DcRealms_Mode", Kind = FormFieldKind.Choice,
            Choices = Modes, Initial = v("mode", "ldap") is { Length: > 0 } m ? m : "ldap" };
        yield return flag("verify", "DcRealms_Verify", "0");
        yield return text("bind_dn", "DcRealms_BindDn", false, "", "DcRealms_BindDnHint", false);
        yield return secret("password", "DcStorage_Password");
        yield return flag("check-connection", "DcRealms_CheckConnection", isCreate ? "1" : "0");

        // 동기화 설정(웹 UI 의 "Sync Options" 탭)
        yield return new FormField { Key = "_sync", LabelKey = "DcRealms_SyncOptions", Kind = FormFieldKind.Section };
        yield return text("user_classes", "DcRealms_UserClasses", false, "", "DcRealms_ClassesHint", true);
        yield return text("group_classes", "DcRealms_GroupClasses", false, "", "DcRealms_ClassesHint", true);
        yield return text("filter", "DcRealms_UserFilter", false, "", null, true);
        yield return text("group_filter", "DcRealms_GroupFilter", false, "", null, true);
        yield return text("group_dn", "DcRealms_GroupDn", false, "", null, true);
        yield return text("group_name_attr", "DcRealms_GroupNameAttr", false, "", null, true);
        var sync = PropertyString.Parse(v("sync-defaults-options", ""));
        yield return new FormField { Key = "sync-scope", LabelKey = "DcRealms_SyncScope", Kind = FormFieldKind.Choice,
            Choices = SyncScopes, Initial = sync.Get("scope") };
        yield return new FormField { Key = "sync-enable-new", LabelKey = "DcRealms_EnableNew",
            Kind = FormFieldKind.Bool, Initial = sync.Get("enable-new", "1") is "0" ? "0" : "1" };
        yield return new FormField { Key = "sync-remove-vanished", LabelKey = "DcRealms_RemoveVanished",
            Kind = FormFieldKind.MultiChoice, Choices = Vanished,
            Initial = sync.Get("remove-vanished").Replace(';', ',') };
    }
}
