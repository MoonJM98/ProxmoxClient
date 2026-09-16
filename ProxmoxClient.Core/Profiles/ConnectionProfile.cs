using System.ComponentModel;
using System.Security;
using System.Text.Json.Serialization;

namespace ProxmoxClient.Core.Profiles;

/// <summary>Authentication mode used to talk to the Proxmox API.</summary>
public enum AuthMode
{
    /// <summary>Username + password exchanged for a ticket/CSRF token pair (POST /access/ticket).</summary>
    Password,

    /// <summary>Static API token: "user@realm!tokenid" + secret (Authorization header, no login).</summary>
    ApiToken
}

/// <summary>Proxy protocol used for reaching the Proxmox host.</summary>
public enum ProxyMode
{
    /// <summary>Direct connection.</summary>
    None,

    /// <summary>HTTP proxy (CONNECT tunneling for HTTPS).</summary>
    Http,

    /// <summary>HTTPS proxy.</summary>
    Https,

    /// <summary>SOCKS5 proxy (natively supported by .NET's SocketsHttpHandler).</summary>
    Socks5,

    /// <summary>Use the operating system's proxy settings (WinINET).</summary>
    System
}

/// <summary>
///     Everything needed to connect to one Proxmox endpoint: server address,
///     credentials, proxy options, and optional OpenVPN tunnel settings.
///     Serialized as JSON by <see cref="ProfileStore" />.
/// </summary>
public sealed class ConnectionProfile
{
    /// <summary>복호화하지 못한(다른 Windows 계정·PC 에서 암호화된) 프록시 비밀번호 — 저장 시 원본 그대로 유지.</summary>
    private string? _unreadableProxyPassword;

    /// <summary>Stable unique id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>User-visible profile name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Proxmox host name or IP address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>API port (Proxmox default 8006).</summary>
    public int Port { get; set; } = 8006;

    /// <summary>
    ///     공인 CA 로 검증되지 않는 서버 인증서(Proxmox 기본 자체 서명 등)를 지문 고정(TOFU) 방식으로 허용.
    ///     처음 연결할 때 사용자가 지문을 확인해 신뢰하면 <see cref="CertificateThumbprint" /> 에 저장하고,
    ///     이후 지문이 바뀌면 연결을 차단하고 다시 확인을 요청한다.
    /// </summary>
    public bool AcceptSelfSignedCertificate { get; set; } = true;

    /// <summary>신뢰한 서버 인증서의 SHA-256 지문(16진수 대문자). null 이면 아직 신뢰한 인증서가 없다.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>Authentication mode.</summary>
    public AuthMode AuthMode { get; set; } = AuthMode.Password;

    /// <summary>User name including realm, e.g. "root@pam". Password mode only.</summary>
    public string UserName { get; set; } = "root@pam";

    /// <summary>
    ///     Password. Password mode only. RUNTIME ONLY — never serialized to disk.
    ///     Held as SecureString to reduce plain-text exposure in managed memory.
    /// </summary>
    [JsonIgnore]
    public SecureString? Password { get; set; }

    /// <summary>Full token id, e.g. "root@pam!mytoken". Token mode only.</summary>
    public string? ApiTokenId { get; set; }

    /// <summary>
    ///     Token secret (UUID). Token mode only. RUNTIME ONLY — never serialized.
    /// </summary>
    [JsonIgnore]
    public SecureString? ApiTokenSecret { get; set; }

    /// <summary>Proxy protocol.</summary>
    public ProxyMode ProxyMode { get; set; } = ProxyMode.None;

    /// <summary>Proxy host name or IP.</summary>
    public string? ProxyHost { get; set; }

    /// <summary>Proxy port.</summary>
    public int? ProxyPort { get; set; }

    /// <summary>Proxy user name for authenticated proxies.</summary>
    public string? ProxyUserName { get; set; }

    /// <summary>
    ///     Proxy password (메모리상 평문). 디스크에는 평문으로 쓰지 않고 <see cref="ProxyPasswordProtected" /> 로 암호화해 저장한다.
    /// </summary>
    [JsonIgnore]
    public string? ProxyPassword { get; set; }

    /// <summary>디스크 저장용 — 프록시 비밀번호를 현재 Windows 계정 기준 DPAPI 로 암호화한 값.</summary>
    [JsonPropertyName("ProxyPasswordProtected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? ProxyPasswordProtected
    {
        get => ProxyPassword is { Length: > 0 } plain ? SecretProtector.Protect(plain) : _unreadableProxyPassword;
        set
        {
            _unreadableProxyPassword = null;
            if (string.IsNullOrEmpty(value)) return;

            if (SecretProtector.TryUnprotect(value, out var plain))
                ProxyPassword = plain;
            else
                _unreadableProxyPassword = value; // 복호화할 수 없어도 버리지 않는다(원래 계정·PC 에선 여전히 유효)
        }
    }

    /// <summary>레거시(이전 버전의 평문 저장) 읽기 전용 — 불러올 때만 쓰이고 저장 시에는 출력하지 않는다.</summary>
    [JsonPropertyName("ProxyPassword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? LegacyProxyPassword
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value)) ProxyPassword ??= value;
        }
    }

    /// <summary>Bring up an OpenVPN tunnel before connecting to this host.</summary>
    public bool UseVpn { get; set; }

    /// <summary>Path to the .ovpn config file.</summary>
    public string? VpnConfigPath { get; set; }

    /// <summary>Explicit openvpn.exe path; null = auto-detect.</summary>
    public string? VpnExePath { get; set; }

    /// <summary>Field-by-field copy (secrets included — runtime copies only).</summary>
    public ConnectionProfile Clone()
    {
        var clone = new ConnectionProfile
        {
            Id = Id,
            Name = Name,
            Host = Host,
            Port = Port,
            AcceptSelfSignedCertificate = AcceptSelfSignedCertificate,
            CertificateThumbprint = CertificateThumbprint,
            AuthMode = AuthMode,
            UserName = UserName,
            Password = Password?.Copy(),
            ApiTokenId = ApiTokenId,
            ApiTokenSecret = ApiTokenSecret?.Copy(),
            ProxyMode = ProxyMode,
            ProxyHost = ProxyHost,
            ProxyPort = ProxyPort,
            ProxyUserName = ProxyUserName,
            ProxyPassword = ProxyPassword,
            UseVpn = UseVpn,
            VpnConfigPath = VpnConfigPath,
            VpnExePath = VpnExePath
        };
        clone._unreadableProxyPassword = _unreadableProxyPassword;
        return clone;
    }
}