using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     고가용성(HA). PVE 9 에서 그룹이 규칙(rules — 노드·리소스 친화성)으로 바뀌었다: 규칙은 9.0+,
///     그룹은 8 까지(9 에서는 사라질 예정이라 쓰지 않는다). 리소스의 failback 은 9.0+.
/// </summary>
public sealed class HaApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private const string Rules = "9.0";

    /// <summary>규칙을 쓰는 서버인지(9.0+). 버전을 모르면 null — 화면이 두 방식을 모두 보인다.</summary>
    public bool? UsesRules => Api.ServerVersion is { } v ? v >= PveApiVersion.Parse(Rules) : null;

    [PveApi("GET", "/cluster/ha/status/current")]
    public Task<IReadOnlyList<Row>> StatusAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/ha/status/current", ct);
    }

    [PveApi("GET", "/cluster/ha/resources")]
    public Task<IReadOnlyList<Row>> ListResourcesAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/ha/resources", ct);
    }

    [PveApi("POST", "/cluster/ha/resources")]
    [PveParam("failback", Rules)]
    public Task<string> CreateResourceAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("cluster/ha/resources", Supported(form), ct);
    }

    [PveApi("PUT", "/cluster/ha/resources/{sid}")]
    [PveParam("failback", Rules)]
    public Task<string> UpdateResourceAsync(string sid, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"cluster/ha/resources/{Seg(sid)}", Supported(form), ct);
    }

    /// <summary>HA 자원을 다른 노드로 온라인 이전(작업 UPID) — 실행 중이면 멈추지 않고 옮긴다.</summary>
    [PveApi("POST", "/cluster/ha/resources/{sid}/migrate")]
    public Task<string> MigrateResourceAsync(string sid, string node, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"cluster/ha/resources/{Seg(sid)}/migrate",
            new Dictionary<string, string> { ["node"] = node }, ct);
    }

    /// <summary>HA 자원 재배치(작업 UPID) — 멈춘 뒤 다른 노드에서 다시 시작한다(오프라인 이전).</summary>
    [PveApi("POST", "/cluster/ha/resources/{sid}/relocate")]
    public Task<string> RelocateResourceAsync(string sid, string node, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"cluster/ha/resources/{Seg(sid)}/relocate",
            new Dictionary<string, string> { ["node"] = node }, ct);
    }

    /// <summary>
    ///     HA 스택 무장 해제(9.1+) — 클러스터 전체 워치독을 풀어 유지 보수 중 노드가 스스로 재부팅되지 않게 한다.
    ///     resourceMode: freeze(자원 그대로 멈춤)·ignore(HA 가 자원을 관리하지 않음).
    /// </summary>
    [PveApi("POST", "/cluster/ha/status/disarm-ha", Since = "9.1")]
    public async Task<string> DisarmAsync(string resourceMode, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PostActionAsync("cluster/ha/status/disarm-ha",
            new Dictionary<string, string> { ["resource-mode"] = resourceMode }, ct).ConfigureAwait(false);
    }

    /// <summary>무장 해제한 HA 스택을 다시 켠다(9.1+).</summary>
    [PveApi("POST", "/cluster/ha/status/arm-ha", Since = "9.1")]
    public async Task<string> ArmAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PostActionAsync("cluster/ha/status/arm-ha", null, ct).ConfigureAwait(false);
    }

    [PveApi("DELETE", "/cluster/ha/resources/{sid}")]
    public Task<string> DeleteResourceAsync(string sid, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"cluster/ha/resources/{Seg(sid)}", ct);
    }

    // ------------------------------------------------------------ 그룹(8 까지)

    [PveApi("GET", "/cluster/ha/groups")]
    public Task<IReadOnlyList<Row>> ListGroupsAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/ha/groups", ct);
    }

    [PveApi("POST", "/cluster/ha/groups")]
    public Task<string> CreateGroupAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("cluster/ha/groups", form, ct);
    }

    [PveApi("PUT", "/cluster/ha/groups/{group}")]
    public Task<string> UpdateGroupAsync(string group, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"cluster/ha/groups/{Seg(group)}", form, ct);
    }

    [PveApi("DELETE", "/cluster/ha/groups/{group}")]
    public Task<string> DeleteGroupAsync(string group, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"cluster/ha/groups/{Seg(group)}", ct);
    }

    // ------------------------------------------------------------ 규칙(9.0+)

    [PveApi("GET", "/cluster/ha/rules", Since = Rules)]
    public async Task<IReadOnlyList<Row>> ListRulesAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync("cluster/ha/rules", ct).ConfigureAwait(false);
    }

    [PveApi("POST", "/cluster/ha/rules", Since = Rules)]
    public async Task<string> CreateRuleAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PostActionAsync("cluster/ha/rules", form, ct).ConfigureAwait(false);
    }

    [PveApi("PUT", "/cluster/ha/rules/{rule}", Since = Rules)]
    public async Task<string> UpdateRuleAsync(string rule, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PutActionAsync($"cluster/ha/rules/{Seg(rule)}", form, ct).ConfigureAwait(false);
    }

    [PveApi("DELETE", "/cluster/ha/rules/{rule}", Since = Rules)]
    public async Task<string> DeleteRuleAsync(string rule, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync($"cluster/ha/rules/{Seg(rule)}", ct).ConfigureAwait(false);
    }

    /// <summary>HA 관리자 전체 상태(보기 좋게 들여쓴 JSON) — 관리자·노드별 LRM 상태·서비스 상태.</summary>
    [PveApi("GET", "/cluster/ha/status/manager_status")]
    public Task<string> ManagerStatusJsonAsync(CancellationToken ct = default)
    {
        return Api.GetPrettyJsonAsync("cluster/ha/status/manager_status", ct);
    }
}
