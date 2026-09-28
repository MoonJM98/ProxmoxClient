using ProxmoxClient.Core.Api.Versioning;
using ProxmoxClient.Core.Models;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     게스트(VM·CT) — 만들기·설정(비동기 POST)·이전·템플릿·삭제·디스크 옮기기/크기·Cloud-Init·모니터·백업 설정.
///     버전 차이(서버가 모르는 설정은 보내지 않는다): VM tpmstate0(7.1)·affinity(7.2)·ciupgrade(8.0)·
///     amd-sev·import-working-storage(8.2)·virtiofs(8.3)·allow-ksm·intel-tdx·ha-managed(9.0),
///     CT dev(8.1)·entrypoint·env·ha-managed(9.0), 디스크 다른 게스트로 넘기기(7.1), CT 이전 target-storage(7.1),
///     VM 이전 with-conntrack-state(9.0), Cloud-Init 다시 만들기(7.2).
/// </summary>
public sealed class GuestsApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private static string G(string node, ResourceKind kind, int vmid) =>
        $"nodes/{Seg(node)}/{kind.ApiSegment()}/{vmid}";

    /// <summary>새 게스트 또는 백업에서 복원(archive)(작업 UPID).</summary>
    [PveApi("POST", "/nodes/{node}/qemu")]
    [PveApi("POST", "/nodes/{node}/lxc")]
    [PveParam("tpmstate0", "7.1")]
    [PveParam("affinity", "7.2")]
    [PveParam("ciupgrade", "8.0")]
    [PveParam("dev[n]", "8.1")]
    [PveParam("amd-sev", "8.2")]
    [PveParam("import-working-storage", "8.2")]
    [PveParam("virtiofs[n]", "8.3")]
    [PveParam("allow-ksm", "9.0")]
    [PveParam("intel-tdx", "9.0")]
    [PveParam("entrypoint", "9.0")]
    [PveParam("env", "9.0")]
    [PveParam("ha-managed", "9.0")]
    public Task<string> CreateAsync(string node, ResourceKind kind, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"nodes/{Seg(node)}/{kind.ApiSegment()}", Supported(form), ct);
    }

    /// <summary>VM 설정 바꾸기 — 비동기(POST, 작업 UPID). 디스크 만들기처럼 오래 걸리는 변경에 쓴다.</summary>
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/config")]
    [PveParam("tpmstate0", "7.1")]
    [PveParam("affinity", "7.2")]
    [PveParam("ciupgrade", "8.0")]
    [PveParam("amd-sev", "8.2")]
    [PveParam("import-working-storage", "8.2")]
    [PveParam("virtiofs[n]", "8.3")]
    [PveParam("allow-ksm", "9.0")]
    [PveParam("intel-tdx", "9.0")]
    public Task<string> UpdateConfigAsync(PveResource guest, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{G(guest.Node, guest.Kind, guest.VmId)}/config", Supported(form), ct);
    }

    /// <summary>
    ///     설정 바꾸기(PUT …/config) — 서버가 모르는 설정은 값·delete 목록 모두에서 뺀다
    ///     (예: 9.0 전 서버에 allow-ksm 을 지우라고 보내면 요청 전체가 거절된다).
    /// </summary>
    [PveApi("PUT", "/nodes/{node}/qemu/{vmid}/config")]
    [PveApi("PUT", "/nodes/{node}/lxc/{vmid}/config")]
    [PveParam("tpmstate0", "7.1")]
    [PveParam("affinity", "7.2")]
    [PveParam("ciupgrade", "8.0")]
    [PveParam("dev[n]", "8.1")]
    [PveParam("amd-sev", "8.2")]
    [PveParam("virtiofs[n]", "8.3")]
    [PveParam("allow-ksm", "9.0")]
    [PveParam("intel-tdx", "9.0")]
    [PveParam("entrypoint", "9.0")]
    [PveParam("env", "9.0")]
    public Task<string> SetConfigAsync(string node, ResourceKind kind, int vmid,
        IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{G(node, kind, vmid)}/config", Supported(form), ct);
    }

    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/migrate")]
    [PveApi("POST", "/nodes/{node}/lxc/{vmid}/migrate")]
    [PveParam("target-storage", "7.1")]
    [PveParam("with-conntrack-state", "9.0")]
    public Task<string> MigrateAsync(PveResource guest, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{G(guest.Node, guest.Kind, guest.VmId)}/migrate", Supported(form), ct);
    }

    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/template")]
    [PveApi("POST", "/nodes/{node}/lxc/{vmid}/template")]
    public Task<string> ConvertToTemplateAsync(PveResource guest, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{G(guest.Node, guest.Kind, guest.VmId)}/template", null, ct);
    }

    /// <summary>게스트 삭제(작업 UPID) — purge: 복제·백업 작업·HA 에서도 뺀다.</summary>
    [PveApi("DELETE", "/nodes/{node}/qemu/{vmid}")]
    [PveApi("DELETE", "/nodes/{node}/lxc/{vmid}")]
    public Task<string> DestroyAsync(PveResource guest, bool purge, bool destroyUnreferencedDisks,
        CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{G(guest.Node, guest.Kind, guest.VmId)}?purge={(purge ? 1 : 0)}"
                                     + $"&destroy-unreferenced-disks={(destroyUnreferencedDisks ? 1 : 0)}", ct);
    }

    /// <summary>
    ///     디스크를 다른 저장소로 옮기거나(storage) 다른 게스트에 넘긴다(target-vmid…, 7.1+). 작업 UPID.
    /// </summary>
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/move_disk")]
    [PveApi("POST", "/nodes/{node}/lxc/{vmid}/move_volume")]
    [PveParam("target-vmid", "7.1")]
    [PveParam("target-disk", "7.1")]
    [PveParam("target-volume", "7.1")]
    [PveParam("target-digest", "7.1")]
    public Task<string> MoveDiskAsync(PveResource guest, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        var verb = guest.Kind == ResourceKind.Lxc ? "move_volume" : "move_disk";
        return Api.PostActionAsync($"{G(guest.Node, guest.Kind, guest.VmId)}/{verb}", Supported(form), ct);
    }

    [PveApi("PUT", "/nodes/{node}/qemu/{vmid}/resize")]
    [PveApi("PUT", "/nodes/{node}/lxc/{vmid}/resize")]
    public Task<string> ResizeDiskAsync(PveResource guest, string disk, string size, CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{G(guest.Node, guest.Kind, guest.VmId)}/resize",
            new Dictionary<string, string> { ["disk"] = disk, ["size"] = size }, ct);
    }

    /// <summary>Cloud-Init 드라이브를 바뀐 설정으로 다시 만든다(7.2+).</summary>
    [PveApi("PUT", "/nodes/{node}/qemu/{vmid}/cloudinit", Since = "7.2")]
    public async Task<string> RegenerateCloudInitAsync(string node, int vmid, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PutActionAsync($"{G(node, ResourceKind.Qemu, vmid)}/cloudinit",
            new Dictionary<string, string>(), ct).ConfigureAwait(false);
    }

    /// <summary>QEMU 모니터 명령 — 출력 글.</summary>
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/monitor")]
    public Task<string> MonitorAsync(string node, int vmid, string command, CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{G(node, ResourceKind.Qemu, vmid)}/monitor",
            new Dictionary<string, string> { ["command"] = command }, ct);
    }

    /// <summary>백업 파일 안의 게스트 설정 원문.</summary>
    [PveApi("GET", "/nodes/{node}/vzdump/extractconfig")]
    public Task<string> BackupConfigAsync(string node, string volume, CancellationToken ct = default)
    {
        return Api.GetTextAsync($"nodes/{Seg(node)}/vzdump/extractconfig?volume={Uri.EscapeDataString(volume)}", ct);
    }

    // ------------------------------------------------------------ 노드 기능(게스트 편집용)

    /// <param name="arch">x86_64·aarch64 — 9.1+ 서버만 안다(낮은 서버엔 보내지 않는다).</param>
    [PveApi("GET", "/nodes/{node}/capabilities/qemu/cpu")]
    [PveParam("arch", "9.1")]
    public Task<IReadOnlyList<Row>> CpuModelsAsync(string node, string? arch = null, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"nodes/{Seg(node)}/capabilities/qemu/cpu{ArchQuery(arch)}", ct);
    }

    [PveApi("GET", "/nodes/{node}/capabilities/qemu/machines")]
    [PveParam("arch", "9.1")]
    public Task<IReadOnlyList<Row>> MachinesAsync(string node, string? arch = null, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"nodes/{Seg(node)}/capabilities/qemu/machines{ArchQuery(arch)}", ct);
    }

    /// <param name="bus">pci·usb.</param>
    [PveApi("GET", "/nodes/{node}/hardware/pci")]
    [PveApi("GET", "/nodes/{node}/hardware/usb")]
    public Task<IReadOnlyList<Row>> HostDevicesAsync(string node, string bus, CancellationToken ct = default)
    {
        var kind = bus is "pci" or "usb" ? bus : throw new ArgumentException($"Unknown bus '{bus}'", nameof(bus));
        return Api.GetTableAsync($"nodes/{Seg(node)}/hardware/{kind}", ct);
    }

    private string ArchQuery(string? arch)
    {
        return arch is { Length: > 0 } && Api.Supports(new PveApiVersion(9, 1))
            ? $"?arch={Uri.EscapeDataString(arch)}"
            : string.Empty;
    }

    // ------------------------------------------------------------ Cloud-Init 보기(7.2~ 대기 값, 생성된 설정)

    /// <summary>Cloud-Init 항목의 지금 값과 적용 대기 값 — key·value·pending·delete(7.2+).</summary>
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/cloudinit", Since = "7.2")]
    public async Task<IReadOnlyList<Row>> CloudInitPendingAsync(string node, int vmid,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync($"{G(node, ResourceKind.Qemu, vmid)}/cloudinit", ct).ConfigureAwait(false);
    }

    /// <summary>서버가 만들어 둔 Cloud-Init 설정 원문 — type: user·network·meta.</summary>
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/cloudinit/dump")]
    public Task<string> CloudInitDumpAsync(string node, int vmid, string type, CancellationToken ct = default)
    {
        if (type is not ("user" or "network" or "meta")) throw new ArgumentOutOfRangeException(nameof(type));
        return Api.GetTextAsync($"{G(node, ResourceKind.Qemu, vmid)}/cloudinit/dump?type={type}", ct);
    }

    // ------------------------------------------------------------ 게스트 에이전트 동작(실행 중 VM)

    /// <summary>게스트 파일 시스템 TRIM — 씬 저장소에 빈 공간을 돌려준다. 응답은 에이전트 결과(보기 좋게 적은 글).</summary>
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/agent/fstrim")]
    public async Task<string> AgentFsTrimAsync(string node, int vmid, CancellationToken ct = default)
    {
        var result = await Api.SendForObjectAsync(HttpMethod.Post, $"{G(node, ResourceKind.Qemu, vmid)}/agent/fstrim",
            new Dictionary<string, string>(), ct, LongRequestTimeout).ConfigureAwait(false);
        return result.TryGetValue("result", out var text) ? text : string.Empty;
    }

    /// <summary>게스트 안 사용자 암호 바꾸기 — 암호는 요청 본문으로만 보낸다.</summary>
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/agent/set-user-password")]
    public Task<IReadOnlyDictionary<string, string>> AgentSetUserPasswordAsync(string node, int vmid,
        string username, string password, CancellationToken ct = default)
    {
        return Api.SendForObjectAsync(HttpMethod.Post, $"{G(node, ResourceKind.Qemu, vmid)}/agent/set-user-password",
            new Dictionary<string, string> { ["username"] = username, ["password"] = password }, ct);
    }

    /// <summary>CT 네트워크 인터페이스와 IP(8.1+) — name·hwaddr·inet·inet6.</summary>
    [PveApi("GET", "/nodes/{node}/lxc/{vmid}/interfaces", Since = "8.1")]
    public async Task<IReadOnlyList<Row>> CtInterfacesAsync(string node, int vmid, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync($"{G(node, ResourceKind.Lxc, vmid)}/interfaces", ct).ConfigureAwait(false);
    }

    /// <summary>에이전트에 물어볼 수 있는 읽기 전용 정보(보기 좋게 들여쓴 JSON 으로 돌려준다).</summary>
    public static IReadOnlyList<string> AgentInfoCommands { get; } =
    [
        "get-fsinfo", "get-users", "get-time", "get-timezone", "get-vcpus", "get-memory-block-info",
        "get-memory-blocks", "info"
    ];

    /// <summary>
    ///     게스트 에이전트 읽기 전용 정보 — command 는 <see cref="AgentInfoCommands" /> 중 하나(파일 시스템·로그인 사용자·
    ///     시각·vCPU·메모리 블록·에이전트 정보). 응답의 result 를 보기 좋게 들여쓴 JSON 으로 돌려준다.
    /// </summary>
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/get-fsinfo")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/get-users")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/get-time")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/get-timezone")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/get-vcpus")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/get-memory-block-info")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/get-memory-blocks")]
    [PveApi("GET", "/nodes/{node}/qemu/{vmid}/agent/info")]
    public Task<string> AgentInfoJsonAsync(string node, int vmid, string command, CancellationToken ct = default)
    {
        if (!AgentInfoCommands.Contains(command)) throw new ArgumentOutOfRangeException(nameof(command), command, null);
        return Api.GetPrettyJsonAsync($"{G(node, ResourceKind.Qemu, vmid)}/agent/{command}", ct);
    }

    /// <summary>에이전트가 응답하는지(작업 없이 바로 끝난다) — 꺼져 있으면 서버 오류가 올라간다.</summary>
    [PveApi("POST", "/nodes/{node}/qemu/{vmid}/agent/ping")]
    public Task<IReadOnlyDictionary<string, string>> AgentPingAsync(string node, int vmid,
        CancellationToken ct = default)
    {
        return Api.SendForObjectAsync(HttpMethod.Post, $"{G(node, ResourceKind.Qemu, vmid)}/agent/ping",
            new Dictionary<string, string>(), ct);
    }

    /// <summary>
    ///     VM 의 안 쓰는 디스크(unusedN)를 설정에서 빼고 저장소에서도 지운다 — idlist 예: unused0,unused1. 되돌릴 수 없다.
    /// </summary>
    [PveApi("PUT", "/nodes/{node}/qemu/{vmid}/unlink")]
    public Task<string> UnlinkDisksAsync(string node, int vmid, IReadOnlyList<string> keys,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{G(node, ResourceKind.Qemu, vmid)}/unlink",
            new Dictionary<string, string> { ["idlist"] = string.Join(',', keys), ["force"] = "1" }, ct);
    }
}
