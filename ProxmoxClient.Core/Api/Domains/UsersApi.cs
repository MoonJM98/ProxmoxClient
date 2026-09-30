using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     사용자·암호·실제 권한·API 토큰. 버전 차이:
///     unlock-tfa(8.0+), 암호 변경의 confirmation-password(8.1+), 토큰 수정의 delete(9.0+)·regenerate(9.1+).
/// </summary>
public sealed class UsersApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private const string TokenDelete = "9.0";
    private const string TokenRegenerate = "9.1";

    /// <param name="full">그룹·토큰까지 받는다(웹 UI 와 같다).</param>
    [PveApi("GET", "/access/users")]
    public Task<IReadOnlyList<Row>> ListAsync(bool full = false, CancellationToken ct = default)
    {
        return Api.GetTableAsync(full ? "access/users?full=1" : "access/users", ct);
    }

    [PveApi("GET", "/access/users/{userid}")]
    public Task<Row> GetAsync(string userId, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"access/users/{Seg(userId)}", ct);
    }

    [PveApi("POST", "/access/users")]
    public Task<string> CreateAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("access/users", form, ct);
    }

    /// <summary>이 API 에는 delete 가 없다 — 비운 칸은 빈 문자열로 보내 지운다(웹 UI 와 같다).</summary>
    [PveApi("PUT", "/access/users/{userid}")]
    public Task<string> UpdateAsync(string userId, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"access/users/{Seg(userId)}", form, ct);
    }

    [PveApi("DELETE", "/access/users/{userid}")]
    public Task<string> DeleteAsync(string userId, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"access/users/{Seg(userId)}", ct);
    }

    /// <summary>2단계 인증 실패로 잠긴 사용자를 푼다(8.0+).</summary>
    [PveApi("PUT", "/access/users/{userid}/unlock-tfa", Since = "8.0")]
    public async Task<string> UnlockTfaAsync(string userId, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PutActionAsync($"access/users/{Seg(userId)}/unlock-tfa", new Dictionary<string, string>(),
            ct).ConfigureAwait(false);
    }

    /// <summary>사용자·토큰의 실제 권한 — 경로 → { 권한: 1 }.</summary>
    [PveApi("GET", "/access/permissions")]
    public Task<Row> PermissionsAsync(string userOrTokenId, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"access/permissions?userid={Uri.EscapeDataString(userOrTokenId)}", ct);
    }

    /// <summary>암호 변경 — form: password, (8.1+) confirmation-password. 낮은 서버엔 확인 암호를 보내지 않는다.</summary>
    [PveApi("PUT", "/access/password")]
    [PveParam("confirmation-password", "8.1")]
    public Task<string> ChangePasswordAsync(string userId, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        var body = Supported(form);
        body["userid"] = userId;
        return Api.PutActionAsync("access/password", body, ct);
    }

    // ------------------------------------------------------------ API 토큰

    [PveApi("GET", "/access/users/{userid}/token")]
    public Task<IReadOnlyList<Row>> ListTokensAsync(string userId, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"access/users/{Seg(userId)}/token", ct);
    }

    /// <summary>만든 토큰 — full-tokenid 와 비밀 값(value). 비밀은 이때 한 번만 받는다.</summary>
    [PveApi("POST", "/access/users/{userid}/token/{tokenid}")]
    public Task<Row> CreateTokenAsync(string userId, string tokenId, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.SendForObjectAsync(HttpMethod.Post, TokenPath(userId, tokenId), form, ct);
    }

    /// <summary>
    ///     토큰 수정 — form 은 UpdateForm 형식(비운 칸은 delete). 9.0 전 서버는 delete 를 몰라서,
    ///     설명은 빈 값·만료는 0 으로 바꿔 보낸다.
    /// </summary>
    [PveApi("PUT", "/access/users/{userid}/token/{tokenid}")]
    [PveParam("delete", TokenDelete)]
    public async Task<string> UpdateTokenAsync(string userId, string tokenId, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        var body = await SupportsAsync(TokenDelete, ct).ConfigureAwait(false) ? Supported(form) : WithoutDelete(form);
        return await Api.PutActionAsync(TokenPath(userId, tokenId), body, ct).ConfigureAwait(false);
    }

    /// <summary>같은 토큰 ID·권한을 둔 채 비밀만 새로 받는다(9.1+). 새 비밀이 없으면 빈 문자열.</summary>
    [PveApi("PUT", "/access/users/{userid}/token/{tokenid}")]
    [PveParam("regenerate", TokenRegenerate)]
    public async Task<string> RegenerateTokenAsync(string userId, string tokenId, CancellationToken ct = default)
    {
        if (!await SupportsAsync(TokenRegenerate, ct).ConfigureAwait(false))
            throw new ProxmoxApiException(Localization.Res.T("Api_VersionRequired", TokenRegenerate,
                Api.ServerVersion));
        var result = await Api.SendForObjectAsync(HttpMethod.Put, TokenPath(userId, tokenId),
            new Dictionary<string, string> { ["regenerate"] = "1" }, ct).ConfigureAwait(false);
        return result.TryGetValue("value", out var secret) ? secret : string.Empty;
    }

    [PveApi("DELETE", "/access/users/{userid}/token/{tokenid}")]
    public Task<string> DeleteTokenAsync(string userId, string tokenId, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync(TokenPath(userId, tokenId), ct);
    }

    /// <summary>delete 를 모르는 서버용 — 지울 칸을 빈 값(설명)·0(만료)으로 바꾼다. 그 밖의 칸은 지우지 않는다.</summary>
    internal static Dictionary<string, string> WithoutDelete(IReadOnlyDictionary<string, string> form)
    {
        var body = form.Where(kv => kv.Key != "delete")
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (!form.TryGetValue("delete", out var delete)) return body;
        foreach (var key in delete.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (key == "comment") body["comment"] = string.Empty;
            else if (key == "expire") body["expire"] = "0";
        }

        return body;
    }

    private static string TokenPath(string userId, string tokenId)
    {
        return $"access/users/{Seg(userId)}/token/{Seg(tokenId)}";
    }
}
