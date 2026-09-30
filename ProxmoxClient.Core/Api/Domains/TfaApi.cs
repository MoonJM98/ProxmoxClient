using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     2단계 인증 항목(PVE 7.1+ 의 access/tfa — 사용자마다 여러 항목). 7.0 은 옛 방식(사용자 하나에 하나)이라
///     이 화면을 쓰지 않는다. 서버는 root@pam 이 아니면 쓰기마다 내 암호(password)를 요구한다.
/// </summary>
public sealed class TfaApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    /// <summary>모든 사용자의 항목을 한 행씩(사용자 필드 userid 를 함께 담는다).</summary>
    [PveApi("GET", "/access/tfa", Since = "7.1")]
    public async Task<IReadOnlyList<Row>> ListAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetFlattenedTableAsync("access/tfa", "entries", ct).ConfigureAwait(false);
    }

    /// <summary>항목 추가 — type(totp·yubico·recovery·webauthn) 과 값. 복구 코드는 응답(recovery)으로 한 번만 받는다.</summary>
    [PveApi("POST", "/access/tfa/{userid}", Since = "7.1")]
    public async Task<Row> AddAsync(string userId, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.SendForObjectAsync(HttpMethod.Post, $"access/tfa/{Seg(userId)}", form, ct)
            .ConfigureAwait(false);
    }

    /// <summary>설명·사용 여부를 바꾼다.</summary>
    [PveApi("PUT", "/access/tfa/{userid}/{id}", Since = "7.1")]
    public async Task<string> UpdateAsync(string userId, string id, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PutActionAsync(EntryPath(userId, id), form, ct).ConfigureAwait(false);
    }

    /// <summary>DELETE 는 본문이 없어 내 암호를 웹 UI 처럼 주소(HTTPS)에 싣는다.</summary>
    [PveApi("DELETE", "/access/tfa/{userid}/{id}", Since = "7.1")]
    public async Task<string> DeleteAsync(string userId, string id, string? password, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        var path = EntryPath(userId, id);
        return await Api.DeleteActionAsync(
            string.IsNullOrEmpty(password) ? path : $"{path}?password={Uri.EscapeDataString(password)}", ct)
            .ConfigureAwait(false);
    }

    private static string EntryPath(string userId, string id)
    {
        return $"access/tfa/{Seg(userId)}/{Seg(id)}";
    }
}
