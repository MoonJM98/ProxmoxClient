namespace ProxmoxClient.Core.Vpn;

/// <summary>OpenVPN 실행/제어 실패.</summary>
public sealed class OpenVpnException : Exception
{
    public OpenVpnException(string message) : base(message)
    {
    }

    public OpenVpnException(string message, Exception innerException) : base(message, innerException)
    {
    }
}