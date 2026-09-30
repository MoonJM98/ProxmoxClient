using System.Net.Http;
using System.Text;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     알림 대상(Sendmail·SMTP·Gotify·Webhook)과 규칙(matcher) 추가·수정 — 웹 UI 의 proxmox-widget-toolkit
///     Endpoint*Edit·NotificationMatcherEdit 와 같은 칸. 목록 값(수신자·필드 조건·일정·헤더)은 줄마다 하나씩
///     적고, 서버에는 같은 키를 반복해 보낸다(정규식 안의 쉼표가 갈라지지 않게). 비밀은 비우면 그대로 둔다.
/// </summary>
internal static class NotificationActions
{

    private static readonly IReadOnlyList<(string, string)> TargetTypes =
        [("sendmail", "Sendmail"), ("smtp", "SMTP"), ("gotify", "Gotify"), ("webhook", "Webhook")];

    private static readonly IReadOnlyList<(string, string)> Severities =
        [("info", "info"), ("notice", "notice"), ("warning", "warning"), ("error", "error"), ("unknown", "unknown")];

    public static IReadOnlyList<TableAction> Targets(ProxmoxApiClient api, bool canEdit)
    {
        var test = new TableAction
        {
            LabelKey = "DcNotify_Test", IconKey = "IconCheck", NeedsSelection = true,
            Run = async (row, _) =>
            {
                await api.Notifications.TestTargetAsync(row!["name"]);
                return Loc.T("DcNotify_TestSent", row["name"]);
            }
        };
        if (!canEdit) return [test];

        return
        [
            test,
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddTargetAsync(api, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditTargetAsync(api, row!, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("DcNotify_DeleteConfirm", row!["name"]),
                Run = async (row, _) =>
                {
                    // 기본 제공 대상(mail-to-root 등)은 서버가 지울 수 없게 막는다
                    if (Value(row!, "origin") == "builtin") return Loc.T("DcNotify_BuiltIn");
                    await api.Notifications.DeleteEndpointAsync(row!["type"], row["name"]);
                    return Loc.T("DcNotify_Deleted");
                }
            }
        ];
    }

    private static async Task<string?> AddTargetAsync(ProxmoxApiClient api, Window? owner)
    {
        var choose = new FormDialog(Loc.T("DcNotify_AddTarget"),
        [
            new FormField
            {
                Key = "type", LabelKey = "Table_Type", Kind = FormFieldKind.Choice, Initial = "sendmail",
                // webhook 은 8.2+ — 서버가 모르는 종류는 고를 수 없게 한다
                Choices = TargetTypes.Where(t => api.Notifications.EndpointTypes.Contains(t.Item1)).ToList()
            }
        ]) { Owner = owner };
        if (choose.ShowDialog() != true || choose.Result is not { } picked) return null;

        var type = picked["type"];
        return await SubmitAsync(owner, Loc.T("DcNotify_AddTypeTitle", type), TargetFields(type, null),
            values => api.Notifications.CreateEndpointAsync(type, NonEmpty(TargetForm(type, values))),
            "DcNotify_Added", titleIsKey: false,
            validate: v => ValidateTarget(type, v));
    }

    private static async Task<string?> EditTargetAsync(ProxmoxApiClient api, IReadOnlyDictionary<string, string> row,
        Window? owner)
    {
        if (Value(row, "origin") == "builtin" && Value(row, "type") != "sendmail")
            return Loc.T("DcNotify_BuiltIn");
        var type = Value(row, "type");
        var config = await api.Notifications.GetEndpointAsync(type, row["name"]);
        return await SubmitAsync(owner, Loc.T("DcNotify_EditTarget", row["name"]), TargetFields(type, config),
            values =>
            {
                var form = TargetForm(type, values);
                foreach (var secret in new[] { "password", "token" })
                    if (form.TryGetValue(secret, out var v) && v.Length == 0) form.Remove(secret);
                return api.Notifications.UpdateEndpointAsync(type, row["name"], UpdateForm(form));
            }, "DcNotify_Updated", titleIsKey: false, validate: v => ValidateTarget(type, v));
    }

    private static List<FormField> TargetFields(string type, IReadOnlyDictionary<string, string>? config)
    {
        var isCreate = config is null;
        string V(string key, string fallback = "") =>
            config is null ? fallback : config.TryGetValue(key, out var v) ? v : string.Empty;
        FormField Text(string key, string labelKey, bool required = false, string? hint = null) =>
            new() { Key = key, LabelKey = labelKey, Required = required, Initial = V(key), Trim = true,
                Hint = hint is null ? null : Loc.T(hint) };
        FormField Lines(string key, string labelKey, string fallback = "", string? hint = null) =>
            new() { Key = key, LabelKey = labelKey, Kind = FormFieldKind.Multiline, Initial = V(key, fallback),
                Hint = hint is null ? null : Loc.T(hint) };
        FormField Secret(string key, string labelKey, bool required = false) =>
            new() { Key = key, LabelKey = labelKey, Kind = FormFieldKind.Password, Required = required && isCreate,
                Hint = isCreate ? null : Loc.T("StorageHint_KeepSecret") };

        var fields = new List<FormField>();
        if (isCreate) fields.Add(new FormField { Key = "name", LabelKey = "Table_Name", Required = true, Trim = true });
        fields.Add(new FormField { Key = "enable", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
            Initial = V("disable") is "1" ? "0" : "1" });
        switch (type)
        {
            case "gotify":
                fields.AddRange([Text("server", "DcStorage_Server", true, "DcNotify_UrlHint"),
                    Secret("token", "DcMetrics_Token", true)]);
                break;
            case "webhook":
                fields.AddRange(
                [
                    new FormField { Key = "method", LabelKey = "DcNotify_Method", Kind = FormFieldKind.Choice,
                        Initial = V("method", "post"), Choices = [("post", "POST"), ("put", "PUT"), ("get", "GET")] },
                    Text("url", "StorageDownload_Url", true, "DcNotify_UrlHint"),
                    new FormField { Key = "headers", LabelKey = "DcNotify_Headers", Kind = FormFieldKind.Multiline,
                        Initial = Webhook.DecodeHeaders(V("header")), Hint = Loc.T("DcNotify_HeadersHint") },
                    new FormField { Key = "body-text", LabelKey = "DcNotify_Body", Kind = FormFieldKind.Multiline,
                        Initial = Webhook.Decode(V("body")), Hint = Loc.T("DcNotify_BodyHint") }
                ]);
                break;
            case "smtp":
                fields.AddRange(
                [
                    Text("server", "DcStorage_Server", true),
                    new FormField { Key = "mode", LabelKey = "DcRealms_Mode", Kind = FormFieldKind.Choice,
                        Initial = V("mode", "tls"),
                        Choices = [("tls", "TLS"), ("starttls", "STARTTLS"), ("insecure", "DcNotify_Insecure")] },
                    Text("port", "DcRealms_Port", hint: "DcNotify_SmtpPortHint"),
                    Text("username", "DcUsers_UserName"), Secret("password", "DcStorage_Password"),
                    Text("from-address", "DcOptions_EmailFrom", true)
                ]);
                fields.AddRange(MailFields(Lines));
                break;
            default:
                fields.AddRange(MailFields(Lines));
                fields.Add(Text("from-address", "DcOptions_EmailFrom", hint: "DcOpt_EmailHint"));
                break;
        }

        if (type is "sendmail" or "smtp")
            fields.Add(new FormField { Key = "author", LabelKey = "DcNotify_Author", Initial = V("author"),
                Trim = true, Advanced = true, Hint = Loc.T("DcNotify_AuthorHint") });
        fields.Add(new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = V("comment") });
        return fields;
    }

    private static IEnumerable<FormField> MailFields(Func<string, string, string, string?, FormField> lines)
    {
        yield return lines("mailto-user", "DcNotify_MailToUser", "root@pam", "DcNotify_OnePerLine");
        yield return lines("mailto", "DcNotify_MailTo", "", "DcNotify_OnePerLine");
    }

    /// <summary>사용 칸 → disable, Webhook 머리글·본문 → 서버 형식(base64).</summary>
    internal static Dictionary<string, string> TargetForm(string type, IReadOnlyDictionary<string, string> values)
    {
        var form = values.Where(kv => kv.Key is not ("enable" or "headers" or "body-text"))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        form["disable"] = values["enable"] == "1" ? "0" : "1";
        if (type == "webhook")
        {
            form["header"] = Webhook.EncodeHeaders(values["headers"]) ?? string.Empty;
            form["body"] = values["body-text"].Length > 0 ? Webhook.Encode(values["body-text"]) : string.Empty;
        }

        return form;
    }

    private static string? ValidateTarget(string type, IReadOnlyDictionary<string, string> values)
    {
        if (type is "sendmail" or "smtp"
            && values["mailto"].Trim().Length == 0 && values["mailto-user"].Trim().Length == 0)
            return Loc.T("DcNotify_NeedRecipient");
        if (values.TryGetValue("port", out var port) && port.Length > 0
            && !(int.TryParse(port, out var p) && p is >= 1 and <= 65535))
            return Loc.T("DcStorage_BadPort");
        return type == "webhook" && Webhook.EncodeHeaders(values["headers"]) is null
            ? Loc.T("DcNotify_HeadersHint")
            : null;
    }

    // ------------------------------------------------------------ 규칙(matcher)

    private static readonly IReadOnlyList<TableColumn> FieldValueColumns =
    [
        new() { Key = "field", HeaderKey = "DcNotify_Field", Width = 120 },
        new() { Key = "value", HeaderKey = "NodeSubscription_Value", Width = 200 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    /// <summary>규칙의 필드 조건에 쓸 수 있는 값(8.2+) — 'exact:필드=값' 을 적을 때 참고한다.</summary>
    public static TableAction FieldValues(ProxmoxApiClient api)
    {
        return new TableAction
        {
            LabelKey = "DcNotify_FieldValues", IconKey = "IconList",
            Requires = api.Notifications.Feature(nameof(Core.Api.Domains.NotificationsApi.MatcherFieldValuesAsync)),
            Run = (_, owner) => Task.FromResult(TableWindow.ShowModal(owner, Loc.T("DcNotify_FieldValues"),
                new TableTab(() => api.Notifications.MatcherFieldValuesAsync(), FieldValueColumns,
                    "DcNotify_FieldValuesHint")))
        };
    }

    public static IReadOnlyList<TableAction> Matchers(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => EditMatcherAsync(api, null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditMatcherAsync(api, row!["name"], owner)
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("DcNotify_DeleteConfirm", row!["name"]),
                Run = async (row, _) =>
                {
                    // 기본 제공 규칙(default-matcher)은 서버가 지울 수 없게 막는다
                    if (Value(row!, "origin") == "builtin") return Loc.T("DcNotify_BuiltIn");
                    await api.Notifications.DeleteMatcherAsync(row!["name"]);
                    return Loc.T("DcNotify_Deleted");
                }
            }
        ];
    }

    /// <summary>
    ///     알림 규칙 — 대상, 심각도, 필드 조건(exact:type=vzdump / regex:hostname=^pve), 일정(mon..fri 8-17),
    ///     모두/하나 이상, 반전. 심각도는 한 항목 안의 쉼표 목록이라 나눠 보내지 않는다.
    /// </summary>
    private static async Task<string?> EditMatcherAsync(ProxmoxApiClient api, string? name, Window? owner)
    {
        var config = name is null ? null : await api.Notifications.GetMatcherAsync(name);
        string V(string key, string fallback = "") =>
            config is null ? fallback : config.TryGetValue(key, out var v) ? v : string.Empty;
        var targets = (await api.Notifications.ListTargetsAsync())
            .Select(t => (Value(t, "name"), Value(t, "name"))).ToList();

        var fields = new List<FormField>();
        if (name is null)
            fields.Add(new FormField { Key = "name", LabelKey = "Table_Name", Required = true, Trim = true });
        fields.AddRange(
        [
            new FormField { Key = "enable", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
                Initial = V("disable") is "1" ? "0" : "1" },
            new FormField { Key = "target", LabelKey = "DcNotify_Targets", Kind = FormFieldKind.MultiChoice,
                Choices = targets, Initial = V("target").Replace('\n', ','), Required = true },
            new FormField { Key = "match-severity", LabelKey = "DcNotify_Severity", Kind = FormFieldKind.MultiChoice,
                Choices = Severities, Initial = V("match-severity").Replace('\n', ',').Replace(" ", "") },
            new FormField { Key = "match-field", LabelKey = "DcNotify_MatchField", Kind = FormFieldKind.Multiline,
                Initial = V("match-field"), Hint = Loc.T("DcNotify_MatchFieldHint") },
            new FormField { Key = "match-calendar", LabelKey = "DcNotify_MatchCalendar",
                Kind = FormFieldKind.Multiline, Initial = V("match-calendar"),
                Hint = Loc.T("DcNotify_MatchCalendarHint") },
            new FormField { Key = "mode", LabelKey = "Table_Mode", Kind = FormFieldKind.Choice,
                Initial = V("mode", "all"), Choices = [("all", "DcNotify_ModeAll"), ("any", "DcNotify_ModeAny")] },
            new FormField { Key = "invert-match", LabelKey = "DcNotify_Invert", Kind = FormFieldKind.Bool,
                Initial = V("invert-match") is "1" or "true" ? "1" : "0" },
            new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = V("comment") }
        ]);

        var title = name is null ? Loc.T("DcNotify_AddMatcher") : Loc.T("DcNotify_EditMatcher", name);
        return await SubmitAsync(owner, title, fields, values =>
        {
            var form = values.Where(kv => kv.Key != "enable")
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            form["disable"] = values["enable"] == "1" ? "0" : "1";
            return name is null
                ? api.Notifications.CreateMatcherAsync(NonEmpty(form))
                : api.Notifications.UpdateMatcherAsync(name, UpdateForm(form));
        }, name is null ? "DcNotify_Added" : "DcNotify_Updated", titleIsKey: false);
    }

    /// <summary>Webhook 머리글·본문 형식 — 서버는 값을 base64 로 저장한다(name=X,value=base64).</summary>
    internal static class Webhook
    {
        public static string Encode(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }

        public static string Decode(string base64)
        {
            try
            {
                return base64.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
            catch (FormatException)
            {
                return base64;
            }
        }

        /// <summary>"Name: value" 줄들 → "name=Name,value=base64" 줄들. 콜론 없는 줄이 있으면 null.</summary>
        public static string? EncodeHeaders(string lines)
        {
            var result = new List<string>();
            var entries = lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in entries)
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) return null;
                result.Add($"name={line[..colon].Trim()},value={Encode(line[(colon + 1)..].Trim())}");
            }

            return string.Join('\n', result);
        }

        public static string DecodeHeaders(string lines)
        {
            return string.Join('\n', lines.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
            {
                var p = Core.Models.PropertyString.Parse(line);
                return $"{p.Get("name")}: {Decode(p.Get("value"))}";
            }));
        }
    }
}
