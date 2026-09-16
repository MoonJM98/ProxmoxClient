namespace ProxmoxClient.Core.Models;

/// <summary>
///     Detailed runtime status of a single node (GET /nodes/{node}/status).
/// </summary>
public sealed class PveNodeStatus
{
    /// <summary>Node name.</summary>
    public string Node { get; init; } = string.Empty;

    /// <summary>CPU usage in percent (0-100) across all cores.</summary>
    public double CpuUsagePercent { get; init; }

    /// <summary>Logical CPU count.</summary>
    public int CpuCores { get; init; }

    /// <summary>Kernel version string.</summary>
    public string KernelVersion { get; init; } = string.Empty;

    /// <summary>Node uptime in seconds.</summary>
    public long UptimeSeconds { get; init; }

    /// <summary>1-minute load average.</summary>
    public double LoadAverage1 { get; init; }

    /// <summary>5-minute load average.</summary>
    public double LoadAverage5 { get; init; }

    /// <summary>15-minute load average.</summary>
    public double LoadAverage15 { get; init; }

    /// <summary>Used memory in bytes.</summary>
    public long MemUsedBytes { get; init; }

    /// <summary>Total memory in bytes.</summary>
    public long MemTotalBytes { get; init; }

    /// <summary>Used swap in bytes.</summary>
    public long SwapUsedBytes { get; init; }

    /// <summary>Total swap in bytes.</summary>
    public long SwapTotalBytes { get; init; }

    /// <summary>Total root filesystem size in bytes.</summary>
    public long RootFsTotalBytes { get; init; }

    /// <summary>Used root filesystem bytes.</summary>
    public long RootFsUsedBytes { get; init; }

    /// <summary>Available root filesystem bytes.</summary>
    public long RootFsAvailableBytes { get; init; }
}