using System.Net.Http;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>데이터센터 메트릭 서버(외부 모니터링으로 수치 보내기)와 알림(대상·규칙).</summary>
internal static class MonitoringTabs
{
    /// <summary>
    ///     대상은 항목마다 하나씩 보내는 배열이다. 심각도(match-severity)는 한 항목 안에 쉼표 목록이 들어가는
    ///     형식이라 여기에 넣지 않는다 — 나눠 보내면 서로 다른 조건이 되어 mode=all 에서 영영 맞지 않는다.
    /// </summary>
    private static readonly HashSet<string> MatcherArrays = new(StringComparer.Ordinal) { "target" };

    private static readonly IReadOnlyList<TableColumn> MetricColumns =
    [
        new() { Key = "id", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 100 },
        new() { Key = "server", HeaderKey = "DcStorage_Server", Width = 0 },
        new() { Key = "port", HeaderKey = "DcRealms_Port", Width = 80 },
        new() { Key = "disable", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.InverseFlag }
    ];

    private static readonly IReadOnlyList<TableColumn> TargetColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 180 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 100 },
        new() { Key = "origin", HeaderKey = "DcNotify_Origin", Width = 110 },
        new() { Key = "disable", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.InverseFlag },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> MatcherColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "target", HeaderKey = "DcNotify_Targets", Width = 160 },
        new() { Key = "match-severity", HeaderKey = "DcNotify_Severity", Width = 160 },
        new() { Key = "mode", HeaderKey = "Table_Mode", Width = 60 },
        new() { Key = "disable", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.InverseFlag },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly IReadOnlyList<(string, string)> Severities =
        [("info", "info"), ("notice", "notice"), ("warning", "warning"), ("error", "error"), ("unknown", "unknown")];

    // ------------------------------------------------------------ 메트릭 서버

    public static TableTab Metrics(ProxmoxApiClient api, bool canEdit)
    {
        const string path = "cluster/metrics/server";
        IReadOnlyList<TableAction>? actions = canEdit
            ?
            [
                new TableAction
                {
                    LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddMetricAsync(api, owner)
                },
                new TableAction
                {
                    LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                    Run = (row, owner) => SubmitAsync(owner, Loc.T("DcMetrics_EditTitle", row!["id"]),
                    [
                        new FormField
                        {
                            Key = "server", LabelKey = "DcStorage_Server", Required = true,
                            Initial = Value(row, "server")
                        },
                        new FormField
                        {
                            Key = "port", LabelKey = "DcRealms_Port", Required = true, Initial = Value(row, "port")
                        },
                        new FormField
                        {
                            Key = "enabled", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
                            Initial = Value(row, "disable") is "1" ? "0" : "1"
                        }
                    ], values => api.PutActionAsync($"{path}/{Seg(row["id"])}", new Dictionary<string, string>
                    {
                        ["server"] = values["server"], ["port"] = values["port"],
                        ["disable"] = values["enabled"] == "1" ? "0" : "1"
                    }), "DcMetrics_Updated", titleIsKey: false)
                },
                DeleteAction(row => Loc.T("DcMetrics_DeleteConfirm", row["id"]),
                    row => api.DeleteActionAsync($"{path}/{Seg(row["id"])}"), "DcMetrics_Deleted")
            ]
            : null;

        return new TableTab(() => api.GetTableAsync(path), MetricColumns, "DcMetrics_Hint", actions);
    }

    private static async Task<string?> AddMetricAsync(ProxmoxApiClient api, Window? owner)
    {
        var choose = new FormDialog(Loc.T("DcMetrics_AddTitle"),
        [
            new FormField
            {
                Key = "type", LabelKey = "Table_Type", Kind = FormFieldKind.Choice, Initial = "influxdb",
                Choices = [("influxdb", "InfluxDB"), ("graphite", "Graphite")]
            }
        ]) { Owner = owner };
        if (choose.ShowDialog() != true || choose.Result is not { } picked) return null;

        var type = picked["type"];
        var fields = new List<FormField>
        {
            new() { Key = "id", LabelKey = "Table_Name", Required = true },
            new() { Key = "server", LabelKey = "DcStorage_Server", Required = true },
            new()
            {
                Key = "port", LabelKey = "DcRealms_Port", Required = true,
                Initial = type == "graphite" ? "2003" : "8089"
            }
        };
        if (type == "graphite")
        {
            fields.Add(new FormField { Key = "path", LabelKey = "DcMetrics_Path", Initial = "proxmox" });
            fields.Add(new FormField
            {
                Key = "proto", LabelKey = "DcMetrics_Protocol", Kind = FormFieldKind.Choice, Initial = "udp",
                Choices = [("udp", "UDP"), ("tcp", "TCP")]
            });
        }
        else
        {
            fields.Add(new FormField
            {
                Key = "influxdbproto", LabelKey = "DcMetrics_Protocol", Kind = FormFieldKind.Choice, Initial = "udp",
                Choices = [("udp", "UDP"), ("http", "HTTP"), ("https", "HTTPS")]
            });
            fields.Add(new FormField { Key = "organization", LabelKey = "DcMetrics_Organization" });
            fields.Add(new FormField { Key = "bucket", LabelKey = "DcMetrics_Bucket" });
            fields.Add(new FormField { Key = "token", LabelKey = "DcMetrics_Token", Kind = FormFieldKind.Password });
        }

        return await SubmitAsync(owner, Loc.T("DcMetrics_AddTypeTitle", type), fields, values =>
        {
            var form = NonEmpty(values);
            form.Remove("id");
            form["type"] = type;
            return api.PostActionAsync($"cluster/metrics/server/{Seg(values["id"])}", form);
        }, "DcMetrics_Added", titleIsKey: false);
    }

    // ------------------------------------------------------------ 알림

    public static SubTabsView Notifications(ProxmoxApiClient api, bool canEdit)
    {
        return new SubTabsView(
        [
            ("DcNotify_TargetsTab", () => new TableTab(() => api.GetTableAsync("cluster/notifications/targets"),
                TargetColumns, "DcNotify_TargetsHint", TargetActions(api, canEdit))),
            ("DcNotify_MatchersTab", () => new TableTab(() => api.GetTableAsync("cluster/notifications/matchers"),
                MatcherColumns, "DcNotify_MatchersHint", canEdit ? MatcherActions(api) : null))
        ]);
    }

    private static IReadOnlyList<TableAction> TargetActions(ProxmoxApiClient api, bool canEdit)
    {
        var test = new TableAction
        {
            LabelKey = "DcNotify_Test", IconKey = "IconCheck", NeedsSelection = true,
            Run = async (row, _) =>
            {
                await api.PostActionAsync($"cluster/notifications/targets/{Seg(row!["name"])}/test");
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
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("DcNotify_DeleteConfirm", row!["name"]),
                Run = async (row, _) =>
                {
                    var target = row!;
                    // 기본 제공 대상(mail-to-root 등)은 서버가 지울 수 없게 막는다
                    if (Value(target, "origin") == "builtin") return Loc.T("DcNotify_BuiltIn");

                    await api.DeleteActionAsync(
                        $"cluster/notifications/endpoints/{Seg(target["type"])}/{Seg(target["name"])}");
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
                Choices = [("sendmail", "Sendmail"), ("smtp", "SMTP"), ("gotify", "Gotify")]
            }
        ]) { Owner = owner };
        if (choose.ShowDialog() != true || choose.Result is not { } picked) return null;

        var type = picked["type"];
        var fields = new List<FormField> { new() { Key = "name", LabelKey = "Table_Name", Required = true } };
        fields.AddRange(type switch
        {
            "gotify" =>
            [
                new FormField { Key = "server", LabelKey = "DcStorage_Server", Required = true },
                new FormField
                {
                    Key = "token", LabelKey = "DcMetrics_Token", Kind = FormFieldKind.Password, Required = true
                }
            ],
            "smtp" =>
            [
                new FormField { Key = "server", LabelKey = "DcStorage_Server", Required = true },
                new FormField
                {
                    Key = "mode", LabelKey = "DcRealms_Mode", Kind = FormFieldKind.Choice, Initial = "tls",
                    Choices = [("tls", "TLS"), ("starttls", "STARTTLS"), ("insecure", "DcNotify_Insecure")]
                },
                new FormField { Key = "port", LabelKey = "DcRealms_Port" },
                new FormField { Key = "username", LabelKey = "DcUsers_UserName" },
                new FormField { Key = "password", LabelKey = "DcStorage_Password", Kind = FormFieldKind.Password },
                new FormField { Key = "from-address", LabelKey = "DcOptions_EmailFrom", Required = true },
                new FormField { Key = "mailto", LabelKey = "DcNotify_MailTo" },
                new FormField { Key = "mailto-user", LabelKey = "DcNotify_MailToUser", Initial = "root@pam" }
            ],
            _ =>
            [
                new FormField { Key = "mailto", LabelKey = "DcNotify_MailTo" },
                new FormField { Key = "mailto-user", LabelKey = "DcNotify_MailToUser", Initial = "root@pam" },
                new FormField { Key = "from-address", LabelKey = "DcOptions_EmailFrom" }
            ]
        });
        fields.Add(new FormField { Key = "comment", LabelKey = "Table_Comment" });

        return await SubmitAsync(owner, Loc.T("DcNotify_AddTypeTitle", type), fields,
            values => api.SendWithArraysAsync(HttpMethod.Post, $"cluster/notifications/endpoints/{type}",
                NonEmpty(values), new HashSet<string>(StringComparer.Ordinal) { "mailto", "mailto-user" }),
            "DcNotify_Added", titleIsKey: false);
    }

    private static IReadOnlyList<TableAction> MatcherActions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => EditMatcherAsync(api, null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditMatcherAsync(api, row, owner)
            },
            DeleteAction(row => Loc.T("DcNotify_DeleteConfirm", row["name"]),
                row => api.DeleteActionAsync($"cluster/notifications/matchers/{Seg(row["name"])}"),
                "DcNotify_Deleted")
        ];
    }

    /// <summary>알림 규칙 — 어떤 심각도의 알림을 어느 대상으로 보낼지.</summary>
    private static async Task<string?> EditMatcherAsync(ProxmoxApiClient api,
        IReadOnlyDictionary<string, string>? row, Window? owner)
    {
        string Initial(string key, string fallback = "") => row is null ? fallback : Value(row, key);

        var targets = (await api.GetTableAsync("cluster/notifications/targets"))
            .Select(t => (Value(t, "name"), Value(t, "name")))
            .ToList();

        var fields = new List<FormField>();
        if (row is null) fields.Add(new FormField { Key = "name", LabelKey = "Table_Name", Required = true });
        fields.AddRange(
        [
            new FormField
            {
                Key = "target", LabelKey = "DcNotify_Targets", Kind = FormFieldKind.MultiChoice, Choices = targets,
                Initial = Initial("target").Replace(" ", ""), Required = true
            },
            new FormField
            {
                Key = "match-severity", LabelKey = "DcNotify_Severity", Kind = FormFieldKind.MultiChoice,
                Choices = Severities, Initial = Initial("match-severity").Replace(" ", "")
            },
            new FormField
            {
                Key = "mode", LabelKey = "Table_Mode", Kind = FormFieldKind.Choice, Initial = Initial("mode", "all"),
                Choices = [("all", "DcNotify_ModeAll"), ("any", "DcNotify_ModeAny")]
            },
            new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Initial("comment") },
            new FormField
            {
                Key = "enabled", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
                Initial = Initial("disable") is "1" ? "0" : "1"
            }
        ]);

        var title = row is null ? Loc.T("DcNotify_AddMatcher") : Loc.T("DcNotify_EditMatcher", row["name"]);
        return await SubmitAsync(owner, title, fields, values =>
        {
            var edited = values.Where(kv => kv.Key != "enabled")
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            edited["disable"] = values["enabled"] == "1" ? "0" : "1";

            return row is null
                ? api.SendWithArraysAsync(HttpMethod.Post, "cluster/notifications/matchers", NonEmpty(edited),
                    MatcherArrays)
                : api.SendWithArraysAsync(HttpMethod.Put, $"cluster/notifications/matchers/{Seg(row["name"])}",
                    UpdateForm(edited), MatcherArrays);
        }, row is null ? "DcNotify_Added" : "DcNotify_Updated", titleIsKey: false);
    }
}
