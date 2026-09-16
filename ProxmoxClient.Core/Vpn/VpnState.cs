namespace ProxmoxClient.Core.Vpn;

/// <summary>Lifecycle state of the OpenVPN tunnel, mirrored from the management interface.</summary>
public enum VpnState
{
    /// <summary>No tunnel running.</summary>
    Disconnected,

    /// <summary>Process started, management attached, waiting for the tunnel to come up.</summary>
    Connecting,

    /// <summary>Tunnel established (a virtual IP may be known).</summary>
    Connected,

    /// <summary>Established tunnel lost connectivity and OpenVPN is retrying.</summary>
    Reconnecting,

    /// <summary>SIGTERM sent, waiting for the process to exit.</summary>
    Disconnecting,

    /// <summary>OpenVPN reported a fatal error or auth requirement.</summary>
    Error
}