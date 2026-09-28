using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     리소스 풀. PVE 8.1 부터 중첩 풀(a/b) 때문에 수정·삭제·구성원 조회가 PUT/DELETE/GET pools?poolid= 로 옮겨졌다.
///     그보다 낮은 서버에는 옛 경로 pools/{poolid} 를 쓴다(옛 경로는 9.x 에도 남아 있지만 중첩 풀을 모른다).
/// </summary>
public sealed class PoolsApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private const string NestedPools = "8.1";

    [PveApi("GET", "/pools")]
    public Task<IReadOnlyList<Row>> ListAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("pools", ct);
    }

    [PveApi("POST", "/pools")]
    public Task<string> CreateAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("pools", form, ct);
    }

    /// <summary>
    ///     설명·구성원(vms·storage·delete·allow-move)을 바꾼다. allow-move(다른 풀에서 옮겨 오기)는 8.1 부터라
    ///     그보다 낮은 서버에는 보내지 않는다(모르는 파라미터로 거절당하지 않게).
    /// </summary>
    [PveApi("PUT", "/pools", Since = NestedPools)]
    [PveApi("PUT", "/pools/{poolid}", Until = NestedPools)]
    [PveParam("allow-move", NestedPools)]
    public async Task<string> UpdateAsync(string poolId, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        var body = form.Where(kv => kv.Key != "poolid").ToDictionary(kv => kv.Key, kv => kv.Value);
        if (!await SupportsAsync(NestedPools, ct).ConfigureAwait(false))
        {
            body.Remove("allow-move");
            return await Api.PutActionAsync($"pools/{Seg(poolId)}", body, ct).ConfigureAwait(false);
        }

        body["poolid"] = poolId;
        return await Api.PutActionAsync("pools", body, ct).ConfigureAwait(false);
    }

    [PveApi("DELETE", "/pools", Since = NestedPools)]
    [PveApi("DELETE", "/pools/{poolid}", Until = NestedPools)]
    public async Task<string> DeleteAsync(string poolId, CancellationToken ct = default)
    {
        return await SupportsAsync(NestedPools, ct).ConfigureAwait(false)
            ? await Api.DeleteActionAsync($"pools?poolid={Uri.EscapeDataString(poolId)}", ct).ConfigureAwait(false)
            : await Api.DeleteActionAsync($"pools/{Seg(poolId)}", ct).ConfigureAwait(false);
    }

    /// <summary>풀 구성원(게스트·저장소) — 행마다 id·type·node·vmid·storage.</summary>
    [PveApi("GET", "/pools")]
    [PveParam("poolid", NestedPools)]
    [PveApi("GET", "/pools/{poolid}", Until = NestedPools)]
    public async Task<IReadOnlyList<Row>> MembersAsync(string poolId, CancellationToken ct = default)
    {
        return await SupportsAsync(NestedPools, ct).ConfigureAwait(false)
            ? await Api.GetFlattenedTableAsync($"pools?poolid={Uri.EscapeDataString(poolId)}", "members", ct)
                .ConfigureAwait(false)
            : await Api.GetArrayPropertyAsync($"pools/{Seg(poolId)}", "members", ct).ConfigureAwait(false);
    }
}
