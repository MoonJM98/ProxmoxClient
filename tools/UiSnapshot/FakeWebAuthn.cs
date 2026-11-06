using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Platform;

namespace UiSnapshot;

/// <summary>
///     스냅샷 검사용 보안 키(--target webauthn) — 실제 키 없이 2단계 인증 창 → Core 응답 조립 → 서버까지의 흐름을 본다.
///     처음 <see cref="CancelFirst" /> 번은 취소(null)를 돌려주고, 그다음은 가짜 서명(자격 증명 "fake-credential")을 준다.
/// </summary>
internal sealed class FakeWebAuthnProvider : IWebAuthnProvider
{
    public int CancelFirst { get; set; }
    public int Calls { get; private set; }
    public Uri? LastOrigin { get; private set; }

    public bool IsAvailable => true;
    public string UnavailableReason => string.Empty;
    public string PromptText => "fake key";

    public Task<string?> GetAssertionAsync(Uri serverOrigin, string publicKeyJson, CancellationToken ct)
    {
        Calls++;
        LastOrigin = serverOrigin;
        if (Calls <= CancelFirst) return Task.FromResult<string?>(null);

        var request = WebAuthnRequest.Parse(publicKeyJson);
        var authData = new byte[37];
        WebAuthnRequest.RpIdHash(request.EffectiveRpId(serverOrigin)).CopyTo(authData, 0);
        authData[32] = 0x01; // UP
        authData[36] = 0x07; // 서명 횟수
        var assertion = new WebAuthnAssertion(request.AllowCredentials[0], authData,
            request.BuildClientDataJson(serverOrigin), [0x30, 0x06, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01]);
        return Task.FromResult<string?>(assertion.ToPveResponse(request.Challenge));
    }
}
