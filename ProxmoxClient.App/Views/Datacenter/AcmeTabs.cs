using System.Text;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>데이터센터 ACME — 인증 기관 계정과 DNS 검증 플러그인. 노드 인증서 탭에서 이 계정·플러그인을 쓴다.</summary>
internal static class AcmeTabs
{
    private static readonly IReadOnlyList<TableColumn> AccountColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> PluginColumns =
    [
        new() { Key = "plugin", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 80 },
        new() { Key = "api", HeaderKey = "DcAcme_Api", Width = 120 },
        new() { Key = "validation-delay", HeaderKey = "DcAcme_Delay", Width = 100 },
        new() { Key = "nodes", HeaderKey = "Table_Nodes", Width = 0 },
        new() { Key = "disable", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.InverseFlag }
    ];

    public static SubTabsView Create(ProxmoxApiClient api, bool canEdit)
    {
        return new SubTabsView(
        [
            ("DcAcme_Accounts", () => new TableTab(() => api.Acme.ListAccountsAsync(), AccountColumns,
                "DcAcme_AccountsHint", canEdit ? AccountActions(api) : null)),
            ("DcAcme_Plugins", () => new TableTab(() => api.Acme.ListPluginsAsync(), PluginColumns,
                "DcAcme_PluginsHint", canEdit ? PluginActions(api) : null))
        ]);
    }

    private static IReadOnlyList<TableAction> AccountActions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "DcAcme_Register", IconKey = "IconPlus", Run = (_, owner) => RegisterAsync(api, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("DcAcme_DeleteAccountConfirm", row!["name"]),
                Run = async (row, _) => await RunTaskAsync(api,
                    api.Acme.DeactivateAccountAsync(row!["name"]), "DcAcme_AccountDeleted")
            },
            new TableAction
            {
                LabelKey = "DcAcme_EditContact", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitTaskAsync(api, owner, Loc.T("DcAcme_EditContactTitle", row!["name"]),
                [
                    new FormField { Key = "contact", LabelKey = "Table_Email", Required = true, Trim = true }
                ], values => api.Acme.UpdateAccountAsync(row["name"], values["contact"]), "DcAcme_ContactUpdated")
            },
            new TableAction
            {
                LabelKey = "DcAcme_DirectoryMeta", IconKey = "IconSearch",
                Requires = api.Acme.Feature(nameof(Core.Api.Domains.AcmeApi.DirectoryMetaJsonAsync)),
                Run = async (_, owner) =>
                {
                    var directories = (await api.Acme.DirectoriesAsync())
                        .Select(d => (Value(d, "url"), Value(d, "name"))).ToList();
                    string? json = null;
                    var picked = await SubmitAsync(owner, "DcAcme_DirectoryMeta",
                    [
                        new FormField { Key = "directory", LabelKey = "DcAcme_Directory", Kind = FormFieldKind.Choice,
                            Choices = directories, Required = true }
                    ], async values =>
                    {
                        json = await api.Acme.DirectoryMetaJsonAsync(values["directory"]);
                        return string.Empty;
                    }, "DcAcme_DirectoryMeta");
                    return picked is null || json is null
                        ? null
                        : TextViewWindow.ShowModal(owner, Loc.T("DcAcme_DirectoryMeta"), json);
                }
            },
            // 계정 정보 보기(연락처·상태·약관 등) — 웹 UI 의 View 버튼
            new TableAction
            {
                LabelKey = "DcAcme_ViewAccount", IconKey = "IconList", NeedsSelection = true,
                Run = async (row, owner) => TextViewWindow.ShowModal(owner, Loc.T("DcAcme_ViewAccount"),
                    await api.Acme.AccountJsonAsync(row!["name"]))
            }
        ];
    }

    /// <summary>
    ///     계정 등록 — 인증 기관(디렉터리)을 고르고, 그 기관의 이용 약관 주소를 보여 준 뒤 사용자가 직접 동의해야 등록한다.
    /// </summary>
    private static async Task<string?> RegisterAsync(ProxmoxApiClient api, Window? owner)
    {
        var directories = (await api.Acme.DirectoriesAsync())
            .Select(d => (Value(d, "url"), Value(d, "name")))
            .ToList();

        var pick = new FormDialog(Loc.T("DcAcme_Register"),
        [
            new FormField { Key = "name", LabelKey = "Table_Name", Required = true, Initial = "default" },
            new FormField { Key = "contact", LabelKey = "Table_Email", Required = true },
            new FormField
            {
                Key = "directory", LabelKey = "DcAcme_Directory", Kind = FormFieldKind.Choice, Choices = directories,
                Required = true
            }
        ]) { Owner = owner };
        if (pick.ShowDialog() != true || pick.Result is not { } account) return null;

        var tos = await api.Acme.TermsOfServiceAsync(account["directory"]);
        var fields = new List<FormField>();
        if (tos.Length > 0)
            fields.Add(new FormField
            {
                Key = "accept", LabelKey = "DcAcme_AcceptTos", Kind = FormFieldKind.Bool, Initial = "0"
            });

        return await SubmitTaskAsync(api, owner, Loc.T("DcAcme_TosTitle", tos.Length > 0 ? tos : "-"), fields,
            _ =>
            {
                var form = new Dictionary<string, string>(account, StringComparer.Ordinal);
                if (tos.Length > 0) form["tos_url"] = tos;
                return api.Acme.RegisterAccountAsync(form);
            },
            "DcAcme_Registered",
            values => tos.Length == 0 || values["accept"] == "1" ? null : Loc.T("DcAcme_TosRequired"));
    }

    private static IReadOnlyList<TableAction> PluginActions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "DcAcme_AddPlugin", IconKey = "IconPlus",
                Run = (_, owner) => EditPluginAsync(api, null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditPluginAsync(api, row, owner)
            },
            DeleteAction(row => Loc.T("DcAcme_DeletePluginConfirm", row["plugin"]),
                row => api.Acme.DeletePluginAsync(row["plugin"]), "DcAcme_PluginDeleted")
        ];
    }

    /// <summary>서버의 base64 인증 정보를 글로 푼다. 형식이 맞지 않으면 받은 그대로 둔다.</summary>
    internal static string DecodeData(string raw)
    {
        if (raw.Length == 0) return raw;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        }
        catch (FormatException)
        {
            return raw;
        }
    }

    /// <summary>DNS 플러그인 — 인증 정보는 "KEY=값" 줄 형식으로 받아 서버가 요구하는 base64 로 보낸다.</summary>
    private static async Task<string?> EditPluginAsync(ProxmoxApiClient api,
        IReadOnlyDictionary<string, string>? row, Window? owner)
    {
        string Initial(string key, string fallback = "") => row is null ? fallback : Value(row, key);

        // 서버는 인증 정보를 base64 로 주고받는다 — 화면에는 풀어서 "KEY=값" 줄로 보여 준다
        var currentData = DecodeData(Initial("data"));
        var fields = new List<FormField>();
        if (row is null) fields.Add(new FormField { Key = "id", LabelKey = "Table_Name", Required = true });
        // DNS API 는 서버가 지원하는 목록(challenge-schema)에서 고른다 — 못 읽으면 직접 입력
        var apis = await DnsApiChoicesAsync(api);
        fields.Add(apis.Count > 0
            ? new FormField { Key = "api", LabelKey = "DcAcme_Api", Kind = FormFieldKind.Choice, Choices = apis,
                Required = true, Initial = Initial("api") }
            : new FormField { Key = "api", LabelKey = "DcAcme_Api", Required = true, Initial = Initial("api") });
        fields.Add(new FormField
        {
            Key = "data", LabelKey = "DcAcme_Data", Kind = FormFieldKind.Multiline, Initial = currentData
        });
        fields.Add(new FormField
        {
            Key = "validation-delay", LabelKey = "DcAcme_Delay", Initial = Initial("validation-delay", "30")
        });
        fields.Add(new FormField { Key = "nodes", LabelKey = "DcStorage_Nodes", Initial = Initial("nodes") });

        var title = row is null ? Loc.T("DcAcme_AddPlugin") : Loc.T("DcAcme_EditPlugin", row["plugin"]);
        return await SubmitAsync(owner, title, fields, values =>
        {
            var data = values["data"].Replace("\r\n", "\n");
            var edited = new Dictionary<string, string>(values, StringComparer.Ordinal)
            {
                ["data"] = data.Length > 0 ? Convert.ToBase64String(Encoding.UTF8.GetBytes(data)) : string.Empty
            };

            // 인증 정보를 건드리지 않았으면 그대로 둔다(다시 보내지도, 지우지도 않는다)
            if (row is not null && data == currentData.Replace("\r\n", "\n")) edited.Remove("data");

            if (row is null)
            {
                var form = NonEmpty(edited);
                form["type"] = "dns";
                return api.Acme.CreatePluginAsync(form);
            }

            return api.Acme.UpdatePluginAsync(row["plugin"], UpdateForm(edited));
        }, row is null ? "DcAcme_PluginAdded" : "DcAcme_PluginUpdated", titleIsKey: false,
            validate: values => int.TryParse(values["validation-delay"], out var d) && d is >= 0 and <= 172800
                ? null
                : Loc.T("DcAcme_BadDelay"));
    }

    private static async Task<IReadOnlyList<(string, string)>> DnsApiChoicesAsync(ProxmoxApiClient api)
    {
        try
        {
            return (await api.Acme.ChallengeSchemaAsync())
                .Where(s => Value(s, "type") is "" or "dns")
                .Select(s => (Value(s, "id"),
                    Value(s, "name") is { Length: > 0 } n ? $"{n} ({Value(s, "id")})" : Value(s, "id")))
                .Where(s => s.Item1.Length > 0)
                .OrderBy(s => s.Item2, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (ProxmoxApiException)
        {
            return [];
        }
    }
}
