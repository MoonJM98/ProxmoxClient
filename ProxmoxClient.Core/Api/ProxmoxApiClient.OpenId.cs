using System.Text.Json;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     OpenID Connect 로그인 — 서버에서 인증 기관(IdP) 로그인 주소를 받아 브라우저로 열고, IdP 가 리디렉션 주소로 돌려준
///     code·state 를 서버에 넘겨 티켓을 받는다. 받은 티켓은 비밀번호 로그인과 같은 방식(아이디 + 티켓)으로 갱신된다.
/// </summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>IdP 로그인 주소 — redirectUrl 은 IdP 에 등록된 주소여야 한다(로그인 뒤 여기로 code·state 가 온다).</summary>
    [Versioning.PveApi("POST", "/access/openid/auth-url")]
    public async Task<string> GetOpenIdAuthUrlAsync(string realm, string redirectUrl, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var data = await PostFormJsonAsync("access/openid/auth-url",
            new Dictionary<string, string> { ["realm"] = realm, ["redirect-url"] = redirectUrl }, false, ct)
            .ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.String ? data.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>
    ///     IdP 가 돌려준 code·state 로 로그인해 티켓을 받는다 — 성공하면 이 클라이언트는 로그인된 상태가 되고,
    ///     서버가 정한 아이디(user@realm)를 돌려준다(프로필 아이디도 그것으로 바꾼다 — 티켓 갱신에 필요).
    /// </summary>
    [Versioning.PveApi("POST", "/access/openid/login")]
    public async Task<string> CompleteOpenIdLoginAsync(string code, string state, string redirectUrl,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var data = await PostFormJsonAsync("access/openid/login",
            new Dictionary<string, string> { ["code"] = code, ["state"] = state, ["redirect-url"] = redirectUrl },
            false, ct).ConfigureAwait(false);
        var ticket = GetString(data, "ticket");
        var user = GetString(data, "username");
        if (ticket.Length == 0 || user.Length == 0) throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_03"));

        Profile.UserName = user;
        AuthenticatedUser = user;
        _auth = new AuthSession(ticket, GetString(data, "CSRFPreventionToken") is { Length: > 0 } csrf ? csrf : null,
            Environment.TickCount64);
        return user;
    }
}
