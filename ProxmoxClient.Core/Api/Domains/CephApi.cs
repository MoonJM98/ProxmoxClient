using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     노드 Ceph — 상태·설정·모니터/매니저/MDS·OSD·CephFS·풀·로그.
///     PVE 7.4 에서 풀 경로가 ceph/pools → ceph/pool, 설정이 ceph/config → ceph/cfg/raw 로 바뀌었다 —
///     그보다 낮은 서버에는 옛 경로를 쓴다. OSD 의 osds-per-device 는 8.1+.
/// </summary>
public sealed class CephApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private const string NewPaths = "7.4";

    private static string C(string node) => $"nodes/{Seg(node)}/ceph";

    [PveApi("GET", "/nodes/{node}/ceph/status")]
    public Task<string> StatusJsonAsync(string node, CancellationToken ct = default)
    {
        return Api.GetPrettyJsonAsync($"{C(node)}/status", ct);
    }

    /// <summary>ceph.conf 원문.</summary>
    [PveApi("GET", "/nodes/{node}/ceph/cfg/raw", Since = NewPaths)]
    [PveApi("GET", "/nodes/{node}/ceph/config", Until = NewPaths, Legacy = true)]
    public async Task<string> ConfigTextAsync(string node, CancellationToken ct = default)
    {
        var path = await SupportsAsync(NewPaths, ct).ConfigureAwait(false) ? "cfg/raw" : "config";
        return await Api.GetTextAsync($"{C(node)}/{path}", ct).ConfigureAwait(false);
    }

    [PveApi("GET", "/nodes/{node}/ceph/log")]
    public Task<IReadOnlyList<Row>> LogAsync(string node, int limit = 500, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{C(node)}/log?limit={limit}", ct);
    }

    // ------------------------------------------------------------ 모니터·매니저·MDS

    /// <param name="kind">mon·mgr·mds.</param>
    [PveApi("GET", "/nodes/{node}/ceph/mon")]
    [PveApi("GET", "/nodes/{node}/ceph/mgr")]
    [PveApi("GET", "/nodes/{node}/ceph/mds")]
    public Task<IReadOnlyList<Row>> ListServicesAsync(string node, string kind, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{C(node)}/{Service(kind)}", ct);
    }

    /// <summary>이 노드에 서비스를 만든다(작업 UPID) — 이름은 노드 이름.</summary>
    [PveApi("POST", "/nodes/{node}/ceph/mon/{monid}")]
    [PveApi("POST", "/nodes/{node}/ceph/mgr/{id}")]
    [PveApi("POST", "/nodes/{node}/ceph/mds/{name}")]
    public Task<string> CreateServiceAsync(string node, string kind, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{C(node)}/{Service(kind)}/{Seg(node)}", null, ct);
    }

    /// <param name="host">서비스가 도는 노드 — 그 노드에 요청해야 서비스까지 정리된다.</param>
    [PveApi("DELETE", "/nodes/{node}/ceph/mon/{monid}")]
    [PveApi("DELETE", "/nodes/{node}/ceph/mgr/{id}")]
    [PveApi("DELETE", "/nodes/{node}/ceph/mds/{name}")]
    public Task<string> DeleteServiceAsync(string host, string kind, string name, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{C(host)}/{Service(kind)}/{Seg(name)}", ct);
    }

    // ------------------------------------------------------------ OSD

    /// <summary>OSD 만들기(작업 UPID) — dev, (선택) db_dev·wal_dev·encrypted·crush-device-class·osds-per-device.</summary>
    [PveApi("POST", "/nodes/{node}/ceph/osd")]
    [PveParam("osds-per-device", "8.1")]
    public Task<string> CreateOsdAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{C(node)}/osd", Supported(form), ct);
    }

    /// <param name="host">OSD 가 있는 노드.</param>
    [PveApi("DELETE", "/nodes/{node}/ceph/osd/{osdid}")]
    public Task<string> DeleteOsdAsync(string host, string id, bool cleanup, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{C(host)}/osd/{Seg(id)}?cleanup={(cleanup ? 1 : 0)}", ct);
    }

    /// <param name="verb">in·out·scrub.</param>
    [PveApi("POST", "/nodes/{node}/ceph/osd/{osdid}/in")]
    [PveApi("POST", "/nodes/{node}/ceph/osd/{osdid}/out")]
    [PveApi("POST", "/nodes/{node}/ceph/osd/{osdid}/scrub")]
    public Task<string> OsdCommandAsync(string node, string id, string verb, CancellationToken ct = default)
    {
        var command = verb is "in" or "out" or "scrub"
            ? verb
            : throw new ArgumentException($"Unknown OSD command '{verb}'", nameof(verb));
        return Api.PostActionAsync($"{C(node)}/osd/{Seg(id)}/{command}", null, ct);
    }

    // ------------------------------------------------------------ CephFS

    [PveApi("GET", "/nodes/{node}/ceph/fs")]
    public Task<IReadOnlyList<Row>> ListFileSystemsAsync(string node, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{C(node)}/fs", ct);
    }

    [PveApi("POST", "/nodes/{node}/ceph/fs/{name}")]
    public Task<string> CreateFileSystemAsync(string node, string name, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{C(node)}/fs/{Seg(name)}", form, ct);
    }

    // ------------------------------------------------------------ 풀

    [PveApi("GET", "/nodes/{node}/ceph/pool", Since = NewPaths)]
    [PveApi("GET", "/nodes/{node}/ceph/pools", Until = NewPaths, Legacy = true)]
    public async Task<IReadOnlyList<Row>> ListPoolsAsync(string node, CancellationToken ct = default)
    {
        return await Api.GetTableAsync(await PoolsPathAsync(node, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <summary>풀 만들기(작업 UPID) — name·size·min_size·pg_autoscale_mode·add_storages.</summary>
    [PveApi("POST", "/nodes/{node}/ceph/pool", Since = NewPaths)]
    [PveApi("POST", "/nodes/{node}/ceph/pools", Until = NewPaths, Legacy = true)]
    public async Task<string> CreatePoolAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return await Api.PostActionAsync(await PoolsPathAsync(node, ct).ConfigureAwait(false), form, ct)
            .ConfigureAwait(false);
    }

    /// <param name="removeStorages">이 풀을 쓰는 PVE 저장소 설정도 지운다.</param>
    [PveApi("DELETE", "/nodes/{node}/ceph/pool/{name}", Since = NewPaths)]
    [PveApi("DELETE", "/nodes/{node}/ceph/pools/{name}", Until = NewPaths, Legacy = true)]
    public async Task<string> DeletePoolAsync(string node, string name, bool removeStorages,
        CancellationToken ct = default)
    {
        var path = await PoolsPathAsync(node, ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync($"{path}/{Seg(name)}?remove_storages={(removeStorages ? 1 : 0)}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>풀 설정 바꾸기(작업 UPID) — size·min_size·pg_num·pg_autoscale_mode·crush_rule·target_size 등.</summary>
    [PveApi("PUT", "/nodes/{node}/ceph/pool/{name}", Since = NewPaths)]
    [PveApi("PUT", "/nodes/{node}/ceph/pools/{name}", Until = NewPaths, Legacy = true)]
    public async Task<string> UpdatePoolAsync(string node, string name, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        var path = await PoolsPathAsync(node, ct).ConfigureAwait(false);
        return await Api.PutActionAsync($"{path}/{Seg(name)}", form, ct).ConfigureAwait(false);
    }

    /// <summary>클러스터 전체 플래그(noout 등) — name·value(0/1)·description.</summary>
    [PveApi("GET", "/cluster/ceph/flags")]
    public Task<IReadOnlyList<Row>> FlagsAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("cluster/ceph/flags", ct);
    }

    /// <summary>여러 플래그를 한 번에 켜고 끈다(작업 UPID) — 이름 → "1"/"0".</summary>
    [PveApi("PUT", "/cluster/ceph/flags")]
    public Task<string> SetFlagsAsync(IReadOnlyDictionary<string, string> flags, CancellationToken ct = default)
    {
        return Api.PutActionAsync("cluster/ceph/flags", Supported(flags), ct);
    }

    /// <summary>
    ///     Ceph 데몬 시작·중지·재시작(작업 UPID) — verb: start·stop·restart, service: "mon.pve"·"osd.3"·"ceph.target"(모두).
    /// </summary>
    [PveApi("POST", "/nodes/{node}/ceph/start")]
    [PveApi("POST", "/nodes/{node}/ceph/stop")]
    [PveApi("POST", "/nodes/{node}/ceph/restart")]
    public Task<string> ServiceCommandAsync(string node, string verb, string service, CancellationToken ct = default)
    {
        if (verb is not ("start" or "stop" or "restart")) throw new ArgumentOutOfRangeException(nameof(verb));
        return Api.PostActionAsync($"{C(node)}/{verb}", new Dictionary<string, string> { ["service"] = service }, ct);
    }

    /// <summary>OSD 자세한 정보(7.4+, 보기 좋게 들여쓴 JSON) — 장치·BlueStore·메모리 등.</summary>
    [PveApi("GET", "/nodes/{node}/ceph/osd/{osdid}/metadata", Since = NewPaths)]
    public async Task<string> OsdMetadataJsonAsync(string node, string osdId, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetPrettyJsonAsync($"{C(node)}/osd/{Seg(osdId)}/metadata", ct).ConfigureAwait(false);
    }

    private async Task<string> PoolsPathAsync(string node, CancellationToken ct)
    {
        return await SupportsAsync(NewPaths, ct).ConfigureAwait(false) ? $"{C(node)}/pool" : $"{C(node)}/pools";
    }

    private static string Service(string kind)
    {
        return kind is "mon" or "mgr" or "mds"
            ? kind
            : throw new ArgumentException($"Unknown Ceph service '{kind}'", nameof(kind));
    }

    /// <summary>풀 상태(7.4+, 보기 좋게 들여쓴 JSON) — 사용량·자동 조정·응용 등.</summary>
    [PveApi("GET", "/nodes/{node}/ceph/pool/{name}/status", Since = NewPaths)]
    public async Task<string> PoolStatusJsonAsync(string node, string name, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetPrettyJsonAsync($"{C(node)}/pool/{Seg(name)}/status?verbose=1", ct)
            .ConfigureAwait(false);
    }

    /// <summary>OSD 의 LVM 볼륨 정보(7.4+, 보기 좋게 들여쓴 JSON) — 블록 장치 기준.</summary>
    [PveApi("GET", "/nodes/{node}/ceph/osd/{osdid}/lv-info", Since = NewPaths)]
    public async Task<string> OsdLvInfoJsonAsync(string node, string osdId, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetPrettyJsonAsync($"{C(node)}/osd/{Seg(osdId)}/lv-info", ct).ConfigureAwait(false);
    }

    /// <summary>CRUSH 맵(글) — 장치·버킷·규칙.</summary>
    [PveApi("GET", "/nodes/{node}/ceph/crush")]
    public Task<string> CrushMapAsync(string node, CancellationToken ct = default)
    {
        return Api.GetTextAsync($"{C(node)}/crush", ct);
    }

    /// <summary>모니터 설정 DB(7.4+) — section·name·value·level·can_update_at_runtime·mask.</summary>
    [PveApi("GET", "/nodes/{node}/ceph/cfg/db", Since = NewPaths)]
    public async Task<IReadOnlyList<Row>> ConfigDbAsync(string node, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync($"{C(node)}/cfg/db", ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 9.2 — 상태 경고 음소거·순차 재시작·릴리스·CephFS 삭제

    private const string Reef92 = "9.2";

    /// <summary>음소거한 상태 경고(9.2+) — code·summary·ttl·sticky.</summary>
    [PveApi("GET", "/cluster/ceph/health-mute", Since = Reef92)]
    public async Task<IReadOnlyList<Row>> HealthMutesAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync("cluster/ceph/health-mute", ct).ConfigureAwait(false);
    }

    /// <summary>상태 경고 음소거·해제(9.2+) — ttl(예: 1h, 비우면 계속), sticky(경고가 풀렸다 다시 떠도 유지).</summary>
    [PveApi("PUT", "/cluster/ceph/health-mute/{code}", Since = Reef92)]
    public async Task<string> SetHealthMuteAsync(string code, bool mute, string? ttl = null, bool sticky = false,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        var form = new Dictionary<string, string> { ["value"] = mute ? "1" : "0" };
        if (mute && !string.IsNullOrWhiteSpace(ttl)) form["ttl"] = ttl.Trim();
        if (mute && sticky) form["sticky"] = "1";
        return await Api.PutActionAsync($"cluster/ceph/health-mute/{Seg(code)}", form, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     클러스터 전체 데몬 순차 재시작(9.2+, 작업 UPID) — service-type(mon·mgr·mds·osd), only-outdated·dry-run·force.
    /// </summary>
    [PveApi("POST", "/cluster/ceph/restart-bulk", Since = Reef92)]
    public async Task<string> ClusterRestartBulkAsync(IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PostActionAsync("cluster/ceph/restart-bulk", form, ct).ConfigureAwait(false);
    }

    /// <summary>이 노드 OSD 순차 재시작(9.2+, 작업 UPID) — set-noout·only-outdated·dry-run 등.</summary>
    [PveApi("POST", "/nodes/{node}/ceph/restart-bulk", Since = Reef92)]
    public async Task<string> NodeRestartBulkAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        var body = new Dictionary<string, string>(form, StringComparer.Ordinal) { ["service-type"] = "osd" };
        return await Api.PostActionAsync($"{C(node)}/restart-bulk", body, ct).ConfigureAwait(false);
    }

    /// <summary>설치할 수 있는 Ceph 릴리스(9.2+) — release·version·available·is-default·unsupported.</summary>
    [PveApi("GET", "/nodes/{node}/ceph/releases", Since = Reef92)]
    public async Task<IReadOnlyList<Row>> ReleasesAsync(string node, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync($"{C(node)}/releases", ct).ConfigureAwait(false);
    }

    /// <summary>CephFS 삭제(9.2+, 작업 UPID) — 쓰는 PVE 저장소가 있으면 서버가 거절한다(removeStorages 로 함께 지울 수 있다).</summary>
    [PveApi("DELETE", "/nodes/{node}/ceph/fs/{name}", Since = Reef92)]
    public async Task<string> DeleteFileSystemAsync(string node, string name, bool removePools, bool removeStorages,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync($"{C(node)}/fs/{Seg(name)}?remove-pools={(removePools ? 1 : 0)}"
                                           + $"&remove-storages={(removeStorages ? 1 : 0)}", ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     데몬을 멈추거나 없애도 데이터가 안전한지(7.2+, Ceph 의 ok-to-stop 등) — safe(bool)·status(설명).
    ///     service: osd·mon·mds, action: stop·destroy.
    /// </summary>
    [PveApi("GET", "/nodes/{node}/ceph/cmd-safety", Since = "7.2")]
    public async Task<Row> CmdSafetyAsync(string node, string service, string id, string action,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetObjectAsync($"{C(node)}/cmd-safety?service={Uri.EscapeDataString(service)}"
                                        + $"&id={Uri.EscapeDataString(id)}&action={Uri.EscapeDataString(action)}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Ceph 첫 설정(ceph.conf 만들기) — network·cluster-network·size·min_size 등.</summary>
    [PveApi("POST", "/nodes/{node}/ceph/init")]
    public Task<string> InitAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{C(node)}/init", form, ct);
    }

    /// <summary>클러스터 모든 Ceph 데몬의 메타데이터(보기 좋게 들여쓴 JSON) — 버전·호스트·장치 등.</summary>
    [PveApi("GET", "/cluster/ceph/metadata")]
    public Task<string> ClusterMetadataJsonAsync(CancellationToken ct = default)
    {
        return Api.GetPrettyJsonAsync("cluster/ceph/metadata", ct);
    }
}
