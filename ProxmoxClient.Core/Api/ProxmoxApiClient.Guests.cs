using System.Globalization;
using System.Text.Json;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Api;

/// <summary>게스트(VM·CT) — 목록·전원·복제·백업·설정·만들기.</summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>Lists the VMs or containers on a node (GET /nodes/{node}/qemu|lxc).</summary>
    [Versioning.PveApi("GET", "/nodes/{node}/qemu")]
    [Versioning.PveApi("GET", "/nodes/{node}/lxc")]
    public Task<IReadOnlyList<PveResource>> GetGuestsAsync(string node, ResourceKind kind,
        CancellationToken ct = default)
    {
        return GetListAsync<PveResource, ResourceDto>(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}",
            dto => MapResource(dto, node),
            ct);
    }
    /// <summary>Starts a guest (POST /nodes/{node}/{qemu|lxc}/{vmid}/status/start). Returns the task UPID.</summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/status/start")]
    [Versioning.PveApi("POST", "/nodes/{node}/lxc/{vmid}/status/start")]
    public Task<string> StartGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/start", null, ct);
    }
    /// <summary>Force-stops a guest (POST .../status/stop). Returns the task UPID.</summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/status/stop")]
    [Versioning.PveApi("POST", "/nodes/{node}/lxc/{vmid}/status/stop")]
    public Task<string> StopGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/stop", null, ct);
    }
    /// <summary>Gracefully shuts a guest down (POST .../status/shutdown). Returns the task UPID.</summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/status/shutdown")]
    [Versioning.PveApi("POST", "/nodes/{node}/lxc/{vmid}/status/shutdown")]
    public Task<string> ShutdownGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/shutdown", null, ct);
    }
    /// <summary>Reboots a running guest (POST .../status/reboot). Returns the task UPID.</summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/status/reboot")]
    [Versioning.PveApi("POST", "/nodes/{node}/lxc/{vmid}/status/reboot")]
    public Task<string> RebootGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/reboot", null, ct);
    }
    /// <summary>
    ///     VM 강제 재설정(POST .../status/reset) — 전원 버튼을 누른 것처럼 게스트 OS 를 거치지 않고 다시 시작한다.
    ///     QEMU 만 있다. 작업 UPID 를 돌려준다.
    /// </summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/status/reset")]
    public Task<string> ResetGuestAsync(string node, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/qemu/{vmid}/status/reset", null, ct);
    }
    /// <summary>Pauses a running VM (POST .../status/suspend). QEMU only. Returns the task UPID.</summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/status/suspend")]
    [Versioning.PveApi("POST", "/nodes/{node}/lxc/{vmid}/status/suspend")]
    public Task<string> SuspendGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/suspend", null, ct);
    }
    /// <summary>Resumes a paused VM (POST .../status/resume). QEMU only. Returns the task UPID.</summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/status/resume")]
    [Versioning.PveApi("POST", "/nodes/{node}/lxc/{vmid}/status/resume")]
    public Task<string> ResumeGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/resume", null, ct);
    }
    /// <summary>
    ///     Hibernates a running VM to disk — POST .../status/suspend with todisk=1 (there is no separate
    ///     hibernate endpoint). QEMU only. Returns the task UPID.
    /// </summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/status/suspend")]
    public Task<string> HibernateGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/suspend",
            new Dictionary<string, string> { ["todisk"] = "1" }, ct);
    }
    /// <summary>
    ///     Clones a guest (POST .../clone). full=true → 전체 복제, false → 연결 복제.
    ///     Returns the task UPID.
    /// </summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/clone")]
    [Versioning.PveApi("POST", "/nodes/{node}/lxc/{vmid}/clone")]
    public Task<string> CloneGuestAsync(
        string node, ResourceKind kind, int vmid, int newId, string? name, bool full,
        string? targetNode = null, string? storage = null, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["newid"] = newId.ToString(),
            ["full"] = full ? "1" : "0"
        };
        if (!string.IsNullOrWhiteSpace(name)) form["name"] = name;

        if (!string.IsNullOrWhiteSpace(targetNode)) form["target"] = targetNode;

        if (!string.IsNullOrWhiteSpace(storage)) form["storage"] = storage;

        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/clone", form, ct);
    }
    /// <summary>
    ///     Starts a vzdump backup of one guest (POST nodes/{node}/vzdump, vmid=…). mode: snapshot|suspend|stop.
    ///     compress: "" (none)|zstd|lzo|gzip. Returns the task UPID.
    ///     options: 웹 UI 백업 창의 추가 칸(protected·notes-template·notification-mode) — 비었으면 보내지 않는다.
    /// </summary>
    [Versioning.PveApi("POST", "/nodes/{node}/vzdump")]
    [Versioning.PveParam("notes-template", "7.1")]
    [Versioning.PveParam("protected", "7.1")]
    [Versioning.PveParam("notification-mode", "8.1")]
    public Task<string> BackupGuestAsync(
        string node, ResourceKind kind, int vmid, string storage, string mode, string compress,
        IReadOnlyDictionary<string, string>? options = null, CancellationToken ct = default)
    {
        // 백업은 게스트 경로가 아니라 노드의 vzdump 에 게스트 번호를 넘긴다(VM·CT 같은 경로)
        var form = new Dictionary<string, string>
        {
            ["vmid"] = vmid.ToString(CultureInfo.InvariantCulture),
            ["storage"] = storage,
            ["mode"] = mode
        };
        if (!string.IsNullOrEmpty(compress)) form["compress"] = compress;
        foreach (var (key, value) in options ?? new Dictionary<string, string>())
            if (value.Length > 0 && VzdumpOptions.Contains(key)) form[key] = value;

        return PostWriteAsync($"nodes/{Escape(node)}/vzdump", form, ct);
    }
    /// <summary>
    ///     Gets the guest's raw configuration (GET .../config) as a string map —
    ///     every value stringified so callers can edit and PUT back the changed keys.
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/qemu/{vmid}/config")]
    [Versioning.PveApi("GET", "/nodes/{node}/lxc/{vmid}/config")]
    public async Task<IReadOnlyDictionary<string, string>> GetGuestConfigAsync(
        string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        var data = await GetJsonAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/config", ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data.ValueKind != JsonValueKind.Object) return map;

        foreach (var property in data.EnumerateObject())
            map[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    => property.Value.GetRawText().Trim('"'),
                _ => property.Value.ToString()
            };

        return map;
    }
    /// <summary>Applies changed config keys (PUT .../config). Server validates constraints.</summary>
    [Versioning.PveApi("PUT", "/nodes/{node}/qemu/{vmid}/config")]
    [Versioning.PveApi("PUT", "/nodes/{node}/lxc/{vmid}/config")]
    public Task<string> UpdateGuestConfigAsync(
        string node, ResourceKind kind, int vmid, IReadOnlyDictionary<string, string> changes,
        CancellationToken ct = default)
    {
        // 서버 버전이 모르는 설정은 영역 API 가 걸러 낸다
        return Guests.SetConfigAsync(node, kind, vmid, changes, ct);
    }
    /// <summary>
    ///     게스트 실시간 상태 (GET nodes/{node}/{kind}/{vmid}/status/current).
    ///     /cluster/resources 의 상태는 pvestatd 주기로 늦게 반영되므로 전원 작업 직후엔 이 값을 쓴다.
    ///     일시정지된 VM 은 status 가 running 이고 qmpstatus 가 paused 이므로 "paused" 로 돌려준다. 값이 없으면 빈 문자열.
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/qemu/{vmid}/status/current")]
    [Versioning.PveApi("GET", "/nodes/{node}/lxc/{vmid}/status/current")]
    public async Task<string> GetGuestCurrentStatusAsync(string node, ResourceKind kind, int vmid,
        CancellationToken ct = default)
    {
        var data = await GetJsonAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/current", ct)
            .ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object) return string.Empty;

        return string.Equals(GetString(data, "qmpstatus"), "paused", StringComparison.OrdinalIgnoreCase)
            ? "paused"
            : GetString(data, "status");
    }
    /// <summary>클러스터에서 사용 가능한 다음 VMID (GET cluster/nextid). 해석 불가 시 null.</summary>
    [Versioning.PveApi("GET", "/cluster/nextid")]
    public async Task<int?> GetNextVmIdAsync(CancellationToken ct = default)
    {
        var data = await GetJsonAsync("cluster/nextid", ct).ConfigureAwait(false);
        return data.ValueKind switch
        {
            JsonValueKind.Number when data.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(data.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
    /// <summary>노드의 브리지 인터페이스 이름 목록 (GET nodes/{node}/network?type=any_bridge).</summary>
    [Versioning.PveApi("GET", "/nodes/{node}/network")]
    public async Task<IReadOnlyList<string>> GetNodeBridgesAsync(string node, CancellationToken ct = default)
    {
        var data = await GetJsonAsync($"nodes/{Escape(node)}/network?type=any_bridge", ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Array) return [];

        return data.EnumerateArray()
            .Select(item => GetString(item, "iface"))
            .Where(name => name.Length > 0)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    /// <summary>
    ///     Lists files in a storage (GET nodes/{node}/storage/{storage}/content).
    ///     contentType: "iso", "vztmpl", "backup", "images", "rootdir" (빈 값=전체).
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/storage/{storage}/content")]
    public async Task<IReadOnlyList<PveContentFile>> GetStorageContentAsync(
        string node, string storage, string? contentType = null, CancellationToken ct = default)
    {
        var query = string.IsNullOrEmpty(contentType) ? string.Empty : $"?content={Uri.EscapeDataString(contentType)}";
        var data = await GetJsonAsync(
            $"nodes/{Escape(node)}/storage/{Escape(storage)}/content{query}", ct).ConfigureAwait(false);
        var list = new List<PveContentFile>();
        if (data.ValueKind != JsonValueKind.Array) return list;

        foreach (var item in data.EnumerateArray())
        {
            var volid = GetString(item, "volid");
            if (volid.Length == 0) continue;

            list.Add(new PveContentFile
            {
                Volid = volid,
                Content = GetString(item, "content"),
                Format = GetString(item, "format"),
                SizeBytes = GetLong(item, "size")
            });
        }

        return list;
    }
    /// <summary>Creates a VM (POST nodes/{node}/qemu). ISO가 없으면 빈 디스크로 생성.</summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu")]
    public Task<string> CreateQemuAsync(
        string node, int vmid, string? name, int cores, int memoryMiB,
        string? isoVolid, string diskStorage, int diskGb, string bridge, string ostype = "l26",
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["vmid"] = vmid.ToString(),
            ["cores"] = cores.ToString(),
            ["memory"] = memoryMiB.ToString(),
            ["ostype"] = ostype,
            ["scsihw"] = "virtio-scsi-pci",
            ["net0"] = $"virtio,bridge={bridge}",
            ["scsi0"] = $"{diskStorage}:{diskGb}"
        };
        if (!string.IsNullOrWhiteSpace(name)) form["name"] = name;

        if (!string.IsNullOrWhiteSpace(isoVolid)) form["ide2"] = $"{isoVolid},media=cdrom";

        return PostWriteAsync($"nodes/{Escape(node)}/qemu", form, ct);
    }
    /// <summary>Creates an LXC container (POST nodes/{node}/lxc).</summary>
    [Versioning.PveApi("POST", "/nodes/{node}/lxc")]
    public Task<string> CreateLxcAsync(
        string node, int vmid, string hostname, int cores, int memoryMiB, int swapMiB,
        string templateVolid, string diskStorage, int diskGb, string bridge,
        string ipAddress, string rootPassword, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["vmid"] = vmid.ToString(),
            ["hostname"] = hostname,
            ["cores"] = cores.ToString(),
            ["memory"] = memoryMiB.ToString(),
            ["swap"] = swapMiB.ToString(),
            ["ostemplate"] = templateVolid,
            ["rootfs"] = $"{diskStorage}:{diskGb}",
            ["net0"] = $"name=eth0,bridge={bridge},ip={ipAddress},firewall=1",
            ["password"] = rootPassword,
            ["unprivileged"] = "1"
        };
        return PostWriteAsync($"nodes/{Escape(node)}/lxc", form, ct);
    }
}
