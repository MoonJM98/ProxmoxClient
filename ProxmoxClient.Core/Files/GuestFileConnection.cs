using System.Text.Json.Serialization;

namespace ProxmoxClient.Core.Files;

/// <summary>비밀값을 담지 않는 게스트별 파일 연결 설정.</summary>
public sealed record GuestFileConnection
{
    /// <summary>연결 방식 — 비어 있으면(이전 버전 설정) UseSftp 로 정한다(<see cref="EffectiveTransport" />).</summary>
    public GuestFileTransport? Transport { get; init; }

    /// <summary>SFTP 인지 — 이전 버전과 SFTP 코드가 읽는다. <see cref="WithTransport" /> 가 함께 맞춘다.</summary>
    public bool UseSftp { get; init; }

    public Guid? SftpProfileId { get; init; }

    /// <summary>SMB 접속 설정(이 게스트).</summary>
    public RemoteFileSettings? Smb { get; init; }

    /// <summary>FTP 접속 설정(이 게스트).</summary>
    public RemoteFileSettings? Ftp { get; init; }

    // 이전 버전 JSON을 읽어 새 프로필로 옮기는 데만 사용하는 필드.
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string UserName { get; init; } = string.Empty;
    public bool RememberPassword { get; init; }

    [JsonIgnore]
    public GuestFileTransport EffectiveTransport =>
        Transport ?? (UseSftp ? GuestFileTransport.Sftp : GuestFileTransport.Agent);

    public GuestFileConnection WithTransport(GuestFileTransport transport) =>
        this with { Transport = transport, UseSftp = transport == GuestFileTransport.Sftp };

    /// <summary>이 방식의 SMB·FTP 설정(없으면 빈 설정).</summary>
    public RemoteFileSettings RemoteFor(GuestFileTransport transport) =>
        (transport == GuestFileTransport.Smb ? Smb : Ftp) ?? new RemoteFileSettings();

    public GuestFileConnection WithRemote(GuestFileTransport transport, RemoteFileSettings settings) =>
        transport == GuestFileTransport.Smb ? this with { Smb = settings } : this with { Ftp = settings };

    public GuestFileConnection Normalize() => this with
    {
        Smb = Smb?.Normalize(),
        Ftp = Ftp?.Normalize(),
        Host = NormalizeHost(Host ?? string.Empty),
        Port = Port is >= 1 and <= 65535 ? Port : 22,
        UserName = (UserName ?? string.Empty).Trim()
    };

    public static string NormalizeHost(string host) => host.Trim().Trim('[', ']').ToLowerInvariant();
    public static string GuestKey(Guid profileId, string kind, int vmId) => $"{profileId:D}/{kind}/{vmId}";
    [JsonIgnore]
    public string ServerKey => $"[{NormalizeHost(Host)}]:{Port}";
    [JsonIgnore]
    public string CredentialTarget => $"ProxmoxClient/SFTP/{ServerKey}/{Uri.EscapeDataString(UserName)}";
}
