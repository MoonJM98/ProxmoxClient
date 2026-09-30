using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     노드 — 설정·hosts·구독·전원·모두 시작/정지/이전·복제·네트워크·인증서·APT.
///     버전 차이: 모두 정지의 timeout·force-stop(7.4+), 네트워크 bridge_vids(8.2+),
///     노드 설정 ballooning-target(8.3+)·location(9.2+). 모두 이전의 동시 작업 수는 모든 버전이 아는 maxworkers 로 보낸다
///     (max-workers 는 9.1+ 새 이름).
/// </summary>
public sealed class NodesApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private static string N(string node) => $"nodes/{Seg(node)}";

    [PveApi("GET", "/nodes/{node}/config")]
    public Task<Row> GetConfigAsync(string node, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"{N(node)}/config", ct);
    }

    [PveApi("PUT", "/nodes/{node}/config")]
    [PveParam("ballooning-target", "8.3")]
    [PveParam("location", "9.2")]
    public Task<string> UpdateConfigAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{N(node)}/config", Supported(form), ct);
    }

    /// <summary>/etc/hosts — data(본문)·digest.</summary>
    [PveApi("GET", "/nodes/{node}/hosts")]
    public Task<Row> GetHostsAsync(string node, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"{N(node)}/hosts", ct);
    }

    [PveApi("POST", "/nodes/{node}/hosts")]
    public Task<string> SetHostsAsync(string node, string text, string digest, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{N(node)}/hosts",
            new Dictionary<string, string> { ["data"] = text, ["digest"] = digest }, ct);
    }

    // ------------------------------------------------------------ 구독

    [PveApi("GET", "/nodes/{node}/subscription")]
    public Task<Row> GetSubscriptionAsync(string node, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"{N(node)}/subscription", ct);
    }

    [PveApi("PUT", "/nodes/{node}/subscription")]
    public Task<string> SetSubscriptionKeyAsync(string node, string key, CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{N(node)}/subscription", new Dictionary<string, string> { ["key"] = key }, ct);
    }

    [PveApi("POST", "/nodes/{node}/subscription")]
    public Task<string> CheckSubscriptionAsync(string node, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{N(node)}/subscription", new Dictionary<string, string> { ["force"] = "1" }, ct);
    }

    [PveApi("DELETE", "/nodes/{node}/subscription")]
    public Task<string> DeleteSubscriptionAsync(string node, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{N(node)}/subscription", ct);
    }

    // ------------------------------------------------------------ 전원·일괄 작업

    /// <summary>노드 재시작·끄기 — command: reboot·shutdown.</summary>
    [PveApi("POST", "/nodes/{node}/status")]
    public Task<string> PowerAsync(string node, string command, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{N(node)}/status", new Dictionary<string, string> { ["command"] = command }, ct);
    }

    [PveApi("POST", "/nodes/{node}/startall")]
    [PveParam("max-workers", "9.1")]
    public Task<string> StartAllAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{N(node)}/startall", Supported(form), ct);
    }

    [PveApi("POST", "/nodes/{node}/stopall")]
    [PveParam("timeout", "7.4")]
    [PveParam("force-stop", "7.4")]
    [PveParam("max-workers", "9.1")]
    public Task<string> StopAllAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{N(node)}/stopall", Supported(form), ct);
    }

    /// <summary>모든 VM 일시 중지(8.1~) — vms 로 대상을 좁힐 수 있다.</summary>
    [PveApi("POST", "/nodes/{node}/suspendall", Since = "8.1")]
    [PveParam("max-workers", "9.1")]
    public Task<string> SuspendAllAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{N(node)}/suspendall", Supported(form), ct);
    }

    /// <summary>
    ///     꺼진 노드를 Wake-on-LAN 으로 깨운다 — 요청을 받은(켜진) 노드가 노드 설정의 wakeonlan MAC 으로 패킷을 보낸다.
    /// </summary>
    [PveApi("POST", "/nodes/{node}/wakeonlan")]
    public Task<string> WakeOnLanAsync(string node, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{N(node)}/wakeonlan", null, ct);
    }

    // ------------------------------------------------------------ 시스템 서비스

    /// <summary>노드 시스템 서비스 — service·name·desc·state·active-state·unit-state.</summary>
    [PveApi("GET", "/nodes/{node}/services")]
    public Task<IReadOnlyList<Row>> ServicesAsync(string node, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{N(node)}/services", ct);
    }

    /// <summary>서비스 시작·중지·재시작·다시 읽기(작업 UPID) — verb: start·stop·restart·reload.</summary>
    [PveApi("POST", "/nodes/{node}/services/{service}/start")]
    [PveApi("POST", "/nodes/{node}/services/{service}/stop")]
    [PveApi("POST", "/nodes/{node}/services/{service}/restart")]
    [PveApi("POST", "/nodes/{node}/services/{service}/reload")]
    public Task<string> ServiceCommandAsync(string node, string service, string verb,
        CancellationToken ct = default)
    {
        if (verb is not ("start" or "stop" or "restart" or "reload"))
            throw new ArgumentOutOfRangeException(nameof(verb), verb, null);
        return Api.PostActionAsync($"{N(node)}/services/{Seg(service)}/{verb}", null, ct);
    }

    /// <summary>모두 이전 — max-workers 는 옛 이름 maxworkers 로 바꿔 보낸다(7.0~ 모두 안다).</summary>
    [PveApi("POST", "/nodes/{node}/migrateall")]
    public Task<string> MigrateAllAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        var body = form.ToDictionary(kv => kv.Key == "max-workers" ? "maxworkers" : kv.Key, kv => kv.Value);
        return Api.PostActionAsync($"{N(node)}/migrateall", body, ct);
    }

    // ------------------------------------------------------------ 복제(노드 기준 상태)

    /// <param name="guest">주면 그 게스트의 작업만.</param>
    [PveApi("GET", "/nodes/{node}/replication")]
    public Task<IReadOnlyList<Row>> ReplicationAsync(string node, int? guest = null, CancellationToken ct = default)
    {
        return Api.GetTableAsync(guest is { } id ? $"{N(node)}/replication?guest={id}" : $"{N(node)}/replication", ct);
    }

    [PveApi("POST", "/nodes/{node}/replication/{id}/schedule_now")]
    public Task<string> RunReplicationNowAsync(string node, string id, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{N(node)}/replication/{Seg(id)}/schedule_now", null, ct);
    }

    [PveApi("GET", "/nodes/{node}/replication/{id}/log")]
    public Task<IReadOnlyList<Row>> ReplicationLogAsync(string node, string id, int limit = 500,
        CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{N(node)}/replication/{Seg(id)}/log?limit={limit}", ct);
    }

    // ------------------------------------------------------------ 네트워크

    [PveApi("GET", "/nodes/{node}/network")]
    public Task<IReadOnlyList<Row>> ListNetworkAsync(string node, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{N(node)}/network", ct);
    }

    [PveApi("POST", "/nodes/{node}/network")]
    [PveParam("bridge_vids", "8.2")]
    public Task<string> CreateInterfaceAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{N(node)}/network", Supported(form), ct);
    }

    [PveApi("PUT", "/nodes/{node}/network/{iface}")]
    [PveParam("bridge_vids", "8.2")]
    public Task<string> UpdateInterfaceAsync(string node, string iface, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{N(node)}/network/{Seg(iface)}", Supported(form), ct);
    }

    [PveApi("DELETE", "/nodes/{node}/network/{iface}")]
    public Task<string> DeleteInterfaceAsync(string node, string iface, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{N(node)}/network/{Seg(iface)}", ct);
    }

    /// <summary>바꾼 네트워크 설정을 적용한다(작업 UPID).</summary>
    [PveApi("PUT", "/nodes/{node}/network")]
    public Task<string> ApplyNetworkAsync(string node, CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{N(node)}/network", new Dictionary<string, string>(), ct);
    }

    /// <summary>적용 전 변경을 버린다.</summary>
    [PveApi("DELETE", "/nodes/{node}/network")]
    public Task<string> RevertNetworkAsync(string node, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{N(node)}/network", ct);
    }

    // ------------------------------------------------------------ 인증서

    [PveApi("GET", "/nodes/{node}/certificates/info")]
    public Task<IReadOnlyList<Row>> CertificatesAsync(string node, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{N(node)}/certificates/info", ct);
    }

    /// <summary>사용자 인증서 올리기 — certificates(체인)·key. 바꾼 뒤 pveproxy 를 다시 띄운다.</summary>
    [PveApi("POST", "/nodes/{node}/certificates/custom")]
    public Task<string> UploadCertificateAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        var body = form.ToDictionary(kv => kv.Key, kv => kv.Value);
        body["force"] = "1";
        body["restart"] = "1";
        return Api.PostActionAsync($"{N(node)}/certificates/custom", body, ct);
    }

    [PveApi("DELETE", "/nodes/{node}/certificates/custom")]
    public Task<string> DeleteCustomCertificateAsync(string node, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{N(node)}/certificates/custom?restart=1", ct);
    }

    /// <summary>ACME 인증서를 인증 기관에서 폐기하고 노드에서 지운다(작업 UPID) — 자체 서명 인증서로 돌아간다.</summary>
    [PveApi("DELETE", "/nodes/{node}/certificates/acme/certificate")]
    public Task<string> RevokeAcmeCertificateAsync(string node, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{N(node)}/certificates/acme/certificate", ct);
    }

    /// <summary>ACME 인증서 새로 받기(작업 UPID).</summary>
    [PveApi("POST", "/nodes/{node}/certificates/acme/certificate")]
    public Task<string> OrderAcmeCertificateAsync(string node, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{N(node)}/certificates/acme/certificate",
            new Dictionary<string, string> { ["force"] = "1" }, ct);
    }

    /// <summary>ACME 인증서 갱신(작업 UPID).</summary>
    [PveApi("PUT", "/nodes/{node}/certificates/acme/certificate")]
    public Task<string> RenewAcmeCertificateAsync(string node, CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{N(node)}/certificates/acme/certificate",
            new Dictionary<string, string> { ["force"] = "1" }, ct);
    }

    // ------------------------------------------------------------ APT

    [PveApi("GET", "/nodes/{node}/apt/update")]
    public Task<IReadOnlyList<Row>> ListUpdatesAsync(string node, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{N(node)}/apt/update", ct);
    }

    /// <summary>중요 Proxmox 패키지 버전 — Package·Version·OldVersion·CurrentState 등(웹 UI 의 패키지 버전 창).</summary>
    [PveApi("GET", "/nodes/{node}/apt/versions")]
    public Task<IReadOnlyList<Row>> PackageVersionsAsync(string node, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{N(node)}/apt/versions", ct);
    }

    /// <summary>시스템 보고서(pvereport) — 지원 문의에 붙이는 긴 글.</summary>
    [PveApi("GET", "/nodes/{node}/report")]
    public Task<string> ReportAsync(string node, CancellationToken ct = default)
    {
        return Api.GetTextAsync($"{N(node)}/report", LongRequestTimeout, ct);
    }

    /// <summary>패키지 목록 새로 고침(작업 UPID).</summary>
    [PveApi("POST", "/nodes/{node}/apt/update")]
    public Task<string> RefreshUpdatesAsync(string node, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{N(node)}/apt/update", null, ct);
    }

    [PveApi("GET", "/nodes/{node}/apt/changelog")]
    public Task<string> ChangelogAsync(string node, string package, string version, CancellationToken ct = default)
    {
        return Api.GetTextAsync($"{N(node)}/apt/changelog?name={Uri.EscapeDataString(package)}"
                                + $"&version={Uri.EscapeDataString(version)}", ct);
    }

    /// <summary>저장소 줄 켜기·끄기 — path·index·enabled·digest.</summary>
    [PveApi("POST", "/nodes/{node}/apt/repositories")]
    public Task<string> ChangeRepositoryAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{N(node)}/apt/repositories", form, ct);
    }

    /// <summary>표준 저장소 더하기 — handle·digest.</summary>
    [PveApi("PUT", "/nodes/{node}/apt/repositories")]
    public Task<string> AddRepositoryAsync(string node, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{N(node)}/apt/repositories", form, ct);
    }

    /// <summary>노드 백업 기본값(/etc/vzdump.conf) — storage·mode·compress 등 설정된 것만.</summary>
    [PveApi("GET", "/nodes/{node}/vzdump/defaults")]
    public Task<Row> VzdumpDefaultsAsync(string node, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"{N(node)}/vzdump/defaults", ct);
    }

    /// <summary>
    ///     PCI 장치가 제공하는 중개 장치(mdev) 유형 — type·available·description. 8.2 에서 경로 변수 이름만 바뀌었다.
    /// </summary>
    [PveApi("GET", "/nodes/{node}/hardware/pci/{pci-id-or-mapping}/mdev", Since = "8.2")]
    [PveApi("GET", "/nodes/{node}/hardware/pci/{pciid}/mdev", Until = "8.2", Legacy = true)]
    public Task<IReadOnlyList<Row>> MdevTypesAsync(string node, string pciId, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{N(node)}/hardware/pci/{Seg(pciId)}/mdev", ct);
    }

    /// <summary>VM 에 켜고 끌 수 있는 CPU 플래그(9.0+) — name·description·supported-on.</summary>
    [PveApi("GET", "/nodes/{node}/capabilities/qemu/cpu-flags", Since = "9.0")]
    public async Task<IReadOnlyList<Row>> CpuFlagsAsync(string node, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync($"{N(node)}/capabilities/qemu/cpu-flags", ct).ConfigureAwait(false);
    }
}
