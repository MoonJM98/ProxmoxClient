namespace ProxmoxClient.Core.Models;

/// <summary>
///     Identifies the two guest (compute resource) kinds managed by a Proxmox cluster.
/// </summary>
public enum ResourceKind
{
    /// <summary>QEMU/KVM virtual machine (API path segment "qemu").</summary>
    Qemu,

    /// <summary>LXC container (API path segment "lxc").</summary>
    Lxc
}

/// <summary>Convenience helpers for <see cref="ResourceKind" />.</summary>
public static class ResourceKindExtensions
{
    /// <summary>Gets the Proxmox API path segment for the kind ("qemu" or "lxc").</summary>
    public static string ApiSegment(this ResourceKind kind)
    {
        return kind switch
        {
            ResourceKind.Qemu => "qemu",
            ResourceKind.Lxc => "lxc",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    /// <summary>Gets a short human-readable label ("VM" or "CT").</summary>
    public static string Label(this ResourceKind kind)
    {
        return kind switch
        {
            ResourceKind.Qemu => "VM",
            ResourceKind.Lxc => "CT",
            _ => kind.ToString()
        };
    }
}