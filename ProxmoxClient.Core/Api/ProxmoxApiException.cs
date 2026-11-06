using System.Text.Json;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     Thrown when a Proxmox API request fails: non-2xx HTTP responses, malformed
///     payloads, or pre-flight problems (missing login/profile data).
/// </summary>
public sealed class ProxmoxApiException : Exception
{
    private const int ExcerptLength = 300;
    private static readonly IReadOnlyDictionary<string, string> NoFieldErrors =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Creates an exception from an HTTP failure (status + body excerpt).</summary>
    public ProxmoxApiException(int statusCode, string? bodyExcerpt, string? message = null)
        : base(message ?? BuildMessage(statusCode, bodyExcerpt))
    {
        StatusCode = statusCode;
        ResponseExcerpt = bodyExcerpt;
    }

    /// <summary>Creates a client-side (pre-flight) exception with a plain message.</summary>
    public ProxmoxApiException(string message)
        : base(message)
    {
        StatusCode = 0;
    }

    private ProxmoxApiException(int statusCode, string? bodyExcerpt, string? reasonPhrase,
        IReadOnlyDictionary<string, string> fieldErrors, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseExcerpt = bodyExcerpt;
        ReasonPhrase = reasonPhrase;
        FieldErrors = fieldErrors;
    }

    /// <summary>HTTP status code; 0/null for client-side (pre-flight) failures.</summary>
    public int StatusCode { get; }

    /// <summary>First ~300 characters of the response body, when available.</summary>
    public string? ResponseExcerpt { get; }

    /// <summary>HTTP 상태 줄의 사유 문구 — pveproxy 는 오류 내용("VM 100 already running")을 여기에 담는다.</summary>
    public string? ReasonPhrase { get; }

    /// <summary>매개변수 검증 실패(400)의 칸별 오류(본문 "errors" 객체). 없으면 빈 사전.</summary>
    public IReadOnlyDictionary<string, string> FieldErrors { get; } = NoFieldErrors;

    /// <summary>
    ///     실패 응답에서 예외를 만든다. 메시지는 사유 문구와 칸별 오류로 짓고, 둘 다 없을 때만 본문 발췌를 쓴다
    ///     (pveproxy 의 본문은 대개 {"data":null} 뿐이라 그대로 보이면 쓸모가 없다).
    /// </summary>
    public static ProxmoxApiException FromResponse(int statusCode, string? reasonPhrase, string? body)
    {
        var excerpt = body is { Length: > 0 } ? Truncate(body, ExcerptLength) : null;
        var errors = ParseFieldErrors(body);
        var reason = string.IsNullOrWhiteSpace(reasonPhrase) ? null : reasonPhrase.Trim();
        var parts = new List<string>();
        // 칸별 오류가 있으면 일반 문구("Parameter verification failed.")는 앞에 한 번만 둔다
        if (reason is not null) parts.Add(reason);
        foreach (var (field, text) in errors) parts.Add($"{field}: {text}");
        if (parts.Count == 0 && excerpt is not null && !IsEmptyEnvelope(body!)) parts.Add(excerpt);

        var message = Res.T("ProxmoxApiException_01", statusCode, string.Join(" — ", parts)).Trim();
        return new ProxmoxApiException(statusCode, excerpt, reason, errors, message);
    }

    /// <summary>본문 {"errors": {"칸": "문구"}} → 사전. 형식이 다르면 빈 사전.</summary>
    private static IReadOnlyDictionary<string, string> ParseFieldErrors(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart() is not ['{', ..]) return NoFieldErrors;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("errors", out var errors)
                || errors.ValueKind != JsonValueKind.Object)
                return NoFieldErrors;

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in errors.EnumerateObject())
            {
                var text = field.Value.ValueKind == JsonValueKind.String
                    ? field.Value.GetString() ?? string.Empty
                    : field.Value.GetRawText();
                result[field.Name] = text.Trim();
            }

            return result;
        }
        catch (JsonException)
        {
            return NoFieldErrors;
        }
    }

    /// <summary>{"data":null} 처럼 알려 주는 것이 없는 본문.</summary>
    private static bool IsEmptyEnvelope(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            foreach (var property in root.EnumerateObject())
                if (property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    return false;

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildMessage(int statusCode, string? bodyExcerpt)
    {
        var excerpt = bodyExcerpt is { Length: > 0 }
            ? Truncate(bodyExcerpt, ExcerptLength)
            : string.Empty;
        return Res.T("ProxmoxApiException_01", statusCode, excerpt).Trim();
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
