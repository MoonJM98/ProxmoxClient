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

    // ------------------------------------------------------------ 메트릭 서버


    /// <summary>
    ///     메트릭 서버(웹 UI dc/MetricServerView.js) — Graphite·InfluxDB 추가·수정. 수정은 서버 설정을 읽어
    ///     모든 칸을 채우고(토큰은 비우면 그대로), 비운 칸은 기본값으로 돌린다.
    /// </summary>
    public static TableTab Metrics(ProxmoxApiClient api, bool canEdit)
    {
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
                    Run = (row, owner) => EditMetricAsync(api, row!["id"], owner)
                },
                DeleteAction(row => Loc.T("DcMetrics_DeleteConfirm", row["id"]),
                    row => api.Metrics.DeleteAsync(row["id"]), "DcMetrics_Deleted")
            ]
            : null;

        return new TableTab(() => api.Metrics.ListAsync(), MetricColumns, "DcMetrics_Hint", actions);
    }

    private static readonly (string, string)[] MetricTypes =
        [("influxdb", "InfluxDB"), ("graphite", "Graphite"), ("opentelemetry", "OpenTelemetry")];

    private static async Task<string?> AddMetricAsync(ProxmoxApiClient api, Window? owner)
    {
        var choose = new FormDialog(Loc.T("DcMetrics_AddTitle"),
        [
            new FormField
            {
                Key = "type", LabelKey = "Table_Type", Kind = FormFieldKind.Choice, Initial = "influxdb",
                // OpenTelemetry 는 9.0+ — 서버가 모르는 종류는 고를 수 없게 한다
                Choices = MetricTypes.Where(t => api.Metrics.Types.Contains(t.Item1)).ToList()
            }
        ]) { Owner = owner };
        if (choose.ShowDialog() != true || choose.Result is not { } picked) return null;

        var type = picked["type"];
        return await SubmitAsync(owner, Loc.T("DcMetrics_AddTypeTitle", type), MetricFields(type, null), values =>
        {
            var form = NonEmpty(MetricForm(values));
            form.Remove("id");
            form["type"] = type;
            return api.Metrics.CreateAsync(values["id"], form);
        }, "DcMetrics_Added", titleIsKey: false, validate: values => ValidateMetric(values));
    }

    private static async Task<string?> EditMetricAsync(ProxmoxApiClient api, string id, Window? owner)
    {
        var config = await api.Metrics.GetAsync(id);
        var type = config.TryGetValue("type", out var t) ? t : "influxdb";
        return await SubmitAsync(owner, Loc.T("DcMetrics_EditTitle", id), MetricFields(type, config), values =>
        {
            var form = MetricForm(values);
            if (form.TryGetValue("token", out var token) && token.Length == 0) form.Remove("token");
            return api.Metrics.UpdateAsync(id, UpdateForm(form));
        }, "DcMetrics_Updated", titleIsKey: false, validate: values => ValidateMetric(values, config));
    }

    private static List<FormField> MetricFields(string type, IReadOnlyDictionary<string, string>? config)
    {
        string V(string key, string fallback = "") =>
            config is null ? fallback : config.TryGetValue(key, out var v) ? v : string.Empty;
        FormField Text(string key, string labelKey, bool required = false, string fallback = "",
            string? hint = null, bool adv = false) =>
            new() { Key = key, LabelKey = labelKey, Required = required, Initial = V(key, fallback), Trim = true,
                Hint = hint is null ? null : Loc.T(hint), Advanced = adv };

        var fields = new List<FormField>();
        if (config is null) fields.Add(Text("id", "Table_Name", true));
        fields.AddRange(
        [
            Text("server", "DcStorage_Server", true),
            Text("port", "DcRealms_Port", true,
                type switch { "graphite" => "2003", "opentelemetry" => "4318", _ => "8089" }),
            new FormField { Key = "enable", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
                Initial = V("disable") is "1" ? "0" : "1" }
        ]);
        if (type == "opentelemetry")
            return [..fields, ..MetricOtel.Fields(config)];
        if (type is not ("graphite" or "influxdb"))
            return fields; // 이 앱이 모르는 유형 — 모르는 칸을 보내 거절당하지 않게 공통 칸만 고친다
        if (type == "graphite")
        {
            fields.Add(Text("path", "DcMetrics_Path", fallback: "proxmox"));
            fields.Add(new FormField { Key = "proto", LabelKey = "DcMetrics_Protocol", Kind = FormFieldKind.Choice,
                Initial = V("proto", "udp"), Choices = [("udp", "UDP"), ("tcp", "TCP")] });
        }
        else
        {
            fields.AddRange(
            [
                new FormField { Key = "influxdbproto", LabelKey = "DcMetrics_Protocol", Kind = FormFieldKind.Choice,
                    Initial = V("influxdbproto", "udp"),
                    Choices = [("udp", "UDP"), ("http", "HTTP"), ("https", "HTTPS")] },
                Text("organization", "DcMetrics_Organization", hint: "DcMetrics_HttpOnly"),
                Text("bucket", "DcMetrics_Bucket", hint: "DcMetrics_HttpOnly"),
                new FormField { Key = "token", LabelKey = "DcMetrics_Token", Kind = FormFieldKind.Password,
                    Hint = Loc.T(config is null ? "DcMetrics_HttpOnly" : "StorageHint_KeepSecret") },
                Text("api-path-prefix", "DcMetrics_ApiPrefix", adv: true),
                Text("max-body-size", "DcMetrics_MaxBody", hint: "DcMetrics_MaxBodyHint", adv: true),
                new FormField { Key = "verify-certificate", LabelKey = "DcRealms_Verify", Kind = FormFieldKind.Bool,
                    Initial = V("verify-certificate", "1") is "0" ? "0" : "1", Advanced = true }
            ]);
        }

        fields.Add(Text("mtu", "NodeNetwork_Mtu", hint: "DcMetrics_MtuHint", adv: true));
        fields.Add(Text("timeout", "DcMetrics_Timeout", hint: "DcMetrics_TimeoutHint", adv: true));
        return fields;
    }

    private static Dictionary<string, string> MetricForm(IReadOnlyDictionary<string, string> values)
    {
        var form = values.Where(kv => kv.Key != "enable")
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        form["disable"] = values["enable"] == "1" ? "0" : "1";
        return MetricOtel.Encode(form);
    }

    private static string? ValidateMetric(IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? original = null)
    {
        if (!(int.TryParse(values["port"], out var port) && port is >= 1 and <= 65535))
            return Loc.T("DcStorage_BadPort");
        foreach (var key in new[] { "timeout", "max-body-size" })
            if (values.TryGetValue(key, out var v) && v.Length > 0 && !(int.TryParse(v, out var n) && n > 0))
                return Loc.T("DcMetrics_BadNumber");
        if (values.TryGetValue("mtu", out var mtu) && mtu.Length > 0
            && !(int.TryParse(mtu, out var m) && m is >= 512 and <= 65536))
            return Loc.T("DcMetrics_BadMtu");
        return MetricOtel.Validate(values, original);
    }

    // ------------------------------------------------------------ 알림

    public static SubTabsView Notifications(ProxmoxApiClient api, bool canEdit)
    {
        return new SubTabsView(
        [
            ("DcNotify_TargetsTab", () => new TableTab(() => api.Notifications.ListTargetsAsync(),
                TargetColumns, "DcNotify_TargetsHint", NotificationActions.Targets(api, canEdit))),
            ("DcNotify_MatchersTab", () => new TableTab(() => api.Notifications.ListMatchersAsync(),
                MatcherColumns, "DcNotify_MatchersHint",
                [..(canEdit ? NotificationActions.Matchers(api) : []), NotificationActions.FieldValues(api)]))
        ]);
    }
}
