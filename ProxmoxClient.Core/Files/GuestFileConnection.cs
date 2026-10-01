using System.Text.Json.Serialization;

namespace ProxmoxClient.Core.Files;

/// <summary>비밀값을 담지 않는 게스트별 파일 연결 설정.</summary>
public sealed record GuestFileConnection
{
    public bool UseSftp { get; init; }
    public Guid? SftpProfileId { get; init; }
    // 이전 버전 JSON을 읽어 새 프로필로 옮기는 데만 사용하는 필드.
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string UserName { get; init; } = string.Empty;
    public bool RememberPassword { get; init; }

    public GuestFileConnection Normalize() => this with
    {
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
