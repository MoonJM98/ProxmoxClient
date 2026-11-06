using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     PVE 2단계 인증 질문의 webauthn 항목({"publicKey":{challenge, timeout, rpId, allowCredentials, userVerification,
///     extensions{appid}}}) — 웹 UI(proxmox-widget-toolkit TfaWindow.js)가 navigator.credentials.get 에 넘기는 값이다.
///     이진 값(challenge·자격 증명 ID)은 base64url 문자열로 온다.
/// </summary>
public sealed record WebAuthnRequest
{
    /// <summary>서버가 준 challenge 문자열(base64url) 그대로 — clientDataJSON 과 응답의 challenge 에 이 문자열을 쓴다.</summary>
    public required string Challenge { get; init; }

    /// <summary>RP ID(보통 PVE 호스트 이름). 질문에 없으면 null(접속 주소의 호스트를 쓴다).</summary>
    public string? RpId { get; init; }

    /// <summary>제한 시간(밀리초). 질문에 없으면 60초.</summary>
    public int TimeoutMs { get; init; } = 60000;

    /// <summary>등록된 보안 키 자격 증명 ID 목록.</summary>
    public IReadOnlyList<byte[]> AllowCredentials { get; init; } = [];

    /// <summary>required · preferred · discouraged(없으면 preferred).</summary>
    public string UserVerification { get; init; } = "preferred";

    /// <summary>U2F 시절 등록한 키용 appid 확장(https://host:8006 등). 없으면 null.</summary>
    public string? AppId { get; init; }

    /// <summary>질문 원문(webauthn 항목 JSON)에서 읽는다. 형식이 다르면 <see cref="FormatException" />.</summary>
    public static WebAuthnRequest Parse(string webauthnJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(webauthnJson);
            var root = doc.RootElement;
            // {"publicKey":{…}} 또는 publicKey 그 자체
            var pk = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("publicKey", out var inner)
                ? inner
                : root;
            if (pk.ValueKind != JsonValueKind.Object ||
                !pk.TryGetProperty("challenge", out var ch) || ch.ValueKind != JsonValueKind.String ||
                ch.GetString() is not { Length: > 0 } challenge)
                throw new FormatException(Res.T("Tfa_BadChallenge"));

            Base64Url.Decode(challenge); // 형식 확인
            var creds = new List<byte[]>();
            if (pk.TryGetProperty("allowCredentials", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var c in list.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("id", out var id) &&
                        id.ValueKind == JsonValueKind.String &&
                        (!c.TryGetProperty("type", out var type) || type.GetString() == "public-key"))
                        creds.Add(Base64Url.Decode(id.GetString()!));

            string? appId = null;
            if (pk.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object &&
                ext.TryGetProperty("appid", out var app) && app.ValueKind == JsonValueKind.String)
                appId = app.GetString();

            return new WebAuthnRequest
            {
                Challenge = challenge,
                RpId = pk.TryGetProperty("rpId", out var rp) && rp.ValueKind == JsonValueKind.String
                    ? rp.GetString()
                    : null,
                TimeoutMs = pk.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number &&
                            t.TryGetInt32(out var ms) && ms > 0
                    ? ms
                    : 60000,
                AllowCredentials = creds,
                UserVerification = pk.TryGetProperty("userVerification", out var uv) &&
                                   uv.ValueKind == JsonValueKind.String
                    ? uv.GetString()!
                    : "preferred",
                AppId = string.IsNullOrEmpty(appId) ? null : appId
            };
        }
        catch (JsonException ex)
        {
            throw new FormatException(Res.T("Tfa_BadChallenge"), ex);
        }
    }

    /// <summary>쓸 RP ID — 질문에 없으면 origin 의 호스트(브라우저 기본값과 같다).</summary>
    public string EffectiveRpId(Uri origin)
    {
        return RpId is { Length: > 0 } rp ? rp : origin.IdnHost;
    }

    /// <summary>
    ///     clientDataJSON(UTF-8) — 브라우저가 만드는 것과 같은 모양:
    ///     {"type":"webauthn.get","challenge":"…","origin":"https://host:8006","crossOrigin":false}.
    ///     PVE 는 origin 을 datacenter.cfg 의 webauthn origin(없으면 "https://" + 요청 Host 헤더)과 비교한다.
    /// </summary>
    public byte[] BuildClientDataJson(Uri origin)
    {
        var json = "{\"type\":\"webauthn.get\",\"challenge\":" + JsonSerializer.Serialize(Challenge) +
                   ",\"origin\":" + JsonSerializer.Serialize(OriginString(origin)) + ",\"crossOrigin\":false}";
        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>"https://host[:port]" — 443 이면 포트를 뺀다(브라우저 origin·Host 헤더와 같다). IPv6 는 [..].</summary>
    public static string OriginString(Uri origin)
    {
        return $"{origin.Scheme}://{origin.Authority}";
    }

    /// <summary>RP ID·appid 의 SHA-256(인증기 데이터 앞 32바이트, U2F 의 application parameter).</summary>
    public static byte[] RpIdHash(string rpIdOrAppId)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(rpIdOrAppId));
    }
}

/// <summary>보안 키가 돌려준 서명(navigator.credentials.get 결과의 이진 값).</summary>
public sealed record WebAuthnAssertion(
    byte[] CredentialId,
    byte[] AuthenticatorData,
    byte[] ClientDataJson,
    byte[] Signature,
    byte[]? UserHandle = null)
{
    /// <summary>
    ///     2단계 응답 password 칸의 "webauthn:" 뒤 JSON — PVE 웹 UI(TfaWindow.js loginWebauthn)와 같은 모양·순서:
    ///     {"id","type":"public-key","challenge"(서버가 준 문자열),"rawId","response":{"authenticatorData","clientDataJSON","signature"}}.
    ///     이진 값은 base64url(패딩 없음).
    /// </summary>
    public string ToPveResponse(string challenge)
    {
        var id = Base64Url.Encode(CredentialId);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("type", "public-key");
            w.WriteString("challenge", challenge);
            w.WriteString("rawId", id);
            w.WriteStartObject("response");
            w.WriteString("authenticatorData", Base64Url.Encode(AuthenticatorData));
            w.WriteString("clientDataJSON", Base64Url.Encode(ClientDataJson));
            w.WriteString("signature", Base64Url.Encode(Signature));
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>base64url(RFC 4648 §5, 패딩 없음) — PVE Proxmox.Utils.bytes_to_base64url 과 같다.</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>패딩이 있어도 없어도 읽는다. 표준 base64(+,/)도 받는다. 잘못되면 <see cref="FormatException" />.</summary>
    public static byte[] Decode(string text)
    {
        var s = text.Trim().TrimEnd('=').Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 1 => throw new FormatException("base64url"), _ => "" };
        return Convert.FromBase64String(s);
    }
}
