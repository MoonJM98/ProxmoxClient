using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     Thrown when a Proxmox API request fails: non-2xx HTTP responses, malformed
///     payloads, or pre-flight problems (missing login/profile data).
/// </summary>
public sealed class ProxmoxApiException : Exception
{
    private const int ExcerptLength = 300;

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

    /// <summary>HTTP status code; 0/null for client-side (pre-flight) failures.</summary>
    public int StatusCode { get; }

    /// <summary>First ~300 characters of the response body, when available.</summary>
    public string? ResponseExcerpt { get; }

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