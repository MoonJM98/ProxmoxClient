namespace ProxmoxClient.Core.Models;

/// <summary>Cluster version info (GET /version).</summary>
public sealed class PveVersion
{
    /// <summary>Proxmox VE version, e.g. "8.2.4".</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Package release number, e.g. "24".</summary>
    public string Release { get; init; } = string.Empty;

    /// <summary>Repository id, e.g. "eco".</summary>
    public string RepoId { get; init; } = string.Empty;
}