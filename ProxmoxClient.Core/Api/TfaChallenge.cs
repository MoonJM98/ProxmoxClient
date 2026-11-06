using System.Text.Json;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Api;

/// <summary>2단계 인증 방식 — 응답 앞에 붙는 이름(totp:123456)과 같다.</summary>
public enum TfaKind
{
    Totp,
    Recovery,
    Yubico,
    WebAuthn,
    U2f
}

/// <summary>
///     로그인 1단계(비밀번호) 뒤 서버가 보낸 2단계 인증 요구. PVE 7.2+ 는 NeedTFA=1 과 함께
///     "PVE:!tfa!{질문 JSON(':'·'%' 만 % 인코딩)}:{시각}::{서명}" 모양의 반쪽 티켓을 준다(pve-access-control
///     create_ticket_do / PVE::Ticket::assemble_rsa_ticket). 질문 JSON 은 쓸 수 있는 방식만 담는다 —
///     totp(true), recovery(남은 복구 키 번호 배열), yubico(true), webauthn({publicKey…}), u2f({challenge, keys}).
///     반쪽 티켓은 2단계 응답의 tfa-challenge 로만 쓰고 인증 상태로 두지 않는다.
/// </summary>
public sealed record TfaChallenge
{
    private const string ChallengePrefix = "PVE:!tfa!";

    /// <summary>2단계 응답에 그대로 돌려줄 반쪽 티켓(tfa-challenge). 옛 형식이면 비어 있다.</summary>
    public string HalfTicket { get; init; } = string.Empty;

    /// <summary>서버가 정한 사용자 ID(user@realm).</summary>
    public string UserName { get; init; } = string.Empty;

    public bool HasTotp { get; init; }

    /// <summary>복구 키가 하나라도 남았다.</summary>
    public bool HasRecovery => RecoveryKeyIds.Count > 0;

    /// <summary>남은 복구 키 번호(웹 UI 의 "ID 0, ID 2"). 복구 키가 없으면 빈 목록.</summary>
    public IReadOnlyList<int> RecoveryKeyIds { get; init; } = [];

    public bool HasYubico { get; init; }

    public bool HasWebAuthn => WebAuthnChallengeJson is not null;

    /// <summary>WebAuthn 질문(navigator.credentials.get 에 넘길 publicKey 를 담은 JSON 원문).</summary>
    public string? WebAuthnChallengeJson { get; init; }

    /// <summary>WebAuthn 질문을 읽은 값(challenge·rpId·자격 증명). 질문이 없거나 읽을 수 없으면 null.</summary>
    public WebAuthnRequest? WebAuthn
    {
        get
        {
            if (WebAuthnChallengeJson is null) return null;

            try
            {
                return WebAuthnRequest.Parse(WebAuthnChallengeJson);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    public bool HasU2f { get; init; }

    /// <summary>
    ///     PVE 7.2 이전 형식 — 질문 내용이 없고, 비밀번호와 함께 otp 칸에 코드를 넣어 다시 로그인한다(TOTP·Yubico 만).
    /// </summary>
    public bool IsLegacy { get; init; }

    /// <summary>코드로 입력하는 방식(TOTP → 복구 키 → Yubico 순). 보안 키(WebAuthn·U2F)는 빠진다 — <see cref="AvailableKinds" />.</summary>
    public IReadOnlyList<TfaKind> SupportedKinds
    {
        get
        {
            var kinds = new List<TfaKind>();
            if (HasTotp) kinds.Add(TfaKind.Totp);
            if (HasRecovery) kinds.Add(TfaKind.Recovery);
            if (HasYubico) kinds.Add(TfaKind.Yubico);
            return kinds;
        }
    }

    /// <summary>
    ///     이 기기에서 쓸 수 있는 방식 — 보안 키를 쓸 수 있고(<paramref name="securityKey" />) 서버가 WebAuthn 을 물었으면
    ///     WebAuthn 을 맨 앞에(PVE 웹 UI 와 같은 순서), 그다음 코드 방식.
    /// </summary>
    public IReadOnlyList<TfaKind> AvailableKinds(bool securityKey)
    {
        var kinds = new List<TfaKind>();
        if (securityKey && !IsLegacy && WebAuthn is not null) kinds.Add(TfaKind.WebAuthn);
        kinds.AddRange(SupportedKinds);
        return kinds;
    }

    /// <summary>쓸 수 있는 방식이 보안 키(WebAuthn·U2F)뿐이다.</summary>
    public bool OnlySecurityKey => SupportedKinds.Count == 0 && (HasWebAuthn || HasU2f);

    /// <summary>응답 앞에 붙이는 방식 이름(PVE TfaLoginWindow 와 같다).</summary>
    public static string WireName(TfaKind kind)
    {
        return kind switch
        {
            TfaKind.Totp => "totp",
            TfaKind.Recovery => "recovery",
            TfaKind.Yubico => "yubico",
            TfaKind.WebAuthn => "webauthn",
            TfaKind.U2f => "u2f",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    /// <summary>새 형식 반쪽 티켓인가("PVE:!tfa!" 로 시작).</summary>
    public static bool IsChallengeTicket(string? ticket)
    {
        return ticket?.StartsWith(ChallengePrefix, StringComparison.Ordinal) == true;
    }

    /// <summary>/access/ticket 응답이 2단계 인증을 기다리는가(NeedTFA 가 참이거나 반쪽 티켓).</summary>
    public static bool NeedsSecondFactor(JsonElement data, string? ticket)
    {
        if (IsChallengeTicket(ticket)) return true;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("NeedTFA", out var needTfa)) return false;

        return needTfa.ValueKind switch
        {
            JsonValueKind.Number => needTfa.TryGetInt32(out var flag) && flag != 0,
            JsonValueKind.String => needTfa.GetString() is { Length: > 0 } text && text != "0",
            JsonValueKind.True => true,
            _ => false
        };
    }

    /// <summary>새 형식 반쪽 티켓에서 질문을 읽는다. 형식이 다르면 <see cref="FormatException" />.</summary>
    public static TfaChallenge Parse(string ticket, string userName)
    {
        if (!IsChallengeTicket(ticket)) throw new FormatException(Res.T("Tfa_BadChallenge"));

        // 값 안의 ':' 는 %3A 로 인코딩되어 있어 첫 ':' 와 다음 ':' 사이가 통째로 데이터다
        var end = ticket.IndexOf(':', ChallengePrefix.Length);
        if (end < 0) throw new FormatException(Res.T("Tfa_BadChallenge"));

        var json = Uri.UnescapeDataString(ticket[ChallengePrefix.Length..end]);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException(Res.T("Tfa_BadChallenge"), ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException(Res.T("Tfa_BadChallenge"));

            return new TfaChallenge
            {
                HalfTicket = ticket,
                UserName = userName,
                HasTotp = IsTruthy(root, "totp"),
                RecoveryKeyIds = ReadRecovery(root),
                HasYubico = IsTruthy(root, "yubico"),
                WebAuthnChallengeJson = root.TryGetProperty("webauthn", out var wa) && wa.ValueKind == JsonValueKind.Object
                    ? wa.GetRawText()
                    : null,
                HasU2f = root.TryGetProperty("u2f", out var u2f) && u2f.ValueKind == JsonValueKind.Object
            };
        }
    }

    /// <summary>옛 형식(PVE 7.2 이전) — 질문 내용이 없으니 코드 입력(TOTP·Yubico 공용)만 둔다.</summary>
    public static TfaChallenge Legacy(string userName, bool hasU2f)
    {
        return new TfaChallenge { UserName = userName, HasTotp = !hasU2f, HasU2f = hasU2f, IsLegacy = true };
    }

    private static bool IsTruthy(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var n) && n != 0,
            JsonValueKind.String => value.GetString() is { Length: > 0 } s && s != "0",
            JsonValueKind.Object => true,
            _ => false
        };
    }

    /// <summary>남은 복구 키 번호 — 배열(신형). 참 값만 오면 번호를 모르니 [0] 으로 "있음"만 표시한다.</summary>
    private static IReadOnlyList<int> ReadRecovery(JsonElement root)
    {
        if (!root.TryGetProperty("recovery", out var value)) return [];

        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _))
                .Select(e => e.GetInt32())
                .ToList();

        return value.ValueKind == JsonValueKind.True ? [0] : [];
    }
}

/// <summary>
///     비밀번호는 맞았고 2단계 인증이 필요하다 — <see cref="Challenge" /> 를 보여 주고 코드를 받아
///     <see cref="ProxmoxApiClient.CompleteTfaAsync" /> 로 로그인을 마친다.
/// </summary>
public sealed class TfaRequiredException(TfaChallenge challenge)
    : Exception(Res.T("ProxmoxApiClient_NeedTfa"))
{
    public TfaChallenge Challenge { get; } = challenge;
}
