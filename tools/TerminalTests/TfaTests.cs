using System.Text.Json;
using ProxmoxClient.Core.Api;

/// <summary>2단계 인증 — /access/ticket 1단계 응답(실제 PVE 모양)에서 질문 읽기.</summary>
public static class TfaTests
{
    /// <summary>PVE 8/9 의 TOTP + 복구 키 사용자 응답 — 질문 JSON 은 ':' 와 '%' 만 % 인코딩된다.</summary>
    private const string TotpResponse =
        """{"username":"root@pam","NeedTFA":1,"CSRFPreventionToken":"6720A0B1:Zm9vYmFy","ticket":"PVE:!tfa!{\"totp\"%3Atrue,\"recovery\"%3A[0,1,4,7]}:6720A0B1::c2lnbmF0dXJlK3dpdGgvc2xhc2g9"}""";

    /// <summary>보안 키만 등록한 사용자.</summary>
    private const string WebAuthnResponse =
        """{"username":"alice@pve","NeedTFA":1,"ticket":"PVE:!tfa!{\"webauthn\"%3A{\"publicKey\"%3A{\"challenge\"%3A\"q7Vx-AbC\",\"timeout\"%3A60000,\"rpId\"%3A\"pve.example.com\",\"allowCredentials\"%3A[{\"type\"%3A\"public-key\",\"id\"%3A\"AQID\"}],\"userVerification\"%3A\"preferred\"}}}:6720A0B1::c2ln"}""";

    /// <summary>Yubico 영역 사용자(Perl 쪽이 만든 질문) — 복구 키 하나 남음.</summary>
    private const string YubicoResponse =
        """{"username":"bob@ldap","NeedTFA":"1","ticket":"PVE:!tfa!{\"recovery\"%3A[3],\"yubico\"%3Atrue}:6720A0B1::c2ln"}""";

    private static JsonElement Data(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static TfaChallenge Read(string json)
    {
        var data = Data(json);
        return TfaChallenge.Parse(data.GetProperty("ticket").GetString()!, data.GetProperty("username").GetString()!);
    }

    public static void ParsesTotpAndRecovery(Checker c)
    {
        var data = Data(TotpResponse);
        var ticket = data.GetProperty("ticket").GetString();
        c.Is(TfaChallenge.NeedsSecondFactor(data, ticket), "NeedTFA=1 이면 2단계 필요");
        var ch = Read(TotpResponse);
        c.Is(ch.HasTotp, "totp 있음");
        c.Is(ch.HasRecovery, "복구 키 있음");
        c.Equal("0,1,4,7", string.Join(",", ch.RecoveryKeyIds), "남은 복구 키 번호");
        c.Is(!ch.HasYubico && !ch.HasWebAuthn && !ch.HasU2f, "다른 방식 없음");
        c.Equal(ticket, ch.HalfTicket, "반쪽 티켓 원문 그대로 보관");
        c.Equal("root@pam", ch.UserName, "서버가 정한 사용자");
        c.Equal("Totp,Recovery", string.Join(",", ch.SupportedKinds), "앱에서 쓸 방식(TOTP 먼저)");
        c.Is(!ch.IsLegacy && !ch.OnlySecurityKey, "새 형식·보안 키 전용 아님");
    }

    public static void ParsesWebAuthnOnly(Checker c)
    {
        var ch = Read(WebAuthnResponse);
        c.Is(ch.HasWebAuthn, "webauthn 있음");
        c.Is(ch.SupportedKinds.Count == 0 && ch.OnlySecurityKey, "보안 키만 — 앱에서 입력할 방식 없음");
        using var doc = JsonDocument.Parse(ch.WebAuthnChallengeJson!);
        c.Equal("q7Vx-AbC", doc.RootElement.GetProperty("publicKey").GetProperty("challenge").GetString(),
            "WebAuthn 질문 JSON 이 풀린 그대로(':' 복원)");
    }

    public static void ParsesYubicoWithRecovery(Checker c)
    {
        var data = Data(YubicoResponse);
        c.Is(TfaChallenge.NeedsSecondFactor(data, data.GetProperty("ticket").GetString()), "NeedTFA 가 문자열 \"1\" 이어도");
        var ch = Read(YubicoResponse);
        c.Is(ch.HasYubico && !ch.HasTotp, "yubico 만");
        c.Equal("Recovery,Yubico", string.Join(",", ch.SupportedKinds), "복구 키·Yubico");
        c.Equal("3", string.Join(",", ch.RecoveryKeyIds), "복구 키 하나");
    }

    public static void NormalTicketIsNotTfa(Checker c)
    {
        var data = Data("""{"username":"root@pam","ticket":"PVE:root@pam:6720A0B1::c2ln","CSRFPreventionToken":"x","cap":{}}""");
        c.Is(!TfaChallenge.NeedsSecondFactor(data, data.GetProperty("ticket").GetString()), "보통 티켓은 2단계 아님");
        var zero = Data("""{"NeedTFA":0,"ticket":"PVE:root@pam:6720A0B1::c2ln"}""");
        c.Is(!TfaChallenge.NeedsSecondFactor(zero, "PVE:root@pam:6720A0B1::c2ln"), "NeedTFA=0");
        // 옛 형식(7.2 이전) — NeedTFA 만 있고 질문 내용은 없다
        var legacy = Data("""{"NeedTFA":1,"ticket":"PVE:tfa!root@pam:6720A0B1::c2ln","username":"root@pam"}""");
        c.Is(TfaChallenge.NeedsSecondFactor(legacy, "PVE:tfa!root@pam:6720A0B1::c2ln"), "옛 형식도 2단계로 본다");
        c.Is(!TfaChallenge.IsChallengeTicket("PVE:tfa!root@pam:6720A0B1::c2ln"), "옛 형식은 새 질문 티켓 아님");
        var old = TfaChallenge.Legacy("root@pam", false);
        c.Is(old.IsLegacy && old.SupportedKinds.SequenceEqual([TfaKind.Totp]), "옛 형식은 코드 입력만");
        c.Is(TfaChallenge.Legacy("root@pam", true).OnlySecurityKey, "옛 U2F 는 보안 키 전용");
    }

    public static void RejectsMalformedChallenge(Checker c)
    {
        var threw = false;
        try
        {
            TfaChallenge.Parse("PVE:!tfa!{not json:6720A0B1::c2ln", "x@pam");
        }
        catch (FormatException)
        {
            threw = true;
        }

        c.Is(threw, "깨진 질문은 FormatException");
    }

    public static void NormalizesResponses(Checker c)
    {
        c.Equal("123456", ProxmoxApiClient.NormalizeTfaResponse(TfaKind.Totp, " 123 456 "), "TOTP 공백 제거");
        c.Equal("1234-5678-9abc-def0",
            ProxmoxApiClient.NormalizeTfaResponse(TfaKind.Recovery, " 1234-5678-9ABC-DEF0 "), "복구 키 소문자·하이픈 유지");
        c.Equal("ccccccabcdef", ProxmoxApiClient.NormalizeTfaResponse(TfaKind.Yubico, "ccccccabcdef\n"), "Yubico 앞뒤 공백만");
        c.Equal("totp", TfaChallenge.WireName(TfaKind.Totp), "응답 앞 이름 totp");
        c.Equal("recovery", TfaChallenge.WireName(TfaKind.Recovery), "응답 앞 이름 recovery");
        c.Equal("webauthn", TfaChallenge.WireName(TfaKind.WebAuthn), "응답 앞 이름 webauthn");
    }
}
