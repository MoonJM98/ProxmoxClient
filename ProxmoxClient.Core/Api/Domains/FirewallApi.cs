using ProxmoxClient.Core.Api.Versioning;
using ProxmoxClient.Core.Models;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     방화벽 — 범위(<see cref="FirewallScope" />: 데이터센터·노드·게스트·보안 그룹)마다 같은 모양이다.
///     노드에는 별칭·IP 집합이 없다. 버전 차이: IP 집합 강제 삭제(force, 7.2+),
///     옵션 nf_conntrack_helpers(7.4+)·nftables(8.2+)·policy_forward·log_level_forward(8.3+).
/// </summary>
public sealed class FirewallApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    [PveApi("GET", "/cluster/firewall/rules")]
    [PveApi("GET", "/cluster/firewall/groups/{group}")]
    [PveApi("GET", "/nodes/{node}/firewall/rules")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/firewall/rules")]
    [PveApi("GET", "/nodes/{node}/lxc/{vmid}/firewall/rules")]
    [PveApi("GET", "/cluster/sdn/vnets/{vnet}/firewall/rules", Since = "8.3")]
    public Task<IReadOnlyList<Row>> ListRulesAsync(FirewallScope scope, CancellationToken ct = default)
    {
        return Api.GetTableAsync(scope.RulesPath, ct);
    }

    [PveApi("POST", "/cluster/firewall/rules")]
    [PveApi("POST", "/cluster/firewall/groups/{group}")]
    [PveApi("POST", "/nodes/{node}/firewall/rules")]
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/firewall/rules")]
    [PveApi("POST", "/nodes/{node}/lxc/{vmid}/firewall/rules")]
    [PveApi("POST", "/cluster/sdn/vnets/{vnet}/firewall/rules", Since = "8.3")]
    public Task<string> CreateRuleAsync(FirewallScope scope, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync(scope.RulesPath, form, ct);
    }

    /// <summary>규칙 수정 — moveto 만 주면 자리를 옮긴다.</summary>
    [PveApi("PUT", "/cluster/firewall/rules/{pos}")]
    [PveApi("PUT", "/cluster/firewall/groups/{group}/{pos}")]
    [PveApi("PUT", "/nodes/{node}/firewall/rules/{pos}")]
    [PveApi("PUT", "/nodes/{node}/qemu/{vmid}/firewall/rules/{pos}")]
    [PveApi("PUT", "/nodes/{node}/lxc/{vmid}/firewall/rules/{pos}")]
    [PveApi("PUT", "/cluster/sdn/vnets/{vnet}/firewall/rules/{pos}", Since = "8.3")]
    public Task<string> UpdateRuleAsync(FirewallScope scope, string pos, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{scope.RulesPath}/{Seg(pos)}", form, ct);
    }

    [PveApi("DELETE", "/cluster/firewall/rules/{pos}")]
    [PveApi("DELETE", "/cluster/firewall/groups/{group}/{pos}")]
    [PveApi("DELETE", "/nodes/{node}/firewall/rules/{pos}")]
    [PveApi("DELETE", "/nodes/{node}/qemu/{vmid}/firewall/rules/{pos}")]
    [PveApi("DELETE", "/nodes/{node}/lxc/{vmid}/firewall/rules/{pos}")]
    [PveApi("DELETE", "/cluster/sdn/vnets/{vnet}/firewall/rules/{pos}", Since = "8.3")]
    public Task<string> DeleteRuleAsync(FirewallScope scope, string pos, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{scope.RulesPath}/{Seg(pos)}", ct);
    }

    [PveApi("GET", "/cluster/firewall/options")]
    [PveApi("GET", "/nodes/{node}/firewall/options")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/firewall/options")]
    [PveApi("GET", "/nodes/{node}/lxc/{vmid}/firewall/options")]
    [PveApi("GET", "/cluster/sdn/vnets/{vnet}/firewall/options", Since = "8.3")]
    public Task<Row> GetOptionsAsync(FirewallScope scope, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"{scope.BasePath}/options", ct);
    }

    [PveApi("PUT", "/cluster/firewall/options")]
    [PveApi("PUT", "/nodes/{node}/firewall/options")]
    [PveApi("PUT", "/nodes/{node}/qemu/{vmid}/firewall/options")]
    [PveApi("PUT", "/nodes/{node}/lxc/{vmid}/firewall/options")]
    [PveApi("PUT", "/cluster/sdn/vnets/{vnet}/firewall/options", Since = "8.3")]
    [PveParam("nf_conntrack_helpers", "7.4")]
    [PveParam("nftables", "8.2")]
    [PveParam("policy_forward", "8.3")]
    [PveParam("log_level_forward", "8.3")]
    public Task<string> UpdateOptionsAsync(FirewallScope scope, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{scope.BasePath}/options", Supported(form), ct);
    }

    [PveApi("GET", "/nodes/{node}/firewall/log")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/firewall/log")]
    [PveApi("GET", "/nodes/{node}/lxc/{vmid}/firewall/log")]
    public Task<IReadOnlyList<Row>> LogAsync(FirewallScope scope, int limit = 500, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{scope.BasePath}/log?limit={limit}", ct);
    }

    /// <summary>
    ///     규칙의 출발지·목적지에 쓸 수 있는 별칭·IP 집합 — type(alias·ipset)·name·ref·scope·comment.
    ///     게스트 범위는 데이터센터 것까지 함께 준다. 노드 범위에는 이 요청이 없어 데이터센터 것을 쓴다.
    /// </summary>
    [PveApi("GET", "/cluster/firewall/refs")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/firewall/refs")]
    [PveApi("GET", "/nodes/{node}/lxc/{vmid}/firewall/refs")]
    public Task<IReadOnlyList<Row>> RefsAsync(FirewallScope scope, CancellationToken ct = default)
    {
        var isGuest = scope.BasePath.Contains("/qemu/", StringComparison.Ordinal)
                      || scope.BasePath.Contains("/lxc/", StringComparison.Ordinal);
        var basePath = isGuest ? scope.BasePath : FirewallScope.Cluster.BasePath;
        return Api.GetTableAsync($"{basePath}/refs", ct);
    }

    // ------------------------------------------------------------ 별칭

    [PveApi("GET", "/cluster/firewall/aliases")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/firewall/aliases")]
    [PveApi("GET", "/nodes/{node}/lxc/{vmid}/firewall/aliases")]
    public Task<IReadOnlyList<Row>> ListAliasesAsync(FirewallScope scope, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{scope.BasePath}/aliases", ct);
    }

    [PveApi("POST", "/cluster/firewall/aliases")]
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/firewall/aliases")]
    [PveApi("POST", "/nodes/{node}/lxc/{vmid}/firewall/aliases")]
    public Task<string> CreateAliasAsync(FirewallScope scope, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{scope.BasePath}/aliases", form, ct);
    }

    /// <summary>수정·이름 바꾸기(rename).</summary>
    [PveApi("PUT", "/cluster/firewall/aliases/{name}")]
    [PveApi("PUT", "/nodes/{node}/qemu/{vmid}/firewall/aliases/{name}")]
    [PveApi("PUT", "/nodes/{node}/lxc/{vmid}/firewall/aliases/{name}")]
    public Task<string> UpdateAliasAsync(FirewallScope scope, string name, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{scope.BasePath}/aliases/{Seg(name)}", form, ct);
    }

    [PveApi("DELETE", "/cluster/firewall/aliases/{name}")]
    [PveApi("DELETE", "/nodes/{node}/qemu/{vmid}/firewall/aliases/{name}")]
    [PveApi("DELETE", "/nodes/{node}/lxc/{vmid}/firewall/aliases/{name}")]
    public Task<string> DeleteAliasAsync(FirewallScope scope, string name, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{scope.BasePath}/aliases/{Seg(name)}", ct);
    }

    // ------------------------------------------------------------ IP 집합

    [PveApi("GET", "/cluster/firewall/ipset")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/firewall/ipset")]
    [PveApi("GET", "/nodes/{node}/lxc/{vmid}/firewall/ipset")]
    public Task<IReadOnlyList<Row>> ListIpSetsAsync(FirewallScope scope, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{scope.BasePath}/ipset", ct);
    }

    /// <summary>만들기 — rename 을 주면 이름·설명 바꾸기.</summary>
    [PveApi("POST", "/cluster/firewall/ipset")]
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/firewall/ipset")]
    [PveApi("POST", "/nodes/{node}/lxc/{vmid}/firewall/ipset")]
    public Task<string> SaveIpSetAsync(FirewallScope scope, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{scope.BasePath}/ipset", form, ct);
    }

    /// <summary>
    ///     지우기 — 7.2+ 는 항목이 있어도 한 번에 지운다(force). 그 전 서버는 force 를 몰라 항목을 먼저 지운다.
    /// </summary>
    [PveApi("DELETE", "/cluster/firewall/ipset/{name}")]
    [PveApi("DELETE", "/nodes/{node}/qemu/{vmid}/firewall/ipset/{name}")]
    [PveApi("DELETE", "/nodes/{node}/lxc/{vmid}/firewall/ipset/{name}")]
    [PveParam("force", "7.2")]
    public async Task<string> DeleteIpSetAsync(FirewallScope scope, string name, CancellationToken ct = default)
    {
        var path = $"{scope.BasePath}/ipset/{Seg(name)}";
        if (await SupportsAsync("7.2", ct).ConfigureAwait(false))
            return await Api.DeleteActionAsync($"{path}?force=1", ct).ConfigureAwait(false);
        foreach (var entry in await Api.GetTableAsync(path, ct).ConfigureAwait(false))
            if (entry.TryGetValue("cidr", out var cidr))
                await Api.DeleteActionAsync($"{path}/{Seg(cidr)}", ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync(path, ct).ConfigureAwait(false);
    }

    [PveApi("GET", "/cluster/firewall/ipset/{name}")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/firewall/ipset/{name}")]
    [PveApi("GET", "/nodes/{node}/lxc/{vmid}/firewall/ipset/{name}")]
    public Task<IReadOnlyList<Row>> ListIpSetEntriesAsync(FirewallScope scope, string name,
        CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{scope.BasePath}/ipset/{Seg(name)}", ct);
    }

    [PveApi("POST", "/cluster/firewall/ipset/{name}")]
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/firewall/ipset/{name}")]
    [PveApi("POST", "/nodes/{node}/lxc/{vmid}/firewall/ipset/{name}")]
    public Task<string> AddIpSetEntryAsync(FirewallScope scope, string name, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{scope.BasePath}/ipset/{Seg(name)}", form, ct);
    }

    [PveApi("PUT", "/cluster/firewall/ipset/{name}/{cidr}")]
    [PveApi("PUT", "/nodes/{node}/qemu/{vmid}/firewall/ipset/{name}/{cidr}")]
    [PveApi("PUT", "/nodes/{node}/lxc/{vmid}/firewall/ipset/{name}/{cidr}")]
    public Task<string> UpdateIpSetEntryAsync(FirewallScope scope, string name, string cidr,
        IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{scope.BasePath}/ipset/{Seg(name)}/{Seg(cidr)}", form, ct);
    }

    [PveApi("DELETE", "/cluster/firewall/ipset/{name}/{cidr}")]
    [PveApi("DELETE", "/nodes/{node}/qemu/{vmid}/firewall/ipset/{name}/{cidr}")]
    [PveApi("DELETE", "/nodes/{node}/lxc/{vmid}/firewall/ipset/{name}/{cidr}")]
    public Task<string> DeleteIpSetEntryAsync(FirewallScope scope, string name, string cidr,
        CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{scope.BasePath}/ipset/{Seg(name)}/{Seg(cidr)}", ct);
    }

    // ------------------------------------------------------------ 보안 그룹·매크로(데이터센터)

    [PveApi("GET", "/cluster/firewall/groups")]
    public Task<IReadOnlyList<Row>> ListGroupsAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/firewall/groups", ct);
    }

    /// <summary>만들기 — rename 을 주면 이름·설명 바꾸기.</summary>
    [PveApi("POST", "/cluster/firewall/groups")]
    public Task<string> SaveGroupAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("cluster/firewall/groups", form, ct);
    }

    [PveApi("DELETE", "/cluster/firewall/groups/{group}")]
    public Task<string> DeleteGroupAsync(string group, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"cluster/firewall/groups/{Seg(group)}", ct);
    }

    [PveApi("GET", "/cluster/firewall/macros")]
    public Task<IReadOnlyList<Row>> MacrosAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/firewall/macros", ct);
    }
}
