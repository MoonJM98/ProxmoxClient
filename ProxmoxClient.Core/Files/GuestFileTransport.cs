namespace ProxmoxClient.Core.Files;

/// <summary>게스트 파일 창의 연결 방식.</summary>
public enum GuestFileTransport
{
    /// <summary>VM 은 게스트 에이전트, CT 는 노드 셸.</summary>
    Agent,
    Sftp,

    /// <summary>Windows 파일 공유(\\주소\공유) — PC 의 Windows SMB 클라이언트를 쓴다.</summary>
    Smb,

    /// <summary>FTP·FTPS.</summary>
    Ftp
}

/// <summary>FTP 암호화 — 명시적 FTPS(AUTH TLS, 기본), 암시적 FTPS(보통 990), 암호화 없음(평문).</summary>
public enum FtpSecurity
{
    Explicit,
    Implicit,
    None
}

/// <summary>
///     게스트별 SMB·FTP 접속 설정(비밀값 없음 — 비밀번호는 Windows 자격 증명 관리자에 둔다).
///     Port 0 은 프로토콜 기본값(FTP 21, 암시적 FTPS 990). SMB 는 포트를 고를 수 없다(445).
/// </summary>
public sealed record RemoteFileSettings
{
    public const int FtpPort = 21;
    public const int FtpsImplicitPort = 990;
    public const int SmbPort = 445;

    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }

    /// <summary>SMB 공유 이름(예: C$, Users) — 그 아래 폴더까지 적어도 된다(Users\Public).</summary>
    public string Share { get; init; } = string.Empty;

    /// <summary>사용자(SMB 는 DOMAIN\user·user@domain 도 된다). 비우면 SMB 는 지금 Windows 계정, FTP 는 anonymous.</summary>
    public string UserName { get; init; } = string.Empty;

    public FtpSecurity Security { get; init; } = FtpSecurity.Explicit;
    public bool RememberPassword { get; init; }

    public RemoteFileSettings Normalize() => this with
    {
        Host = GuestFileConnection.NormalizeHost(Host ?? string.Empty),
        Port = Port is >= 0 and <= 65535 ? Port : 0,
        Share = (Share ?? string.Empty).Trim().Trim('\\', '/').Replace('/', '\\'),
        UserName = (UserName ?? string.Empty).Trim()
    };

    /// <summary>열 수 있을 만큼 채워졌는지(SMB 는 공유 이름도 필요).</summary>
    public bool IsComplete(GuestFileTransport transport) =>
        Host.Length > 0 && (transport != GuestFileTransport.Smb || Share.Length > 0);

    /// <summary>실제로 접속할 FTP 포트.</summary>
    public int FtpPortOrDefault => Port > 0 ? Port : Security == FtpSecurity.Implicit ? FtpsImplicitPort : FtpPort;
}
