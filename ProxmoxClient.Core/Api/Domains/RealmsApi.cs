using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     인증 영역(access/domains)과 동기화. 버전 차이:
///     OpenID acr-values·prompt·scopes(7.1+), check-connection(8.1+), OpenID 그룹 설정(8.3+), audiences(9.2+),
///     동기화 remove-vanished(7.1+), 동기화 작업(cluster/jobs/realm-sync, 8.0+).
///     서버가 모르는 파라미터는 보내지 않는다(요청 전체가 거절되지 않게).
/// </summary>
public sealed class RealmsApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private const string Jobs = "cluster/jobs/realm-sync";

    [PveApi("GET", "/access/domains")]
    public Task<IReadOnlyList<Row>> ListAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("access/domains", ct);
    }

    [PveApi("GET", "/access/domains/{realm}")]
    public Task<Row> GetAsync(string realm, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"access/domains/{Seg(realm)}", ct);
    }

    [PveApi("POST", "/access/domains")]
    [PveParam("acr-values", "7.1")]
    [PveParam("prompt", "7.1")]
    [PveParam("scopes", "7.1")]
    [PveParam("check-connection", "8.1")]
    [PveParam("groups-autocreate", "8.3")]
    [PveParam("groups-claim", "8.3")]
    [PveParam("groups-overwrite", "8.3")]
    [PveParam("query-userinfo", "8.3")]
    [PveParam("audiences", "9.2")]
    public Task<string> CreateAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("access/domains", Supported(form), ct);
    }

    [PveApi("PUT", "/access/domains/{realm}")]
    [PveParam("acr-values", "7.1")]
    [PveParam("prompt", "7.1")]
    [PveParam("scopes", "7.1")]
    [PveParam("check-connection", "8.1")]
    [PveParam("groups-autocreate", "8.3")]
    [PveParam("groups-claim", "8.3")]
    [PveParam("groups-overwrite", "8.3")]
    [PveParam("query-userinfo", "8.3")]
    [PveParam("audiences", "9.2")]
    public Task<string> UpdateAsync(string realm, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"access/domains/{Seg(realm)}", Supported(form), ct);
    }

    [PveApi("DELETE", "/access/domains/{realm}")]
    public Task<string> DeleteAsync(string realm, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"access/domains/{Seg(realm)}", ct);
    }

    /// <summary>LDAP·AD 동기화를 지금 실행한다(작업 UPID).</summary>
    [PveApi("POST", "/access/domains/{realm}/sync")]
    [PveParam("remove-vanished", "7.1")]
    public Task<string> SyncAsync(string realm, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"access/domains/{Seg(realm)}/sync", Supported(form), ct);
    }

    // ------------------------------------------------------------ 동기화 작업(8.0+)

    [PveApi("GET", "/cluster/jobs/realm-sync", Since = "8.0")]
    public async Task<IReadOnlyList<Row>> ListSyncJobsAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync(Jobs, ct).ConfigureAwait(false);
    }

    [PveApi("GET", "/cluster/jobs/realm-sync/{id}", Since = "8.0")]
    public async Task<Row> GetSyncJobAsync(string id, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetObjectAsync($"{Jobs}/{Seg(id)}", ct).ConfigureAwait(false);
    }

    [PveApi("POST", "/cluster/jobs/realm-sync/{id}", Since = "8.0")]
    public async Task<string> CreateSyncJobAsync(string id, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PostActionAsync($"{Jobs}/{Seg(id)}", form, ct).ConfigureAwait(false);
    }

    [PveApi("PUT", "/cluster/jobs/realm-sync/{id}", Since = "8.0")]
    public async Task<string> UpdateSyncJobAsync(string id, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PutActionAsync($"{Jobs}/{Seg(id)}", form, ct).ConfigureAwait(false);
    }

    [PveApi("DELETE", "/cluster/jobs/realm-sync/{id}", Since = "8.0")]
    public async Task<string> DeleteSyncJobAsync(string id, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync($"{Jobs}/{Seg(id)}", ct).ConfigureAwait(false);
    }
}
