using System.Net;
using System.Text.Json;
using ProxmoxClient.Core.Localization;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.Core.Api;

/// <summary>2단계 인증(TOTP·복구 키·Yubico) 로그인 — 1단계는 <see cref="LoginAsync" />, 2단계는 여기.</summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>티켓 요청의 목적 — 2단계 인증 요구를 어떻게 다룰지가 다르다.</summary>
    private enum TicketStep
    {
        /// <summary>비밀번호 로그인 — 2단계 요구는 <see cref="TfaRequiredException" />.</summary>
        Login,

        /// <summary>자동 갱신 — 코드를 물을 수 없으니 세션 만료.</summary>
        Renew,

        /// <summary>2단계 응답 — 또 요구하면 오류.</summary>
        SecondFactor
    }

    /// <summary>
    ///     2단계 인증 코드로 로그인을 마친다. 성공하면 보통 로그인과 똑같이 티켓·CSRF 토큰·사용자를 둔다.
    ///     새 형식: POST /access/ticket (username, tfa-challenge=반쪽 티켓, password=방식:값).
    ///     옛 형식(PVE 7.2 이전): 비밀번호와 함께 otp=코드 로 다시 로그인한다.
    ///     코드가 틀리면 401 <see cref="ProxmoxApiException" /> — 같은 질문으로 다시 시도할 수 있다.
    /// </summary>
    [Versioning.PveApi("POST", "/access/ticket")]
    public async Task CompleteTfaAsync(TfaChallenge challenge, TfaKind kind, string response,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ThrowIfDisposed();

        var value = NormalizeTfaResponse(kind, response);
        if (value.Length == 0) throw new ProxmoxApiException(0, null, Res.T("Tfa_EmptyCode"));

        Dictionary<string, string> form;
        if (challenge.IsLegacy)
        {
            if (Profile.Password is null) throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_02"));

            form = TicketForm(SecureStringHelper.ToPlainString(Profile.Password) ?? string.Empty);
            form["otp"] = value;
        }
        else
        {
            form = new Dictionary<string, string>
            {
                ["username"] = challenge.UserName.Length > 0 ? challenge.UserName : Profile.UserName,
                ["tfa-challenge"] = challenge.HalfTicket,
                ["password"] = $"{TfaChallenge.WireName(kind)}:{value}"
            };
        }

        await AcquireTicketAsync(form, TicketStep.SecondFactor, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     WebAuthn 의 origin — "https://" + 요청 Host 헤더(호스트[:포트]). PVE 는 datacenter.cfg 에 webauthn origin 이
    ///     없으면 이 값과 clientDataJSON 의 origin 을 비교한다(PVE::AccessControl 의 get_origin).
    /// </summary>
    public Uri ServerOrigin => new(WebAuthnRequest.OriginString(_http.BaseAddress!));

    /// <summary>
    ///     보안 키 서명으로 로그인을 마친다 — <paramref name="assertionJson" /> 은
    ///     <see cref="WebAuthnAssertion.ToPveResponse" /> 모양(웹 UI 와 같은 JSON). password=webauthn:{JSON}.
    /// </summary>
    public Task CompleteWebAuthnAsync(TfaChallenge challenge, string assertionJson, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (challenge.IsLegacy || !challenge.HasWebAuthn) throw new ProxmoxApiException(0, null, Res.T("Tfa_BadChallenge"));

        return CompleteTfaAsync(challenge, TfaKind.WebAuthn, assertionJson, ct);
    }

    /// <summary>입력값 정리 — TOTP 는 사이 공백("123 456")을 빼고, 복구 키는 소문자로(웹 UI 형식 xxxx-xxxx-xxxx-xxxx).</summary>
    public static string NormalizeTfaResponse(TfaKind kind, string? response)
    {
        var text = (response ?? string.Empty).Trim();
        return kind switch
        {
            TfaKind.Totp => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != '-')),
            TfaKind.Recovery => string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant(),
            _ => text
        };
    }

    /// <summary>
    ///     1단계 응답에서 2단계 질문을 읽는다. PVE 7.2~7.4 는 new-format 을 주지 않으면 옛 형식을 주므로
    ///     new-format=1 로 한 번 더 요청해 새 형식 질문을 받는다. 그 인자를 모르는 서버(7.2 이전, 400)면 옛 형식으로 다룬다.
    /// </summary>
    private async Task<TfaChallenge> ReadTfaChallengeAsync(JsonElement data, string ticket,
        Dictionary<string, string> form, CancellationToken ct)
    {
        var user = GetString(data, "username") is { Length: > 0 } u ? u : Profile.UserName;
        if (TfaChallenge.IsChallengeTicket(ticket)) return TfaChallenge.Parse(ticket, user);

        var retryForm = new Dictionary<string, string>(form) { ["new-format"] = "1" };
        try
        {
            var retry = await PostFormJsonAsync("access/ticket", retryForm, false, ct).ConfigureAwait(false);
            if (GetString(retry, "ticket") is { } retryTicket && TfaChallenge.IsChallengeTicket(retryTicket))
                return TfaChallenge.Parse(retryTicket,
                    GetString(retry, "username") is { Length: > 0 } ru ? ru : user);
        }
        catch (ProxmoxApiException ex) when (ex.StatusCode == (int)HttpStatusCode.BadRequest)
        {
            // new-format 을 모르는 옛 서버 — 아래 옛 형식으로
        }

        return TfaChallenge.Legacy(user, data.TryGetProperty("U2FChallenge", out _));
    }
}
