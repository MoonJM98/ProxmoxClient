using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProxmoxClient.Core.Localization;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     Async client for the Proxmox VE REST API (api2/json).
///     - Password auth: <see cref="LoginAsync" /> exchanges credentials for a ticket
///     (PVEAuthCookie) + CSRF token; every write (POST/PUT/DELETE) then carries the
///     CSRFPreventionToken header.
///     - API-token auth: no login round-trip; requests carry
///     "Authorization: PVEAPIToken={tokenId}={secret}".
///     - Optional HTTP/HTTPS/SOCKS5 proxy and self-signed certificate acceptance.
/// </summary>
public sealed class ProxmoxApiClient : IDisposable
{
    private const int ResponseExcerptLength = 300;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>티켓 갱신 주기 — PVE 티켓 유효 시간(2시간)보다 충분히 앞서 갱신한다.</summary>
    private static readonly TimeSpan TicketRenewAge = TimeSpan.FromMinutes(60);

    private readonly HttpClient _http;

    private readonly SemaphoreSlim _renewGate = new(1, 1);
    private volatile AuthSession? _auth;
    private bool _disposed;

    /// <summary>Builds a client for the given profile (proxy + TLS validation are applied here).</summary>
    public ProxmoxApiClient(ConnectionProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.Host)) throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_01"));

        var handler = new HttpClientHandler
        {
            UseProxy = profile.ProxyMode != ProxyMode.None
        };

        // 모든 인증서 허용 대신 공인 CA 검증 또는 신뢰한 지문(TOFU)만 통과
        CertificateValidator = new ServerCertificateValidator(profile);
        handler.ServerCertificateCustomValidationCallback =
            (_, certificate, _, errors) => CertificateValidator.Validate(certificate, errors);

        ConfigureProxy(handler, profile);

        _http = new HttpClient(handler)
        {
            BaseAddress = BuildBaseAddress(profile),
            Timeout = TimeSpan.FromSeconds(30)
        };

        if (profile.AuthMode == AuthMode.ApiToken)
        {
            var header = $"PVEAPIToken={profile.ApiTokenId}={SecureStringHelper.ToPlainString(profile.ApiTokenSecret)}";
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);
        }
    }

    /// <summary>True once ticket auth succeeded (or immediately in token mode).</summary>
    public bool IsAuthenticated =>
        Profile.AuthMode == AuthMode.ApiToken || _auth is not null;

    /// <summary>세션 인증 티켓(비밀번호 모드). 콘솔 웹소켓 연결에 필요.</summary>
    public string? AuthTicket => _auth?.Ticket;

    /// <summary>이 클라이언트를 만든 연결 프로필.</summary>
    public ConnectionProfile Profile { get; }

    /// <summary>서버 인증서 검증기(API·콘솔 웹소켓 공용) — 거부 시 신뢰 확인 정보를 보관한다.</summary>
    internal ServerCertificateValidator CertificateValidator { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _http.Dispose();
        _renewGate.Dispose();
    }

    /// <summary>
    ///     Authenticates with the profile credentials. Password mode performs
    ///     POST /access/ticket and stores the ticket/CSRF token; token mode is a no-op.
    /// </summary>
    public async Task LoginAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (Profile.AuthMode == AuthMode.ApiToken) return;

        if (string.IsNullOrWhiteSpace(Profile.UserName) || Profile.Password is null)
            throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_02"));

        await AcquireTicketAsync(SecureStringHelper.ToPlainString(Profile.Password) ?? string.Empty, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     비밀번호 모드 티켓을 만료 전에 갱신한다(<paramref name="force" /> 면 나이와 무관 — 401 응답 직후).
    ///     현재 티켓으로 재발급(PVE 표준: password 자리에 티켓)하고, 실패하면 보관 중인 비밀번호로 다시 로그인한다.
    ///     동시 요청이 몰려도 갱신은 한 번만 수행된다.
    /// </summary>
    private async Task EnsureFreshTicketAsync(bool force, CancellationToken ct)
    {
        if (Profile.AuthMode != AuthMode.Password || _auth is not { } current) return;

        if (!force && Environment.TickCount64 - current.IssuedAtTick < (long)TicketRenewAge.TotalMilliseconds) return;

        await _renewGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_auth, current)) return; // 기다리는 사이 다른 요청이 이미 갱신함

            try
            {
                await AcquireTicketAsync(current.Ticket, ct).ConfigureAwait(false);
            }
            catch (ProxmoxApiException) when (Profile.Password is not null)
            {
                await AcquireTicketAsync(SecureStringHelper.ToPlainString(Profile.Password) ?? string.Empty, ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _renewGate.Release();
        }
    }

    private async Task AcquireTicketAsync(string password, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["username"] = Profile.UserName,
            ["password"] = password
        };

        var data = await PostFormJsonAsync("access/ticket", form, false, ct).ConfigureAwait(false);
        var ticket = data.TryGetProperty("ticket", out var ticketEl) ? ticketEl.GetString() : null;
        var csrfToken = data.TryGetProperty("CSRFPreventionToken", out var csrfEl) ? csrfEl.GetString() : null;

        if (string.IsNullOrEmpty(ticket)) throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_03"));

        _auth = new AuthSession(ticket, csrfToken, Environment.TickCount64);
    }

    /// <summary>Gets the cluster version (GET /version) — handy as a connection test.</summary>
    public async Task<PveVersion> GetVersionAsync(CancellationToken ct = default)
    {
        var data = await GetJsonAsync("version", ct).ConfigureAwait(false);

        return new PveVersion
        {
            Version = GetString(data, "version"),
            Release = GetString(data, "release"),
            RepoId = GetString(data, "repoid")
        };
    }

    /// <summary>Lists all VMs and CTs in the cluster (GET /cluster/resources?type=vm).</summary>
    public Task<IReadOnlyList<PveResource>> GetClusterResourcesAsync(CancellationToken ct = default)
    {
        return GetListAsync<PveResource, ResourceDto>(
            "cluster/resources?type=vm",
            dto => MapResource(dto, dto.Node ?? string.Empty),
            ct);
    }

    /// <summary>Lists all storages in the cluster (GET /cluster/resources?type=storage).</summary>
    public Task<IReadOnlyList<PveStorage>> GetClusterStoragesAsync(CancellationToken ct = default)
    {
        return GetListAsync<PveStorage, ResourceDto>("cluster/resources?type=storage", MapStorage, ct);
    }

    /// <summary>Lists all nodes (GET /nodes).</summary>
    public Task<IReadOnlyList<PveNode>> GetNodesAsync(CancellationToken ct = default)
    {
        return GetListAsync<PveNode, NodeDto>(
            "nodes",
            dto => new PveNode
            {
                Node = dto.Node ?? string.Empty,
                Status = dto.Status ?? "unknown",
                CpuUsagePercent = (dto.Cpu ?? 0) * 100.0,
                CpuCount = dto.MaxCpu ?? 0,
                MemBytes = dto.Mem ?? 0,
                MaxMemBytes = dto.MaxMem ?? 0,
                DiskBytes = dto.Disk ?? 0,
                MaxDiskBytes = dto.MaxDisk ?? 0,
                UptimeSeconds = dto.Uptime ?? 0,
                Level = dto.Level
            },
            ct);
    }

    /// <summary>
    ///     클러스터 전체 현황을 한 번에 조회(GET /cluster/resources) — 게스트·노드·스토리지가 한 응답에 담긴다.
    ///     값은 pvestatd 주기로 모인 것이라 실시간보다 몇 초 늦을 수 있다(전원 작업 직후엔 status/current 로 보정).
    /// </summary>
    public async Task<ClusterOverview> GetClusterOverviewAsync(CancellationToken ct = default)
    {
        using var doc = await GetDocumentAsync("cluster/resources", ct).ConfigureAwait(false);
        var data = DataElement(doc);

        var guests = new List<PveResource>();
        var nodes = new List<PveNode>();
        var storages = new List<PveStorage>();
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var item in data.EnumerateArray())
            {
                if (item.Deserialize<ResourceDto>(JsonOptions) is not { } dto) continue;

                switch (dto.Type)
                {
                    case "qemu":
                    case "lxc":
                        guests.Add(MapResource(dto, dto.Node ?? string.Empty));
                        break;
                    case "node":
                        nodes.Add(MapNode(dto));
                        break;
                    case "storage":
                        storages.Add(MapStorage(dto));
                        break;
                }
            }

        return new ClusterOverview(guests, nodes, storages);
    }

    private static PveStorage MapStorage(ResourceDto dto)
    {
        return new PveStorage
        {
            Id = dto.Id ?? $"storage/{dto.Node}/{dto.Storage}",
            Storage = dto.Storage ?? dto.Id ?? string.Empty,
            Node = dto.Node ?? string.Empty,
            PluginType = dto.PluginType ?? string.Empty,
            Content = NormalizeCsv(dto.Content),
            DiskBytes = dto.Disk ?? 0,
            MaxDiskBytes = dto.MaxDisk ?? 0,
            Status = dto.Status ?? string.Empty
        };
    }

    private static PveNode MapNode(ResourceDto dto)
    {
        return new PveNode
        {
            Node = dto.Node ?? string.Empty,
            Status = dto.Status ?? "unknown",
            CpuUsagePercent = (dto.Cpu ?? 0) * 100.0,
            CpuCount = dto.MaxCpu ?? 0,
            MemBytes = dto.Mem ?? 0,
            MaxMemBytes = dto.MaxMem ?? 0,
            DiskBytes = dto.Disk ?? 0,
            MaxDiskBytes = dto.MaxDisk ?? 0,
            UptimeSeconds = dto.Uptime ?? 0,
            Level = dto.Level
        };
    }

    /// <summary>Gets detailed runtime status for one node (GET /nodes/{node}/status).</summary>
    public async Task<PveNodeStatus> GetNodeStatusAsync(string node, CancellationToken ct = default)
    {
        var data = await GetJsonAsync($"nodes/{Escape(node)}/status", ct).ConfigureAwait(false);

        return new PveNodeStatus
        {
            Node = node,
            CpuUsagePercent = GetDouble(data, "cpu") * 100.0,
            CpuCores = TryObject(data, "cpuinfo", out var cpuInfo) ? GetInt(cpuInfo, "cpus") : 0,
            KernelVersion = GetString(data, "kversion"),
            UptimeSeconds = GetLong(data, "uptime"),
            // loadavg: ["0.12","0.34","0.56"] = 1·5·15분 평균
            LoadAverage1 = ParseLoadAverage(data, 0),
            LoadAverage5 = ParseLoadAverage(data, 1),
            LoadAverage15 = ParseLoadAverage(data, 2),
            MemUsedBytes = TryObject(data, "memory", out var mem) ? GetLong(mem, "used") : 0,
            MemTotalBytes = TryObject(data, "memory", out mem) ? GetLong(mem, "total") : 0,
            SwapUsedBytes = TryObject(data, "swap", out var swap) ? GetLong(swap, "used") : 0,
            SwapTotalBytes = TryObject(data, "swap", out swap) ? GetLong(swap, "total") : 0,
            RootFsTotalBytes = TryObject(data, "rootfs", out var rootFs) ? GetLong(rootFs, "total") : 0,
            RootFsUsedBytes = TryObject(data, "rootfs", out rootFs) ? GetLong(rootFs, "used") : 0,
            RootFsAvailableBytes = TryObject(data, "rootfs", out rootFs) ? GetLong(rootFs, "avail") : 0
        };
    }

    /// <summary>nodes/{node}/status 의 loadavg 배열 항목(문자열 또는 숫자). 없거나 해석 불가면 0.</summary>
    private static double ParseLoadAverage(in JsonElement data, int index)
    {
        if (!TryArrayItem(data, "loadavg", index, out var item)) return 0.0;

        return item.ValueKind switch
        {
            JsonValueKind.Number => item.GetDouble(),
            JsonValueKind.String when double.TryParse(
                item.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) => value,
            _ => 0.0
        };
    }

    /// <summary>Lists the VMs or containers on a node (GET /nodes/{node}/qemu|lxc).</summary>
    public Task<IReadOnlyList<PveResource>> GetGuestsAsync(string node, ResourceKind kind,
        CancellationToken ct = default)
    {
        return GetListAsync<PveResource, ResourceDto>(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}",
            dto => MapResource(dto, node),
            ct);
    }

    /// <summary>Starts a guest (POST /nodes/{node}/{qemu|lxc}/{vmid}/status/start). Returns the task UPID.</summary>
    public Task<string> StartGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/start", null, ct);
    }

    /// <summary>Force-stops a guest (POST .../status/stop). Returns the task UPID.</summary>
    public Task<string> StopGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/stop", null, ct);
    }

    /// <summary>Gracefully shuts a guest down (POST .../status/shutdown). Returns the task UPID.</summary>
    public Task<string> ShutdownGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/shutdown", null, ct);
    }

    /// <summary>Reboots a running guest (POST .../status/reboot). Returns the task UPID.</summary>
    public Task<string> RebootGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/reboot", null, ct);
    }

    /// <summary>Pauses a running VM (POST .../status/suspend). QEMU only. Returns the task UPID.</summary>
    public Task<string> SuspendGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/suspend", null, ct);
    }

    /// <summary>Resumes a paused VM (POST .../status/resume). QEMU only. Returns the task UPID.</summary>
    public Task<string> ResumeGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/resume", null, ct);
    }

    /// <summary>Hibernates a running VM to disk (POST .../status/hibernate). QEMU only. Returns the task UPID.</summary>
    public Task<string> HibernateGuestAsync(string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/status/hibernate", null, ct);
    }

    /// <summary>
    ///     Clones a guest (POST .../clone). full=true → 전체 복제, false → 연결 복제.
    ///     Returns the task UPID.
    /// </summary>
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
    ///     Starts a vzdump backup (POST .../vzdump). mode: snapshot|suspend|stop.
    ///     compress: "" (none)|zstd|lzo|gzip. Returns the task UPID.
    /// </summary>
    public Task<string> BackupGuestAsync(
        string node, ResourceKind kind, int vmid, string storage, string mode, string compress,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["storage"] = storage,
            ["mode"] = mode
        };
        if (!string.IsNullOrEmpty(compress)) form["compress"] = compress;

        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/vzdump", form, ct);
    }

    /// <summary>
    ///     Gets the guest's raw configuration (GET .../config) as a string map —
    ///     every value stringified so callers can edit and PUT back the changed keys.
    /// </summary>
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
    public Task<string> UpdateGuestConfigAsync(
        string node, ResourceKind kind, int vmid, IReadOnlyDictionary<string, string> changes,
        CancellationToken ct = default)
    {
        return SendWriteAsync(
            HttpMethod.Put,
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/config",
            changes,
            ct);
    }

    /// <summary>
    ///     "vztmpl,iso,backup" 처럼 서버가 매 응답마다 순서를 바꿔 주는 콤마 목록을 정렬해 표시가 흔들리지 않게 한다.
    /// </summary>
    private static string NormalizeCsv(string? csv)
    {
        return string.IsNullOrWhiteSpace(csv)
            ? string.Empty
            : string.Join(",", csv
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     게스트 실시간 상태 (GET nodes/{node}/{kind}/{vmid}/status/current).
    ///     /cluster/resources 의 상태는 pvestatd 주기로 늦게 반영되므로 전원 작업 직후엔 이 값을 쓴다.
    ///     일시정지된 VM 은 status 가 running 이고 qmpstatus 가 paused 이므로 "paused" 로 돌려준다. 값이 없으면 빈 문자열.
    /// </summary>
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
    public Task<string> CreateQemuAsync(
        string node, int vmid, string? name, int cores, int memoryMiB,
        string? isoVolid, string diskStorage, int diskGb, string bridge,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["vmid"] = vmid.ToString(),
            ["cores"] = cores.ToString(),
            ["memory"] = memoryMiB.ToString(),
            ["ostype"] = "l26",
            ["scsihw"] = "virtio-scsi-pci",
            ["net0"] = $"virtio,bridge={bridge}",
            ["scsi0"] = $"{diskStorage}:{diskGb}"
        };
        if (!string.IsNullOrWhiteSpace(name)) form["name"] = name;

        if (!string.IsNullOrWhiteSpace(isoVolid)) form["ide2"] = $"{isoVolid},media=cdrom";

        return PostWriteAsync($"nodes/{Escape(node)}/qemu", form, ct);
    }

    /// <summary>Creates an LXC container (POST nodes/{node}/lxc).</summary>
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

    /// <summary>Gets guest firewall options (GET .../firewall/options) as a string map.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetFirewallOptionsAsync(
        string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        var data = await GetJsonAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/firewall/options", ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data.ValueKind == JsonValueKind.Object)
            foreach (var property in data.EnumerateObject())
                map[property.Name] = property.Value.ToString();

        return map;
    }

    /// <summary>Enables or disables the guest firewall (PUT .../firewall/options).</summary>
    public Task<string> SetFirewallEnabledAsync(
        string node, ResourceKind kind, int vmid, bool enabled, CancellationToken ct = default)
    {
        return SendWriteAsync(
            HttpMethod.Put,
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/firewall/options",
            new Dictionary<string, string> { ["enable"] = enabled ? "1" : "0" },
            ct);
    }

    /// <summary>Lists guest firewall rules (GET .../firewall/rules).</summary>
    public async Task<IReadOnlyList<PveFirewallRule>> GetFirewallRulesAsync(
        string node, ResourceKind kind, int vmid, CancellationToken ct = default)
    {
        var data = await GetJsonAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/firewall/rules", ct).ConfigureAwait(false);
        var list = new List<PveFirewallRule>();
        if (data.ValueKind != JsonValueKind.Array) return list;

        foreach (var item in data.EnumerateArray())
        {
            var type = GetString(item, "type");
            list.Add(new PveFirewallRule
            {
                Pos = GetInt(item, "pos"),
                Action = GetString(item, "action"),
                Direction = type is "in" or "out" ? type : string.Empty,
                Proto = GetString(item, "proto"),
                DPort = GetString(item, "dport"),
                Source = GetString(item, "source"),
                Macro = GetString(item, "macro"),
                Comment = GetString(item, "comment"),
                Enabled = GetInt(item, "enable") == 1,
                Type = type
            });
        }

        return list;
    }

    /// <summary>Adds a rule (POST .../firewall/rules). pos는 자동 할당.</summary>
    public Task<string> AddFirewallRuleAsync(
        string node, ResourceKind kind, int vmid, PveFirewallRule rule, CancellationToken ct = default)
    {
        return PostWriteAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/firewall/rules",
            new Dictionary<string, string>(rule.ToForm(), StringComparer.Ordinal),
            ct);
    }

    /// <summary>Replaces the rule at pos (PUT .../firewall/rules/{pos}).</summary>
    public Task<string> UpdateFirewallRuleAsync(
        string node, ResourceKind kind, int vmid, int pos, PveFirewallRule rule,
        CancellationToken ct = default)
    {
        return SendWriteAsync(
            HttpMethod.Put,
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/firewall/rules/{pos}",
            new Dictionary<string, string>(rule.ToForm(), StringComparer.Ordinal),
            ct);
    }

    /// <summary>Deletes the rule at pos (DELETE .../firewall/rules/{pos}).</summary>
    public Task<string> DeleteFirewallRuleAsync(
        string node, ResourceKind kind, int vmid, int pos, CancellationToken ct = default)
    {
        return SendWriteAsync(
            HttpMethod.Delete,
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/firewall/rules/{pos}",
            null,
            ct);
    }

    /// <summary>
    ///     Lists a guest's snapshots (GET .../snapshot), excluding the "!current"
    ///     pseudo-entry.
    /// </summary>
    public Task<IReadOnlyList<PveSnapshot>> GetSnapshotsAsync(string node, ResourceKind kind, int vmid,
        CancellationToken ct = default)
    {
        return GetListAsync<PveSnapshot, SnapshotDto>(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/snapshot",
            MapSnapshot,
            ct);
    }

    /// <summary>
    ///     Creates a snapshot (POST .../snapshot, form: vmid, name, description,
    ///     vmstate=1 when <paramref name="includeRam" />). Returns the task UPID.
    /// </summary>
    public Task<string> CreateSnapshotAsync(
        string node,
        ResourceKind kind,
        int vmid,
        string name,
        string? description = null,
        bool includeRam = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_04"));

        var form = new Dictionary<string, string>
        {
            ["vmid"] = vmid.ToString(),
            ["name"] = name
        };
        if (!string.IsNullOrEmpty(description)) form["description"] = description;
        if (includeRam) form["vmstate"] = "1";

        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/snapshot", form, ct);
    }

    /// <summary>Rolls a guest back to a snapshot (POST .../snapshot/{name}/rollback). Returns the task UPID.</summary>
    public Task<string> RollbackSnapshotAsync(string node, ResourceKind kind, int vmid, string name,
        CancellationToken ct = default)
    {
        return PostWriteAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/snapshot/{Escape(name)}/rollback",
            null,
            ct);
    }

    /// <summary>Deletes a snapshot (DELETE .../snapshot/{name}). Returns the task UPID.</summary>
    public Task<string> DeleteSnapshotAsync(string node, ResourceKind kind, int vmid, string name,
        CancellationToken ct = default)
    {
        return DeleteWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/snapshot/{Escape(name)}", ct);
    }

    /// <summary>Lists cluster-wide tasks, newest first (GET /cluster/tasks).</summary>
    public Task<IReadOnlyList<PveTask>> GetClusterTasksAsync(CancellationToken ct = default)
    {
        return GetListAsync<PveTask, TaskDto>("cluster/tasks", MapTask, ct);
    }

    /// <summary>
    ///     Lists tasks on one node (GET /nodes/{node}/tasks?active=0|1&amp;limit=100).
    ///     Running tasks (no status yet) are reported as "running".
    /// </summary>
    public Task<IReadOnlyList<PveTask>> GetNodeTasksAsync(string node, bool activeOnly = false,
        CancellationToken ct = default)
    {
        return GetListAsync<PveTask, TaskDto>(
            $"nodes/{Escape(node)}/tasks?active={(activeOnly ? 1 : 0)}&limit=100",
            MapTask,
            ct);
    }

    /// <summary>
    ///     Gets the current status of one task by UPID
    ///     (GET /nodes/{node}/tasks/{upid}/status). The node is parsed from the UPID.
    ///     Terminal tasks report "OK" / "ERROR: ..." (exitstatus); running ones "running".
    /// </summary>
    public async Task<PveTask> GetTaskStatusAsync(string upid, CancellationToken ct = default)
    {
        var baseTask = ParseUpid(upid);
        var data = await GetJsonAsync(
            $"nodes/{Escape(baseTask.Node)}/tasks/{Escape(upid)}/status",
            ct).ConfigureAwait(false);

        // Finished tasks expose status "stopped" plus exitstatus ("OK"/"ERROR: ...").
        var status = GetString(data, "exitstatus");
        if (status.Length == 0) status = GetString(data, "status");

        var endTime = GetLong(data, "endtime");
        return new PveTask
        {
            Upid = baseTask.Upid,
            Node = baseTask.Node,
            Type = baseTask.Type,
            Id = baseTask.Id,
            User = baseTask.User,
            StartTimeUtc = baseTask.StartTimeUtc,
            EndTimeUtc = endTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(endTime).UtcDateTime
                : null,
            Status = status.Length > 0 ? status : "running"
        };
    }

    /// <summary>
    ///     Polls a task (default every 2 s) until it leaves the "running" state and
    ///     returns the final task snapshot. Cancellation stops the wait.
    /// </summary>
    public async Task<PveTask> WaitTaskAsync(string upid, TimeSpan? pollInterval = null, CancellationToken ct = default)
    {
        var delay = pollInterval ?? TimeSpan.FromSeconds(2);
        while (true)
        {
            var task = await GetTaskStatusAsync(upid, ct).ConfigureAwait(false);
            if (!task.IsRunning) return task;

            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Fetches a node RRD graph as PNG bytes
    ///     (GET /nodes/{node}/rrdtool?cf=AVERAGE&amp;timeframe={tf}&amp;ds={ds}).
    /// </summary>
    public Task<byte[]> GetNodeRrdPngAsync(string node, string timeframe = "hour", string ds = "cpu",
        CancellationToken ct = default)
    {
        return GetPngAsync($"nodes/{Escape(node)}/rrdtool?cf=AVERAGE&timeframe={Escape(timeframe)}&ds={Escape(ds)}",
            ct);
    }

    /// <summary>
    ///     Fetches a guest (VM/CT) RRD graph as PNG bytes
    ///     (GET /nodes/{node}/{qemu|lxc}/{vmid}/rrdtool?...).
    /// </summary>
    public Task<byte[]> GetGuestRrdPngAsync(string node, ResourceKind kind, int vmid, string timeframe = "hour",
        string ds = "cpu", CancellationToken ct = default)
    {
        return GetPngAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/rrdtool?cf=AVERAGE&timeframe={Escape(timeframe)}&ds={Escape(ds)}",
            ct);
    }

    /// <summary>
    ///     게스트 시계열 데이터(GET .../rrddata?timeframe=hour|day|week|month|year).
    ///     PVE 8.2+에서 rrdtool PNG 엔드포인트가 제거되어 JSON 데이터 기반 렌더링에 사용.
    /// </summary>
    public async Task<IReadOnlyList<RrdSample>> GetGuestRrdDataAsync(
        string node, ResourceKind kind, int vmid, string timeframe = "hour", CancellationToken ct = default)
    {
        var data = await GetJsonAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/rrddata?timeframe={Uri.EscapeDataString(timeframe)}&cf=AVERAGE",
            ct).ConfigureAwait(false);
        return ParseRrdSamples(data);
    }

    /// <summary>노드 시계열 데이터(GET nodes/{node}/rrddata).</summary>
    public async Task<IReadOnlyList<RrdSample>> GetNodeRrdDataAsync(
        string node, string timeframe = "hour", CancellationToken ct = default)
    {
        var data = await GetJsonAsync(
            $"nodes/{Escape(node)}/rrddata?timeframe={Uri.EscapeDataString(timeframe)}&cf=AVERAGE",
            ct).ConfigureAwait(false);
        return ParseRrdSamples(data);
    }

    private static List<RrdSample> ParseRrdSamples(JsonElement data)
    {
        var samples = new List<RrdSample>();
        if (data.ValueKind != JsonValueKind.Array) return samples;

        foreach (var item in data.EnumerateArray())
            samples.Add(new RrdSample
            {
                TimeUnix = GetLong(item, "time"),
                Cpu = GetDoubleOrNull(item, "cpu"),
                Mem = GetDoubleOrNull(item, "mem"),
                MaxMem = GetDoubleOrNull(item, "maxmem"),
                NetIn = GetDoubleOrNull(item, "netin"),
                NetOut = GetDoubleOrNull(item, "netout"),
                DiskRead = GetDoubleOrNull(item, "diskread"),
                DiskWrite = GetDoubleOrNull(item, "diskwrite")
            });

        return samples;
    }

    private static double? GetDoubleOrNull(in JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var el)
               && el.ValueKind is JsonValueKind.Number or JsonValueKind.String
               && double.TryParse(el.ToString(), CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    ///     Gets the effective permission tree visible to the current user
    ///     (GET /access/permissions) and flattens it into a capability summary.
    /// </summary>
    public async Task<PermissionsInfo> GetPermissionsSummaryAsync(CancellationToken ct = default)
    {
        var data = await GetJsonAsync("access/permissions", ct).ConfigureAwait(false);
        var privileges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var isAdmin = false;
        if (data.ValueKind != JsonValueKind.Object) return new PermissionsInfo { Privileges = privileges };

        foreach (var path in data.EnumerateObject())
        {
            if (path.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var role in path.Value.EnumerateObject())
            {
                // PVE 응답 형식: { "/vms": { "VM.PowerMgmt": 1, ... } } — 값은 propagate 플래그(권한 보유 자체는 동일)
                if (role.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    privileges.Add(role.Name);
                    continue;
                }

                if (role.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) isAdmin = true;

                if (role.Value.ValueKind != JsonValueKind.Array) continue;

                foreach (var privilege in role.Value.EnumerateArray())
                    if (privilege.ValueKind is JsonValueKind.String)
                        privileges.Add(privilege.GetString() ?? string.Empty);
            }
        }

        return new PermissionsInfo { IsAdmin = isAdmin, Privileges = privileges };
    }

    /// <summary>
    ///     Starts a VNC console proxy (POST nodes/{node}/qemu/{vmid}/vncproxy).
    ///     Returns port + ticket used to open the vncwebsocket console channel.
    ///     Requires password (ticket) authentication.
    /// </summary>
    public async Task<VncProxyInfo> CreateVncProxyAsync(string node, ResourceKind kind, int vmid,
        CancellationToken ct = default)
    {
        if (Profile.AuthMode == AuthMode.ApiToken || _auth is null)
            throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_05"));

        var data = await PostFormJsonAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/vncproxy",
            new Dictionary<string, string>(),
            true,
            ct).ConfigureAwait(false);

        return new VncProxyInfo
        {
            Port = GetInt(data, "port"),
            Ticket = GetString(data, "ticket"),
            UpId = GetString(data, "upid")
        };
    }

    /// <summary>
    ///     터미널 프록시 시작 (POST nodes/{node}/{lxc|qemu}/{vmid}/termproxy).
    ///     서버가 게스트 콘솔에 PTY 를 붙이고, 반환된 포트·티켓으로 vncwebsocket 에 연결한다.
    /// </summary>
    public async Task<TermProxyInfo> CreateTermProxyAsync(string node, ResourceKind kind, int vmid,
        CancellationToken ct = default)
    {
        if (Profile.AuthMode == AuthMode.ApiToken || _auth is null)
            throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_06"));

        var data = await PostFormJsonAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/termproxy",
            new Dictionary<string, string>(),
            true,
            ct).ConfigureAwait(false);

        return new TermProxyInfo
        {
            Port = GetInt(data, "port"),
            Ticket = GetString(data, "ticket"),
            User = GetString(data, "user"),
            UpId = GetString(data, "upid")
        };
    }

    /// <summary>
    ///     Gets the SPICE proxy connection settings (POST nodes/{node}/qemu/{vmid}/spiceproxy)
    ///     used to build a .vv file for remote-viewer. QEMU VMs only.
    /// </summary>
    public async Task<SpiceProxyInfo> GetSpiceProxyAsync(string node, int vmid, CancellationToken ct = default)
    {
        // proxy: 클라이언트가 접속할 spiceproxy 주소. 생략하면 서버가 노드 이름을 돌려주는데,
        // 이 PC 에서 그 이름이 해석되지 않으면 연결에 실패하므로 웹 UI 처럼 접속 중인 서버 주소를 보낸다.
        var data = await PostFormJsonAsync(
            $"nodes/{Escape(node)}/qemu/{vmid}/spiceproxy",
            new Dictionary<string, string> { ["proxy"] = Profile.Host },
            true,
            ct).ConfigureAwait(false);

        if (data.ValueKind != JsonValueKind.Object)
            throw new ProxmoxApiException(0, null, Res.T("ProxmoxApiClient_07"));

        var settings = new List<KeyValuePair<string, string>>();
        foreach (var property in data.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => null
            };
            if (value is not null) settings.Add(new KeyValuePair<string, string>(property.Name, value));
        }

        return new SpiceProxyInfo
        {
            Settings = settings,
            Host = GetString(data, "host"),
            Password = GetString(data, "password"),
            TlsPort = GetIntOrNull(data, "tls-port"),
            SecurePort = GetIntOrNull(data, "secure-port"),
            HostSubject = GetString(data, "host-subject"),
            Proxy = GetString(data, "proxy"),
            ReleaseCursor = GetString(data, "release-cursor"),
            ToggleFullscreen = GetString(data, "toggle-fullscreen")
        };
    }

    private static int? GetIntOrNull(in JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var el)
               && el.ValueKind is JsonValueKind.Number or JsonValueKind.String
               && int.TryParse(el.ToString(), out var value)
            ? value
            : null;
    }

    /// <summary>
    ///     Gets the authentication realms (login types) for the login screen
    ///     (GET /access/domains — explicitly allowed without authentication by
    ///     pve-proxy). Returns an empty list on failure so callers can fall back
    ///     to built-in defaults.
    /// </summary>
    public async Task<IReadOnlyList<PveAuthDomain>> GetAuthDomainsAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await GetJsonAsync("access/domains", ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Array) return [];

            var list = new List<PveAuthDomain>();
            foreach (var item in data.EnumerateArray())
            {
                var realm = GetString(item, "realm");
                if (string.IsNullOrEmpty(realm)) continue;

                list.Add(new PveAuthDomain
                {
                    Realm = realm,
                    Type = GetString(item, "type"),
                    Comment = GetString(item, "comment"),
                    RequiresTfa = item.TryGetProperty("tfa", out _),
                    IsDefault = item.TryGetProperty("default", out var def)
                                && def.ValueKind is JsonValueKind.Number or JsonValueKind.String
                                && def.ToString() == "1"
                });
            }

            return list;
        }
        catch (ProxmoxApiException)
        {
            return [];
        }
    }

    private static Uri BuildBaseAddress(ConnectionProfile profile)
    {
        var builder = new UriBuilder(Uri.UriSchemeHttps, profile.Host, profile.Port)
        {
            Path = "/api2/json/"
        };
        return builder.Uri;
    }

    private static void ConfigureProxy(HttpClientHandler handler, ConnectionProfile profile)
    {
        if (profile.ProxyMode == ProxyMode.None)
        {
            handler.UseProxy = false; // 직접 연결 — 시스템 프록시도 사용하지 않음
            return;
        }

        if (profile.ProxyMode == ProxyMode.System)
        {
            handler.UseProxy = true; // 시스템(운영체제) 프록시 설정 사용
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.ProxyHost) || profile.ProxyPort is not > 0) return;

        var scheme = profile.ProxyMode switch
        {
            ProxyMode.Http => "http",
            ProxyMode.Https => "https",
            ProxyMode.Socks5 => "socks5",
            _ => "http"
        };

        var proxy = new WebProxy($"{scheme}://{profile.ProxyHost}:{profile.ProxyPort!.Value}");
        if (!string.IsNullOrEmpty(profile.ProxyUserName))
            proxy.Credentials = new NetworkCredential(profile.ProxyUserName, profile.ProxyPassword ?? string.Empty);

        handler.Proxy = proxy;
    }

    private static string Escape(string segment)
    {
        return Uri.EscapeDataString(segment);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string Excerpt(string body)
    {
        return body.Length <= ResponseExcerptLength
            ? body
            : body[..ResponseExcerptLength];
    }

    /// <summary>GET → "data" 요소(복제본). 1초 주기 목록 조회는 복제 없이 문서에서 바로 매핑하는 <see cref="GetListAsync{T,TDto}" /> 사용.</summary>
    private async Task<JsonElement> GetJsonAsync(string relative, CancellationToken ct)
    {
        using var doc = await GetDocumentAsync(relative, ct).ConfigureAwait(false);
        return DataElement(doc).Clone(); // Clone() survives doc disposal.
    }

    private async Task<JsonDocument> GetDocumentAsync(string relative, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, relative), true, ct)
            .ConfigureAwait(false);
        return await ReadJsonDocumentAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>POSTs a form and returns the "data" element (login path: requireAuth=false, no cookie/renewal).</summary>
    private async Task<JsonElement> PostFormJsonAsync(
        string relative,
        IReadOnlyDictionary<string, string> form,
        bool requireAuth,
        CancellationToken ct)
    {
        using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, relative) { Content = new FormUrlEncodedContent(form) },
                requireAuth,
                ct)
            .ConfigureAwait(false);
        using var doc = await ReadJsonDocumentAsync(response, ct).ConfigureAwait(false);
        return DataElement(doc).Clone();
    }

    private Task<string> PostWriteAsync(string relative, IReadOnlyDictionary<string, string>? form,
        CancellationToken ct)
    {
        return SendWriteAsync(HttpMethod.Post, relative, form, ct);
    }

    private Task<string> DeleteWriteAsync(string relative, CancellationToken ct)
    {
        return SendWriteAsync(HttpMethod.Delete, relative, null, ct);
    }

    /// <summary>Performs a write request (POST/DELETE) with the CSRF header; returns "data" as string (UPID).</summary>
    private async Task<string> SendWriteAsync(
        HttpMethod method,
        string relative,
        IReadOnlyDictionary<string, string>? form,
        CancellationToken ct)
    {
        ThrowIfDisposed();

        if (Profile.AuthMode == AuthMode.Password && _auth is null)
            throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_08"));

        using var response = await SendAsync(
                () => new HttpRequestMessage(method, relative)
                {
                    Content = form is null ? null : new FormUrlEncodedContent(form)
                },
                true,
                ct)
            .ConfigureAwait(false);
        using var doc = await ReadJsonDocumentAsync(response, ct).ConfigureAwait(false);
        var data = DataElement(doc);
        return data.ValueKind == JsonValueKind.String ? data.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>
    ///     인증 헤더를 요청마다 붙여 전송한다(공유 DefaultRequestHeaders 를 바꾸지 않아 동시 요청과 경합 없음).
    ///     비밀번호 모드는 만료 전에 티켓을 갱신하고, 401 이면 한 번 갱신 후 재시도한다(요청은 재전송 불가라 팩토리로 생성).
    ///     연결 실패·타임아웃은 <see cref="ProxmoxApiException" /> 으로 감싸 호출자가 한 종류만 처리하면 되게 한다.
    ///     응답은 헤더까지만 받고 본문은 스트림으로 읽는다(문자열 전체 버퍼링 없음) — 호출자가 Dispose 해야 한다.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, bool requireAuth,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        if (requireAuth) await EnsureFreshTicketAsync(false, ct).ConfigureAwait(false);

        var response = await SendOnceAsync(createRequest, requireAuth, ct).ConfigureAwait(false);
        if (!requireAuth || response.StatusCode != HttpStatusCode.Unauthorized || _auth is null) return response;

        response.Dispose();
        await EnsureFreshTicketAsync(true, ct).ConfigureAwait(false);
        return await SendOnceAsync(createRequest, requireAuth, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> createRequest, bool requireAuth,
        CancellationToken ct)
    {
        using var request = createRequest();
        ApplyAuthHeaders(request, requireAuth);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // TLS 인증 실패가 인증서 미신뢰·지문 불일치 때문이면 호출자가 사용자에게 확인을 요청할 수 있게 구분한다
            if (CertificateValidator.TryCreateTrustException(ex) is { } trust) throw trust;

            throw new ProxmoxApiException(0, null, Res.T("ProxmoxApiClient_09", ex.Message));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProxmoxApiException(0, null, Res.T("ProxmoxApiClient_10"));
        }
    }

    /// <summary>티켓 쿠키(모든 인증 요청)와 CSRF 헤더(쓰기 요청)를 요청 단위로 붙인다.</summary>
    private void ApplyAuthHeaders(HttpRequestMessage request, bool requireAuth)
    {
        if (!requireAuth || Profile.AuthMode != AuthMode.Password || _auth is not { } auth) return;

        request.Headers.TryAddWithoutValidation("Cookie", $"PVEAuthCookie={auth.Ticket}");
        if (request.Method != HttpMethod.Get && !string.IsNullOrEmpty(auth.CsrfToken))
            request.Headers.TryAddWithoutValidation("CSRFPreventionToken", auth.CsrfToken);
    }

    /// <summary>성공 응답 본문을 스트림에서 바로 파싱; 실패(non-2xx)는 본문 발췌와 함께 <see cref="ProxmoxApiException" />.</summary>
    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new ProxmoxApiException((int)response.StatusCode, Excerpt(body));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ProxmoxApiException((int)response.StatusCode, null, Res.T("ProxmoxApiClient_11", ex.Message));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ProxmoxApiException((int)response.StatusCode, null, Res.T("ProxmoxApiClient_12", ex.Message));
        }
    }

    /// <summary>{"data": ...} 봉투의 data, 봉투가 없으면 루트.</summary>
    private static JsonElement DataElement(JsonDocument doc)
    {
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;
    }

    private async Task<byte[]> GetPngAsync(string relative, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, relative), true, ct)
            .ConfigureAwait(false);
        try
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new ProxmoxApiException((int)response.StatusCode, Excerpt(body));
            }

            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ProxmoxApiException((int)response.StatusCode, null, Res.T("ProxmoxApiClient_12", ex.Message));
        }
    }

    /// <summary>목록 응답을 문서에서 바로 DTO → 모델로 매핑(응답 문자열·JsonElement 복제·중간 리스트 없음).</summary>
    private async Task<IReadOnlyList<T>> GetListAsync<T, TDto>(
        string relative,
        Func<TDto, T> map,
        CancellationToken ct)
    {
        using var doc = await GetDocumentAsync(relative, ct).ConfigureAwait(false);
        var data = DataElement(doc);
        switch (data.ValueKind)
        {
            case JsonValueKind.Array:
                var list = new List<T>(data.GetArrayLength());
                foreach (var item in data.EnumerateArray())
                    if (item.Deserialize<TDto>(JsonOptions) is { } dto)
                        list.Add(map(dto));

                return list;

            case JsonValueKind.Object:
                return data.Deserialize<TDto>(JsonOptions) is { } single ? [map(single)] : [];

            default:
                return [];
        }
    }

    private static PveResource MapResource(ResourceDto dto, string node)
    {
        var kind = string.Equals(dto.Type, "lxc", StringComparison.OrdinalIgnoreCase)
            ? ResourceKind.Lxc
            : ResourceKind.Qemu;
        var vmid = dto.VmId ?? 0;

        return new PveResource
        {
            Id = dto.Id ?? $"{kind.ApiSegment()}/{vmid}",
            Kind = kind,
            VmId = vmid,
            Name = !string.IsNullOrWhiteSpace(dto.Name) ? dto.Name : $"{kind.Label()}-{vmid}",
            Node = node,
            Status = dto.Status ?? string.Empty,
            CpuUsagePercent = (dto.Cpu ?? 0) * 100.0,
            CpuCount = dto.MaxCpu ?? dto.Cpus ?? 0,
            MemBytes = dto.Mem ?? 0,
            MaxMemBytes = dto.MaxMem ?? 0,
            DiskBytes = dto.Disk ?? 0,
            MaxDiskBytes = dto.MaxDisk ?? 0,
            NetInBytes = dto.NetIn ?? 0,
            NetOutBytes = dto.NetOut ?? 0,
            UptimeSeconds = dto.Uptime ?? 0,
            IsTemplate = (dto.Template ?? 0) != 0,
            Tags = (dto.Tags ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.TrimStart(';'))
                .ToArray(),
            Pool = string.IsNullOrEmpty(dto.Pool) ? null : dto.Pool
        };
    }

    private static PveSnapshot MapSnapshot(SnapshotDto dto)
    {
        return new PveSnapshot
        {
            Name = dto.Name ?? string.Empty,
            Description = string.IsNullOrEmpty(dto.Description) ? null : dto.Description,
            SnapTimeUtc = dto.SnapTime is > 0
                ? DateTimeOffset.FromUnixTimeSeconds(dto.SnapTime.Value).UtcDateTime
                : null,
            HasRam = (dto.Running ?? 0) != 0,
            Parent = string.IsNullOrEmpty(dto.Parent) ? null : dto.Parent
        };
    }

    private static PveTask MapTask(TaskDto dto)
    {
        var upid = dto.Upid ?? string.Empty;
        var fromUpid = ParseUpid(upid);
        var status = dto.Status;
        if (string.IsNullOrEmpty(status) && dto.ExitStatus is not null)
            status = dto.ExitStatus;
        else if (string.IsNullOrEmpty(status))
            // Task list entries without a status are still running.
            status = "running";

        return new PveTask
        {
            Upid = upid,
            Node = dto.Node ?? fromUpid.Node,
            Type = dto.Type ?? fromUpid.Type,
            Id = dto.Id ?? fromUpid.Id,
            User = dto.User ?? fromUpid.User,
            StartTimeUtc = dto.StartTime is > 0
                ? DateTimeOffset.FromUnixTimeSeconds(dto.StartTime.Value).UtcDateTime
                : fromUpid.StartTimeUtc,
            EndTimeUtc = dto.EndTime is > 0
                ? DateTimeOffset.FromUnixTimeSeconds(dto.EndTime.Value).UtcDateTime
                : null,
            Status = status
        };
    }

    /// <summary>
    ///     Parses "UPID:{node}:{pid}:{pstart}:{starttime}:{type}:{id}:{user}:".
    ///     Unknown shapes yield an empty task instead of throwing.
    /// </summary>
    internal static PveTask ParseUpid(string upid)
    {
        var parts = upid.Split(':');
        if (parts.Length < 8 || parts[0] != "UPID") return new PveTask { Upid = upid, Status = string.Empty };

        var startTime = long.TryParse(parts[4], out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            : default;

        return new PveTask
        {
            Upid = upid,
            Node = parts[1],
            Type = parts[5],
            Id = parts[6],
            User = parts[7],
            StartTimeUtc = startTime
        };
    }

    // Used for payloads with nested objects (node status) where DTO mapping is clumsy.
    // Proxmox may send numbers as strings, so both kinds are accepted.

    private static string GetString(in JsonElement obj, string name)
    {
        return obj.ValueKind == JsonValueKind.Object
               && obj.TryGetProperty(name, out var v)
               && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
    }

    private static long GetLong(in JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return 0;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (long)v.GetDouble(),
            JsonValueKind.String when long.TryParse(v.GetString(), out var l) => l,
            _ => 0
        };
    }

    private static int GetInt(in JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return 0;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : (int)v.GetDouble(),
            JsonValueKind.String when int.TryParse(v.GetString(), out var i) => i,
            _ => 0
        };
    }

    private static double GetDouble(in JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return 0;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(
                v.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var d) => d,
            _ => 0
        };
    }

    private static bool TryObject(in JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Object)
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryArrayItem(in JsonElement obj, string name, int index, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Array
            && v.GetArrayLength() > index)
        {
            value = v[index];
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>비밀번호 모드 인증 상태 — 티켓·CSRF 토큰을 한 객체로 원자적으로 교체해 동시 요청이 짝이 어긋난 값을 읽지 않게 한다.</summary>
    private sealed record AuthSession(string Ticket, string? CsrfToken, long IssuedAtTick);

    private sealed class ResourceDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("vmid")] public int? VmId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("node")] public string? Node { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("cpu")] public double? Cpu { get; set; }
        [JsonPropertyName("maxcpu")] public int? MaxCpu { get; set; }
        [JsonPropertyName("cpus")] public int? Cpus { get; set; }
        [JsonPropertyName("mem")] public long? Mem { get; set; }
        [JsonPropertyName("maxmem")] public long? MaxMem { get; set; }
        [JsonPropertyName("disk")] public long? Disk { get; set; }
        [JsonPropertyName("maxdisk")] public long? MaxDisk { get; set; }
        [JsonPropertyName("netin")] public long? NetIn { get; set; }
        [JsonPropertyName("netout")] public long? NetOut { get; set; }
        [JsonPropertyName("uptime")] public long? Uptime { get; set; }
        [JsonPropertyName("template")] public int? Template { get; set; }
        [JsonPropertyName("tags")] public string? Tags { get; set; }
        [JsonPropertyName("pool")] public string? Pool { get; set; }
        [JsonPropertyName("storage")] public string? Storage { get; set; }
        [JsonPropertyName("plugintype")] public string? PluginType { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("level")] public string? Level { get; set; } // 노드 항목의 구독 수준
    }

    private sealed class NodeDto
    {
        [JsonPropertyName("node")] public string? Node { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("cpu")] public double? Cpu { get; set; }
        [JsonPropertyName("maxcpu")] public int? MaxCpu { get; set; }
        [JsonPropertyName("mem")] public long? Mem { get; set; }
        [JsonPropertyName("maxmem")] public long? MaxMem { get; set; }
        [JsonPropertyName("disk")] public long? Disk { get; set; }
        [JsonPropertyName("maxdisk")] public long? MaxDisk { get; set; }
        [JsonPropertyName("uptime")] public long? Uptime { get; set; }
        [JsonPropertyName("level")] public string? Level { get; set; }
    }

    private sealed class SnapshotDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("snaptime")] public long? SnapTime { get; set; }
        [JsonPropertyName("running")] public int? Running { get; set; }
        [JsonPropertyName("parent")] public string? Parent { get; set; }
    }

    private sealed class TaskDto
    {
        [JsonPropertyName("upid")] public string? Upid { get; set; }
        [JsonPropertyName("node")] public string? Node { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("user")] public string? User { get; set; }
        [JsonPropertyName("starttime")] public long? StartTime { get; set; }
        [JsonPropertyName("endtime")] public long? EndTime { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("exitstatus")] public string? ExitStatus { get; set; }
    }
}