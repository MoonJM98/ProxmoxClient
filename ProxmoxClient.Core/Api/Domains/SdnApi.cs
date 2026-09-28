using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     SDN — 영역(zones)·가상 네트워크(vnets)·서브넷·컨트롤러·IPAM·DNS, 패브릭(9.0+), 적용.
///     컬렉션(zones·vnets·controllers·ipams·dns)은 같은 모양이라 이름으로 고른다. 서버가 모르는 파라미터
///     (예: 8.1 전의 dhcp, 8.3 전의 fingerprint)는 보내지 않는다.
/// </summary>
public sealed class SdnApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private const string Fabrics = "9.0";

    private static readonly HashSet<string> Collections = ["zones", "vnets", "controllers", "ipams", "dns"];

    /// <summary>바꾼 설정을 노드에 적용한다(작업 UPID).</summary>
    [PveApi("PUT", "/cluster/sdn")]
    public Task<string> ApplyAsync(CancellationToken ct = default)
    {
        return Api.PutActionAsync("cluster/sdn", new Dictionary<string, string>(), ct);
    }

    /// <param name="pending">적용 전 변경(pending)도 함께.</param>
    [PveApi("GET", "/cluster/sdn/zones")]
    [PveApi("GET", "/cluster/sdn/vnets")]
    [PveApi("GET", "/cluster/sdn/controllers")]
    [PveApi("GET", "/cluster/sdn/ipams")]
    [PveApi("GET", "/cluster/sdn/dns")]
    public Task<IReadOnlyList<Row>> ListAsync(string collection, bool pending = false, CancellationToken ct = default)
    {
        return Api.GetTableAsync(Path(collection) + (pending ? "?pending=1" : string.Empty), ct);
    }

    [PveApi("GET", "/cluster/sdn/zones/{zone}")]
    [PveApi("GET", "/cluster/sdn/vnets/{vnet}")]
    [PveApi("GET", "/cluster/sdn/controllers/{controller}")]
    [PveApi("GET", "/cluster/sdn/ipams/{ipam}")]
    [PveApi("GET", "/cluster/sdn/dns/{dns}")]
    public Task<Row> GetAsync(string collection, string id, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"{Path(collection)}/{Seg(id)}", ct);
    }

    [PveApi("POST", "/cluster/sdn/zones")]
    [PveApi("POST", "/cluster/sdn/vnets")]
    [PveApi("POST", "/cluster/sdn/controllers")]
    [PveApi("POST", "/cluster/sdn/ipams")]
    [PveApi("POST", "/cluster/sdn/dns")]
    [PveParam("advertise-subnets", "7.1")]
    [PveParam("disable-arp-nd-suppression", "7.1")]
    [PveParam("exitnodes-local-routing", "7.1")]
    [PveParam("exitnodes-primary", "7.1")]
    [PveParam("rt-import", "7.1")]
    [PveParam("bridge-disable-mac-learning", "7.1")]
    [PveParam("bgp-multipath-as-path-relax", "7.1")]
    [PveParam("vxlan-port", "8.0")]
    [PveParam("dhcp", "8.1")]
    [PveParam("isis-domain", "8.1")]
    [PveParam("isis-ifaces", "8.1")]
    [PveParam("isis-net", "8.1")]
    [PveParam("isolate-ports", "8.2")]
    [PveParam("fingerprint", "8.3")]
    [PveParam("fabric", "9.0")]
    public Task<string> CreateAsync(string collection, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync(Path(collection), Supported(form), ct);
    }

    [PveApi("PUT", "/cluster/sdn/zones/{zone}")]
    [PveApi("PUT", "/cluster/sdn/vnets/{vnet}")]
    [PveApi("PUT", "/cluster/sdn/controllers/{controller}")]
    [PveApi("PUT", "/cluster/sdn/ipams/{ipam}")]
    [PveApi("PUT", "/cluster/sdn/dns/{dns}")]
    [PveParam("advertise-subnets", "7.1")]
    [PveParam("disable-arp-nd-suppression", "7.1")]
    [PveParam("exitnodes-local-routing", "7.1")]
    [PveParam("exitnodes-primary", "7.1")]
    [PveParam("rt-import", "7.1")]
    [PveParam("bridge-disable-mac-learning", "7.1")]
    [PveParam("bgp-multipath-as-path-relax", "7.1")]
    [PveParam("vxlan-port", "8.0")]
    [PveParam("dhcp", "8.1")]
    [PveParam("isis-domain", "8.1")]
    [PveParam("isis-ifaces", "8.1")]
    [PveParam("isis-net", "8.1")]
    [PveParam("isolate-ports", "8.2")]
    [PveParam("fingerprint", "8.3")]
    [PveParam("fabric", "9.0")]
    public Task<string> UpdateAsync(string collection, string id, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{Path(collection)}/{Seg(id)}", Supported(form), ct);
    }

    [PveApi("DELETE", "/cluster/sdn/zones/{zone}")]
    [PveApi("DELETE", "/cluster/sdn/vnets/{vnet}")]
    [PveApi("DELETE", "/cluster/sdn/controllers/{controller}")]
    [PveApi("DELETE", "/cluster/sdn/ipams/{ipam}")]
    [PveApi("DELETE", "/cluster/sdn/dns/{dns}")]
    public Task<string> DeleteAsync(string collection, string id, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{Path(collection)}/{Seg(id)}", ct);
    }

    // ------------------------------------------------------------ 서브넷

    [PveApi("GET", "/cluster/sdn/vnets/{vnet}/subnets")]
    public Task<IReadOnlyList<Row>> ListSubnetsAsync(string vnet, bool pending = false, CancellationToken ct = default)
    {
        return Api.GetTableAsync(SubnetsPath(vnet) + (pending ? "?pending=1" : string.Empty), ct);
    }

    /// <summary>서브넷 하나 — dhcp-range 는 줄마다 한 범위(JSON 객체 한 줄).</summary>
    [PveApi("GET", "/cluster/sdn/vnets/{vnet}/subnets/{subnet}")]
    public Task<Row> GetSubnetAsync(string vnet, string subnet, CancellationToken ct = default)
    {
        return Api.GetConfigLinesAsync($"{SubnetsPath(vnet)}/{Seg(subnet)}", ct);
    }

    /// <summary>pairs: dhcp-range 는 범위마다 키를 반복한다. DHCP(8.1+)를 모르는 서버엔 그 키를 보내지 않는다.</summary>
    [PveApi("POST", "/cluster/sdn/vnets/{vnet}/subnets")]
    [PveParam("dhcp-range", "8.1")]
    [PveParam("dhcp-dns-server", "8.1")]
    public Task<string> CreateSubnetAsync(string vnet, IReadOnlyList<KeyValuePair<string, string>> pairs,
        CancellationToken ct = default)
    {
        return Api.SendPairsAsync(HttpMethod.Post, SubnetsPath(vnet), SupportedPairs(pairs), ct);
    }

    [PveApi("PUT", "/cluster/sdn/vnets/{vnet}/subnets/{subnet}")]
    [PveParam("dhcp-range", "8.1")]
    [PveParam("dhcp-dns-server", "8.1")]
    public Task<string> UpdateSubnetAsync(string vnet, string subnet, IReadOnlyList<KeyValuePair<string, string>> pairs,
        CancellationToken ct = default)
    {
        return Api.SendPairsAsync(HttpMethod.Put, $"{SubnetsPath(vnet)}/{Seg(subnet)}", SupportedPairs(pairs), ct);
    }

    [PveApi("DELETE", "/cluster/sdn/vnets/{vnet}/subnets/{subnet}")]
    public Task<string> DeleteSubnetAsync(string vnet, string subnet, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{SubnetsPath(vnet)}/{Seg(subnet)}", ct);
    }

    // ------------------------------------------------------------ 패브릭(9.0+)

    [PveApi("GET", "/cluster/sdn/fabrics/fabric", Since = Fabrics)]
    public async Task<IReadOnlyList<Row>> ListFabricsAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync("cluster/sdn/fabrics/fabric", ct).ConfigureAwait(false);
    }

    [PveApi("GET", "/cluster/sdn/fabrics/fabric/{id}", Since = Fabrics)]
    public async Task<Row> GetFabricAsync(string id, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetConfigLinesAsync($"cluster/sdn/fabrics/fabric/{Seg(id)}", ct).ConfigureAwait(false);
    }

    [PveApi("POST", "/cluster/sdn/fabrics/fabric", Since = Fabrics)]
    public async Task<string> CreateFabricAsync(IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PostActionAsync("cluster/sdn/fabrics/fabric", form, ct).ConfigureAwait(false);
    }

    /// <summary>pairs: 프로토콜 태그와, 비운 칸은 delete 반복.</summary>
    [PveApi("PUT", "/cluster/sdn/fabrics/fabric/{id}", Since = Fabrics)]
    public async Task<string> UpdateFabricAsync(string id, IReadOnlyList<KeyValuePair<string, string>> pairs,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.SendPairsAsync(HttpMethod.Put, $"cluster/sdn/fabrics/fabric/{Seg(id)}", pairs, ct)
            .ConfigureAwait(false);
    }

    [PveApi("DELETE", "/cluster/sdn/fabrics/fabric/{id}", Since = Fabrics)]
    public async Task<string> DeleteFabricAsync(string id, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync($"cluster/sdn/fabrics/fabric/{Seg(id)}", ct).ConfigureAwait(false);
    }

    [PveApi("GET", "/cluster/sdn/fabrics/node/{fabric_id}", Since = Fabrics)]
    public async Task<IReadOnlyList<Row>> ListFabricNodesAsync(string fabric, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync($"cluster/sdn/fabrics/node/{Seg(fabric)}", ct).ConfigureAwait(false);
    }

    [PveApi("GET", "/cluster/sdn/fabrics/node/{fabric_id}/{node_id}", Since = Fabrics)]
    public async Task<Row> GetFabricNodeAsync(string fabric, string node, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetConfigLinesAsync($"cluster/sdn/fabrics/node/{Seg(fabric)}/{Seg(node)}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>pairs: node_id·protocol·ip·ip6, interfaces 는 인터페이스마다 반복.</summary>
    [PveApi("POST", "/cluster/sdn/fabrics/node/{fabric_id}", Since = Fabrics)]
    public async Task<string> CreateFabricNodeAsync(string fabric, IReadOnlyList<KeyValuePair<string, string>> pairs,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.SendPairsAsync(HttpMethod.Post, $"cluster/sdn/fabrics/node/{Seg(fabric)}", pairs, ct)
            .ConfigureAwait(false);
    }

    [PveApi("PUT", "/cluster/sdn/fabrics/node/{fabric_id}/{node_id}", Since = Fabrics)]
    public async Task<string> UpdateFabricNodeAsync(string fabric, string node,
        IReadOnlyList<KeyValuePair<string, string>> pairs, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.SendPairsAsync(HttpMethod.Put, $"cluster/sdn/fabrics/node/{Seg(fabric)}/{Seg(node)}",
            pairs, ct).ConfigureAwait(false);
    }

    [PveApi("DELETE", "/cluster/sdn/fabrics/node/{fabric_id}/{node_id}", Since = Fabrics)]
    public async Task<string> DeleteFabricNodeAsync(string fabric, string node, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync($"cluster/sdn/fabrics/node/{Seg(fabric)}/{Seg(node)}", ct)
            .ConfigureAwait(false);
    }

    private static string Path(string collection)
    {
        return Collections.Contains(collection)
            ? $"cluster/sdn/{collection}"
            : throw new ArgumentException($"Unknown SDN collection '{collection}'", nameof(collection));
    }

    private static string SubnetsPath(string vnet)
    {
        return $"cluster/sdn/vnets/{Seg(vnet)}/subnets";
    }

    /// <summary>적용하지 않은 SDN 변경을 모두 버린다(9.0+) — 마지막으로 적용한 설정으로 돌아간다.</summary>
    [PveApi("POST", "/cluster/sdn/rollback", Since = "9.0")]
    public async Task<string> RollbackAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PostActionAsync("cluster/sdn/rollback", null, ct).ConfigureAwait(false);
    }

    /// <summary>적용하면 노드에서 바뀔 내용 미리 보기(9.1+, 보기 좋게 들여쓴 JSON) — node 를 비우면 요청받은 노드.</summary>
    [PveApi("GET", "/cluster/sdn/dry-run", Since = "9.1")]
    public async Task<string> DryRunJsonAsync(string? node = null, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        var query = string.IsNullOrEmpty(node) ? string.Empty : $"?node={Uri.EscapeDataString(node)}";
        return await Api.GetPrettyJsonAsync($"cluster/sdn/dry-run{query}", ct).ConfigureAwait(false);
    }

    /// <summary>PVE IPAM 이 나눠 준 주소(8.1+) — vnet·subnet·ip·mac·hostname·vmid 등.</summary>
    [PveApi("GET", "/cluster/sdn/ipams/{ipam}/status", Since = "8.1")]
    public async Task<IReadOnlyList<Row>> IpamStatusAsync(string ipam, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync($"cluster/sdn/ipams/{Seg(ipam)}/status", ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 노드별 SDN 상태(9.0)

    private async Task<IReadOnlyList<Row>> NodeSdnAsync(string node, string path, CancellationToken ct,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "")
    {
        await RequireAsync(ct, member).ConfigureAwait(false);
        return await Api.GetTableAsync($"nodes/{Seg(node)}/sdn/{path}", ct).ConfigureAwait(false);
    }

    /// <summary>영역의 브리지(VNet) — name·ports·vlan_filtering.</summary>
    [PveApi("GET", "/nodes/{node}/sdn/zones/{zone}/bridges", Since = "9.0")]
    public Task<IReadOnlyList<Row>> ZoneBridgesAsync(string node, string zone, CancellationToken ct = default)
    {
        return NodeSdnAsync(node, $"zones/{Seg(zone)}/bridges", ct);
    }

    /// <summary>EVPN 영역의 IP VRF(경로 표) — ip·protocol·metric·nexthops.</summary>
    [PveApi("GET", "/nodes/{node}/sdn/zones/{zone}/ip-vrf", Since = "9.0")]
    public Task<IReadOnlyList<Row>> ZoneIpVrfAsync(string node, string zone, CancellationToken ct = default)
    {
        return NodeSdnAsync(node, $"zones/{Seg(zone)}/ip-vrf", ct);
    }

    /// <summary>EVPN VNet 의 MAC VRF — mac·ip·nexthop.</summary>
    [PveApi("GET", "/nodes/{node}/sdn/vnets/{vnet}/mac-vrf", Since = "9.0")]
    public Task<IReadOnlyList<Row>> VnetMacVrfAsync(string node, string vnet, CancellationToken ct = default)
    {
        return NodeSdnAsync(node, $"vnets/{Seg(vnet)}/mac-vrf", ct);
    }

    /// <summary>패브릭 경로 — route·via.</summary>
    [PveApi("GET", "/nodes/{node}/sdn/fabrics/{fabric}/routes", Since = "9.0")]
    public Task<IReadOnlyList<Row>> FabricRoutesAsync(string node, string fabric, CancellationToken ct = default)
    {
        return NodeSdnAsync(node, $"fabrics/{Seg(fabric)}/routes", ct);
    }

    /// <summary>패브릭 이웃 — neighbor·status·uptime.</summary>
    [PveApi("GET", "/nodes/{node}/sdn/fabrics/{fabric}/neighbors", Since = "9.0")]
    public Task<IReadOnlyList<Row>> FabricNeighborsAsync(string node, string fabric, CancellationToken ct = default)
    {
        return NodeSdnAsync(node, $"fabrics/{Seg(fabric)}/neighbors", ct);
    }

    /// <summary>패브릭 인터페이스 — name·type·state.</summary>
    [PveApi("GET", "/nodes/{node}/sdn/fabrics/{fabric}/interfaces", Since = "9.0")]
    public Task<IReadOnlyList<Row>> FabricInterfacesAsync(string node, string fabric, CancellationToken ct = default)
    {
        return NodeSdnAsync(node, $"fabrics/{Seg(fabric)}/interfaces", ct);
    }

    // ------------------------------------------------------------ VNet IP 매핑(8.1 — PVE IPAM)

    private static Dictionary<string, string> IpForm(string zone, string ip, string? mac)
    {
        var form = new Dictionary<string, string> { ["zone"] = zone, ["ip"] = ip };
        if (!string.IsNullOrWhiteSpace(mac)) form["mac"] = mac.Trim();
        return form;
    }

    /// <summary>VNet 에 IP 를 직접 매핑한다(8.1+) — PVE IPAM 에 고정 주소를 적어 둔다.</summary>
    [PveApi("POST", "/cluster/sdn/vnets/{vnet}/ips", Since = "8.1")]
    public async Task<string> CreateVnetIpAsync(string vnet, string zone, string ip, string? mac,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PostActionAsync($"cluster/sdn/vnets/{Seg(vnet)}/ips", IpForm(zone, ip, mac), ct)
            .ConfigureAwait(false);
    }

    /// <summary>VNet IP 매핑을 지운다(8.1+).</summary>
    [PveApi("DELETE", "/cluster/sdn/vnets/{vnet}/ips", Since = "8.1")]
    public async Task<string> DeleteVnetIpAsync(string vnet, string zone, string ip, string? mac,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        var query = string.Join('&', IpForm(zone, ip, mac).Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return await Api.DeleteActionAsync($"cluster/sdn/vnets/{Seg(vnet)}/ips?{query}", ct).ConfigureAwait(false);
    }
}
