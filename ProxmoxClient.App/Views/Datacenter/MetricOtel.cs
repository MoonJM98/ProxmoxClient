using System.Text;
using System.Text.Json;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     OpenTelemetry 메트릭 서버(PVE 9, OTLP/HTTP) 칸 — otel-* 설정. 헤더·리소스 속성은 JSON 으로 적고
///     서버에는 base64 로 보낸다(API: "JSON format, base64 encoded", 최대 1024자).
/// </summary>
internal static class MetricOtel
{
    private const int MaxEncodedLength = 1024;
    private const int MinBodySize = 1024;
    private const int MaxTimeout = 10;

    private static readonly string[] JsonKeys = ["otel-headers", "otel-resource-attributes"];

    internal static IEnumerable<FormField> Fields(IReadOnlyDictionary<string, string>? config)
    {
        string V(string key, string fallback = "") =>
            config is null ? fallback : config.TryGetValue(key, out var v) ? v : string.Empty;

        yield return new FormField { Key = "otel-protocol", LabelKey = "DcMetrics_Protocol",
            Kind = FormFieldKind.Choice, Initial = V("otel-protocol", "https"),
            Choices = [("", "StorageField_Default"), ("http", "HTTP"), ("https", "HTTPS")] };
        yield return new FormField { Key = "otel-path", LabelKey = "DcMetrics_Path", Initial = V("otel-path"),
            Hint = Loc.T("DcMetrics_OtelPathHint") };
        yield return new FormField { Key = "otel-verify-ssl", LabelKey = "DcRealms_Verify", Kind = FormFieldKind.Bool,
            Initial = V("otel-verify-ssl", "1") is "0" ? "0" : "1" };
        yield return new FormField { Key = "otel-headers", LabelKey = "DcMetrics_OtelHeaders",
            Kind = FormFieldKind.Multiline, CanLoadFile = false, Initial = Decode(V("otel-headers")),
            Hint = Loc.T("DcMetrics_OtelJsonHint"), Advanced = true };
        yield return new FormField { Key = "otel-resource-attributes", LabelKey = "DcMetrics_OtelAttributes",
            Kind = FormFieldKind.Multiline, CanLoadFile = false, Initial = Decode(V("otel-resource-attributes")),
            Hint = Loc.T("DcMetrics_OtelJsonHint"), Advanced = true };
        yield return new FormField { Key = "otel-compression", LabelKey = "DcMetrics_OtelCompression",
            Kind = FormFieldKind.Choice, Initial = V("otel-compression"), Advanced = true,
            Choices = [("", "StorageField_Default"), ("gzip", "gzip"), ("none", "DcRealms_TfaNone")] };
        yield return new FormField { Key = "otel-timeout", LabelKey = "DcMetrics_Timeout", Initial = V("otel-timeout"),
            Hint = Loc.T("DcMetrics_OtelTimeoutHint"), Advanced = true };
        yield return new FormField { Key = "otel-max-body-size", LabelKey = "DcMetrics_MaxBody",
            Initial = V("otel-max-body-size"), Hint = Loc.T("DcMetrics_OtelBodyHint"), Advanced = true };
    }

    /// <summary>헤더·리소스 속성 JSON → base64(빈 값은 그대로 — 수정 때 delete 가 된다).</summary>
    internal static Dictionary<string, string> Encode(IReadOnlyDictionary<string, string> form)
    {
        return form.ToDictionary(kv => kv.Key,
            kv => JsonKeys.Contains(kv.Key) && kv.Value.Trim().Length > 0
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes(kv.Value.Trim()))
                : kv.Value,
            StringComparer.Ordinal);
    }

    /// <param name="original">수정 전 서버 값 — 손대지 않은 칸은 형식이 달라도 그대로 둔다.</param>
    internal static string? Validate(IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? original = null)
    {
        foreach (var key in JsonKeys)
        {
            if (!values.TryGetValue(key, out var json) || json.Trim().Length == 0) continue;
            if (original is not null && original.TryGetValue(key, out var raw) && Decode(raw).Trim() == json.Trim())
                continue;
            if (!IsJsonObject(json)) return Loc.T("DcMetrics_OtelBadJson");
            if (Encode(new Dictionary<string, string> { [key] = json })[key].Length > MaxEncodedLength)
                return Loc.T("DcMetrics_OtelTooLong");
        }

        if (values.TryGetValue("otel-timeout", out var t) && t.Length > 0
            && !(int.TryParse(t, out var timeout) && timeout is >= 1 and <= MaxTimeout))
            return Loc.T("DcMetrics_OtelTimeoutHint");
        if (values.TryGetValue("otel-max-body-size", out var b) && b.Length > 0
            && !(int.TryParse(b, out var body) && body >= MinBodySize))
            return Loc.T("DcMetrics_OtelBodyHint");
        return null;
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Decode(string raw)
    {
        try
        {
            return raw.Length == 0 ? raw : Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        }
        catch (FormatException)
        {
            return raw;
        }
    }
}
