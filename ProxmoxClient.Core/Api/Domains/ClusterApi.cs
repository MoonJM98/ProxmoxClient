using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>클러스터 상태·자원·옵션·구성(만들기·가입), Ceph 상태.</summary>
public sealed class ClusterApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    [PveApi("GET", "/cluster/status")]
    public Task<IReadOnlyList<Row>> StatusAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/status", ct);
    }

    /// <param name="type">vm·storage·node·sdn — 비우면 전체.</param>
    [PveApi("GET", "/cluster/resources")]
    public Task<IReadOnlyList<Row>> ResourcesAsync(string? type = null, CancellationToken ct = default)
    {
        return Api.GetTableAsync(type is null ? "cluster/resources" : $"cluster/resources?type={Seg(type)}", ct);
    }

    [PveApi("GET", "/cluster/options")]
    public Task<Row> GetOptionsAsync(CancellationToken ct = default)
    {
        return Api.GetObjectAsync("cluster/options", ct);
    }

    /// <summary>데이터센터 옵션 — 서버가 모르는 옵션(표시한 버전보다 낮은 서버)은 보내지 않는다.</summary>
    [PveApi("PUT", "/cluster/options")]
    [PveParam("description", "7.1")]
    [PveParam("webauthn", "7.1")]
    [PveParam("next-id", "7.1")]
    [PveParam("crs", "7.2")]
    [PveParam("registered-tags", "7.2")]
    [PveParam("tag-style", "7.2")]
    [PveParam("user-tag-access", "7.2")]
    [PveParam("notify", "7.4")]
    [PveParam("consent-text", "8.3")]
    [PveParam("replication", "9.0")]
    public Task<string> UpdateOptionsAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PutActionAsync("cluster/options", Supported(form), ct);
    }

    [PveApi("GET", "/cluster/config/nodes")]
    public Task<IReadOnlyList<Row>> ConfigNodesAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/config/nodes", ct);
    }

    /// <summary>클러스터 만들기(작업 UPID) — clustername, link0…</summary>
    [PveApi("POST", "/cluster/config")]
    public Task<string> CreateAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("cluster/config", form, ct);
    }

    /// <summary>다른 클러스터에 가입(작업 UPID) — hostname, fingerprint, password, linkN.</summary>
    [PveApi("POST", "/cluster/config/join")]
    public Task<string> JoinAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("cluster/config/join", form, ct);
    }

    /// <summary>Ceph 상태(보기 좋게 들여쓴 JSON).</summary>
    [PveApi("GET", "/cluster/ceph/status")]
    public Task<string> CephStatusJsonAsync(CancellationToken ct = default)
    {
        return Api.GetPrettyJsonAsync("cluster/ceph/status", ct);
    }

    /// <summary>클러스터 로그(웹 UI 아래 'Cluster log') — 최근 max 줄. uid·time·node·user·tag·pri·msg.</summary>
    [PveApi("GET", "/cluster/log")]
    public Task<IReadOnlyList<Row>> LogAsync(int max = 200, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"cluster/log?max={max}", ct);
    }

    /// <summary>
    ///     클러스터 전체 게스트 일괄 작업(9.0+, 작업 UPID) — action: start·shutdown·suspend·migrate, vms 로 대상을 고른다.
    ///     동시 작업 수는 9.1 에서 max-workers 로 이름이 바뀌어, 9.0 서버에는 옛 이름 maxworkers 로 보낸다.
    ///     vms 는 배열 파라미터라 쉼표 목록을 키 반복(vms=100&amp;vms=101)으로 펼친다.
    /// </summary>
    private static readonly IReadOnlySet<string> VmsArray = new HashSet<string>(["vms"], StringComparer.Ordinal);

    [PveApi("POST", "/cluster/bulk-action/guest/start", Since = "9.0")]
    [PveApi("POST", "/cluster/bulk-action/guest/shutdown", Since = "9.0")]
    [PveApi("POST", "/cluster/bulk-action/guest/suspend", Since = "9.0")]
    [PveApi("POST", "/cluster/bulk-action/guest/migrate", Since = "9.0")]
    [PveParam("max-workers", "9.1")]
    public async Task<string> BulkGuestAsync(string action, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        if (action is not ("start" or "shutdown" or "suspend" or "migrate"))
            throw new ArgumentOutOfRangeException(nameof(action), action, null);
        await RequireAsync(ct).ConfigureAwait(false);
        var body = form.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (body.Remove("max-workers", out var workers))
            body[SupportsParam(nameof(BulkGuestAsync), "max-workers") ? "max-workers" : "maxworkers"] = workers;
        return await Api.SendWithArraysAsync(HttpMethod.Post, $"cluster/bulk-action/guest/{action}", body,
            VmsArray, ct).ConfigureAwait(false);
    }

    /// <summary>Corosync totem 설정(보기 좋게 들여쓴 JSON) — 링·암호화·타임아웃.</summary>
    [PveApi("GET", "/cluster/config/totem")]
    public Task<string> TotemJsonAsync(CancellationToken ct = default)
    {
        return Api.GetPrettyJsonAsync("cluster/config/totem", ct);
    }

    /// <summary>QDevice(외부 투표 장치) 상태(보기 좋게 들여쓴 JSON) — 없으면 빈 객체.</summary>
    [PveApi("GET", "/cluster/config/qdevice")]
    public Task<string> QDeviceJsonAsync(CancellationToken ct = default)
    {
        return Api.GetPrettyJsonAsync("cluster/config/qdevice", ct);
    }

    // ------------------------------------------------------------ 사용자 CPU 모델(9.2)

    /// <summary>사용자 CPU 모델(9.2+) — cputype·reported-model·flags·hidden·hv-vendor-id·phys-bits 등.</summary>
    [PveApi("GET", "/cluster/qemu/custom-cpu-models", Since = "9.2")]
    public async Task<IReadOnlyList<Row>> CpuModelsAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync("cluster/qemu/custom-cpu-models", ct).ConfigureAwait(false);
    }

    [PveApi("POST", "/cluster/qemu/custom-cpu-models", Since = "9.2")]
    public async Task<string> CreateCpuModelAsync(IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PostActionAsync("cluster/qemu/custom-cpu-models", form, ct).ConfigureAwait(false);
    }

    [PveApi("PUT", "/cluster/qemu/custom-cpu-models/{cputype}", Since = "9.2")]
    public async Task<string> UpdateCpuModelAsync(string cpuType, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PutActionAsync($"cluster/qemu/custom-cpu-models/{Seg(cpuType)}", form, ct)
            .ConfigureAwait(false);
    }

    [PveApi("DELETE", "/cluster/qemu/custom-cpu-models/{cputype}", Since = "9.2")]
    public async Task<string> DeleteCpuModelAsync(string cpuType, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync($"cluster/qemu/custom-cpu-models/{Seg(cpuType)}", ct)
            .ConfigureAwait(false);
    }
}
