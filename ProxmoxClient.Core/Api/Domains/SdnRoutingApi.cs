using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     SDN 라우팅 정책(9.1+) — 접두사 목록(prefix list)과 라우트 맵(route map). BGP·EVPN 에서 받아들이거나 내보낼 경로를
///     고른다. 바꾼 뒤에는 SDN 적용이 있어야 노드에 반영된다. 라우트 맵의 match·set 은 값 자체에 쉼표가 들어 있어
///     줄마다 키를 반복해 보낸다.
/// </summary>
public sealed class SdnRoutingApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private const string Since = "9.1";
    private const string Prefix = "cluster/sdn/prefix-lists";
    private const string RouteMaps = "cluster/sdn/route-maps/entries";

    // ------------------------------------------------------------ 접두사 목록

    [PveApi("GET", "/cluster/sdn/prefix-lists", Since = Since)]
    public async Task<IReadOnlyList<Row>> ListPrefixListsAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync($"{Prefix}?pending=1", ct).ConfigureAwait(false);
    }

    [PveApi("POST", "/cluster/sdn/prefix-lists", Since = Since)]
    public async Task<string> CreatePrefixListAsync(string id, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PostActionAsync(Prefix, new Dictionary<string, string> { ["id"] = id }, ct)
            .ConfigureAwait(false);
    }

    [PveApi("DELETE", "/cluster/sdn/prefix-lists/{id}", Since = Since)]
    public async Task<string> DeletePrefixListAsync(string id, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync($"{Prefix}/{Seg(id)}", ct).ConfigureAwait(false);
    }

    /// <summary>접두사 목록의 항목 — seq·action(permit·deny)·prefix·ge·le.</summary>
    [PveApi("GET", "/cluster/sdn/prefix-lists/{id}/entries", Since = Since)]
    public async Task<IReadOnlyList<Row>> ListPrefixEntriesAsync(string id, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync($"{Prefix}/{Seg(id)}/entries", ct).ConfigureAwait(false);
    }

    [PveApi("POST", "/cluster/sdn/prefix-lists/{id}/entries", Since = Since)]
    public async Task<string> CreatePrefixEntryAsync(string id, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PostActionAsync($"{Prefix}/{Seg(id)}/entries", form, ct).ConfigureAwait(false);
    }

    [PveApi("PUT", "/cluster/sdn/prefix-lists/{id}/entries/{url_seq}", Since = Since)]
    public async Task<string> UpdatePrefixEntryAsync(string id, string seq, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PutActionAsync($"{Prefix}/{Seg(id)}/entries/{Seg(seq)}", form, ct).ConfigureAwait(false);
    }

    [PveApi("DELETE", "/cluster/sdn/prefix-lists/{id}/entries/{url_seq}", Since = Since)]
    public async Task<string> DeletePrefixEntryAsync(string id, string seq, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync($"{Prefix}/{Seg(id)}/entries/{Seg(seq)}", ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 라우트 맵

    /// <summary>모든 라우트 맵 항목 — route-map-id·order·action·match·set·call·exit-action.</summary>
    [PveApi("GET", "/cluster/sdn/route-maps/entries", Since = Since)]
    public async Task<IReadOnlyList<Row>> ListRouteMapEntriesAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync($"{RouteMaps}?pending=1", ct).ConfigureAwait(false);
    }

    /// <param name="match">조건 한 줄씩(예: key=ip-address-prefix-list,value=mylist).</param>
    /// <param name="set">설정 한 줄씩(예: key=local-preference,value=200).</param>
    [PveApi("POST", "/cluster/sdn/route-maps/entries", Since = Since)]
    public async Task<string> CreateRouteMapEntryAsync(IReadOnlyDictionary<string, string> form,
        IReadOnlyList<string> match, IReadOnlyList<string> set, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.SendPairsAsync(HttpMethod.Post, RouteMaps, Pairs(form, match, set), ct).ConfigureAwait(false);
    }

    /// <param name="delete">비운 칸(서버 기본값으로) — match·set 을 모두 지우려면 그 이름을 넣는다.</param>
    [PveApi("PUT", "/cluster/sdn/route-maps/entries/{route-map-id}/entry/{order}", Since = Since)]
    public async Task<string> UpdateRouteMapEntryAsync(string mapId, string order,
        IReadOnlyDictionary<string, string> form, IReadOnlyList<string> match, IReadOnlyList<string> set,
        IReadOnlyList<string> delete, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        var pairs = Pairs(form, match, set);
        pairs.AddRange(delete.Select(d => new KeyValuePair<string, string>("delete", d)));
        return await Api.SendPairsAsync(HttpMethod.Put, $"{RouteMaps}/{Seg(mapId)}/entry/{Seg(order)}", pairs, ct)
            .ConfigureAwait(false);
    }

    [PveApi("DELETE", "/cluster/sdn/route-maps/entries/{route-map-id}/entry/{order}", Since = Since)]
    public async Task<string> DeleteRouteMapEntryAsync(string mapId, string order, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync($"{RouteMaps}/{Seg(mapId)}/entry/{Seg(order)}", ct).ConfigureAwait(false);
    }

    private static List<KeyValuePair<string, string>> Pairs(IReadOnlyDictionary<string, string> form,
        IReadOnlyList<string> match, IReadOnlyList<string> set)
    {
        var pairs = form.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();
        pairs.AddRange(match.Select(m => new KeyValuePair<string, string>("match", m)));
        pairs.AddRange(set.Select(s => new KeyValuePair<string, string>("set", s)));
        return pairs;
    }
}
