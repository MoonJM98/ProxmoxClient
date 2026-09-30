using System.Text.Json;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Api;

/// <summary>노드 — 목록·상태·설정 묶음·시스템 로그.</summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>Lists all nodes (GET /nodes).</summary>
    [Versioning.PveApi("GET", "/nodes")]
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
    /// <summary>Gets detailed runtime status for one node (GET /nodes/{node}/status).</summary>
    [Versioning.PveApi("GET", "/nodes/{node}/status")]
    public async Task<PveNodeStatus> GetNodeStatusAsync(string node, CancellationToken ct = default)
    {
        var data = await GetJsonAsync($"nodes/{Escape(node)}/status", ct).ConfigureAwait(false);

        return new PveNodeStatus
        {
            Node = node,
            CpuUsagePercent = GetDouble(data, "cpu") * 100.0,
            CpuCores = TryObject(data, "cpuinfo", out var cpuInfo) ? GetInt(cpuInfo, "cpus") : 0,
            CpuSockets = TryObject(data, "cpuinfo", out cpuInfo) ? GetInt(cpuInfo, "sockets") : 0,
            CpuModel = TryObject(data, "cpuinfo", out cpuInfo) ? GetString(cpuInfo, "model") : string.Empty,
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
    /// <summary>
    ///     노드의 설정 묶음을 문자열 맵으로 읽는다(DNS·시간·노트 등).
    ///     게스트 설정과 같은 모양이라 화면에서 같은 편집 흐름을 쓸 수 있다.
    /// </summary>
    /// <param name="section">nodes/{node} 아래 경로. 예: "dns", "time", "config".</param>
    [Versioning.PveApi("GET", "/nodes/{node}/dns")]
    [Versioning.PveApi("GET", "/nodes/{node}/time")]
    public Task<IReadOnlyDictionary<string, string>> GetNodeSectionAsync(
        string node, string section, CancellationToken ct = default)
    {
        return GetObjectAsync($"nodes/{Escape(node)}/{section}", ct);
    }
    /// <summary>바뀐 항목만 노드 설정에 적용한다(PUT nodes/{node}/{section}).</summary>
    [Versioning.PveApi("PUT", "/nodes/{node}/dns")]
    [Versioning.PveApi("PUT", "/nodes/{node}/time")]
    public Task<string> UpdateNodeSectionAsync(
        string node, string section, IReadOnlyDictionary<string, string> changes, CancellationToken ct = default)
    {
        return SendWriteAsync(HttpMethod.Put, $"nodes/{Escape(node)}/{section}", changes, ct);
    }
    /// <summary>
    ///     노드 시스템 로그 (GET nodes/{node}/syslog). 최신 줄이 뒤에 오며, 줄마다 번호가 붙어 온다.
    /// </summary>
    /// <param name="lines">가져올 줄 수(서버 기본값보다 많이 요청하면 잘릴 수 있다).</param>
    [Versioning.PveApi("GET", "/nodes/{node}/syslog")]
    public async Task<IReadOnlyList<string>> GetNodeSyslogAsync(
        string node, int lines = 200, CancellationToken ct = default)
    {
        var data = await GetJsonAsync($"nodes/{Escape(node)}/syslog?limit={lines}", ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Array) return [];

        var result = new List<string>(data.GetArrayLength());
        foreach (var item in data.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("t", out var line)
                ? line.GetString()
                : item.ToString();
            if (!string.IsNullOrEmpty(text)) result.Add(text);
        }

        return result;
    }
}
