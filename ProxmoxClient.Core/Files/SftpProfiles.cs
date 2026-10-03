using System.Text.Json.Serialization;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.Core.Files;

public enum SftpAuthenticationMode
{
    Password,
    PrivateKey,
    DefaultKeys,
    Certificate
}

public sealed record SftpAccountProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public SftpAuthenticationMode AuthenticationMode { get; init; } = SftpAuthenticationMode.Password;
    public string PrivateKeyPath { get; init; } = string.Empty;
    public string CertificatePath { get; init; } = string.Empty;
    public bool RememberPassword { get; init; }
    public bool RememberPassphrase { get; init; }
    public long CredentialRevision { get; init; }
    [JsonIgnore] public string CredentialTarget => $"ProxmoxClient/SFTP/Account/{Id:D}";
    [JsonIgnore] public string PassphraseCredentialTarget => $"ProxmoxClient/SFTP/Account/{Id:D}/Passphrase";
    [JsonIgnore] public string SecretTarget => AuthenticationMode == SftpAuthenticationMode.Password
        ? CredentialTarget : PassphraseCredentialTarget;
    [JsonIgnore] public bool RememberSecret => AuthenticationMode == SftpAuthenticationMode.Password
        ? RememberPassword : RememberPassphrase;
    public SftpAccountProfile Normalize() => this with
    {
        Name = (Name ?? string.Empty).Trim(), UserName = (UserName ?? string.Empty).Trim(),
        AuthenticationMode = Enum.IsDefined(AuthenticationMode) ? AuthenticationMode : SftpAuthenticationMode.Password,
        PrivateKeyPath = (PrivateKeyPath ?? string.Empty).Trim(),
        CertificatePath = (CertificatePath ?? string.Empty).Trim(),
        CredentialRevision = Math.Max(0, CredentialRevision)
    };
    public override string ToString() => Name;
}

public sealed record SftpConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public Guid AccountId { get; init; }
    [JsonIgnore] public string ServerKey => $"[{GuestFileConnection.NormalizeHost(Host)}]:{Port}";
    public SftpConnectionProfile Normalize() => this with
    {
        Name = (Name ?? string.Empty).Trim(), Host = GuestFileConnection.NormalizeHost(Host ?? string.Empty),
        Port = Port is >= 1 and <= 65535 ? Port : 22
    };
    public override string ToString() => Name;
}

/// <summary>기존 호스트별 설정을 계정·접속 프로필로 옮긴다. 비밀번호 이전은 호출자가 처리한다.</summary>
public static class SftpProfileMigration
{
    public static ConsoleSettings Migrate(ConsoleSettings settings,
        Action<GuestFileConnection, SftpAccountProfile>? migratePassword = null)
    {
        var accounts = new Dictionary<Guid, SftpAccountProfile>(settings.SftpAccounts);
        var profiles = new Dictionary<Guid, SftpConnectionProfile>(settings.SftpConnections);
        var mappings = new Dictionary<string, GuestFileConnection>(settings.GuestFileConnections);
        var migrated = new Dictionary<string, Guid>();
        foreach (var (key, legacy) in settings.GuestFileConnections.OrderByDescending(pair => pair.Value.RememberPassword))
        {
            if (legacy.SftpProfileId is not null || legacy.Host.Length == 0 || legacy.UserName.Length == 0) continue;
            if (!migrated.TryGetValue(legacy.CredentialTarget, out var profileId))
            {
                // 같은 사용자명만으로는 비밀번호가 같다고 판단할 수 없다. 기존 접속 대상별로 계정을 만든다.
                var account = new SftpAccountProfile
                {
                    Name = $"{legacy.UserName} ({legacy.Host}:{legacy.Port})", UserName = legacy.UserName,
                    RememberPassword = legacy.RememberPassword
                };
                migratePassword?.Invoke(legacy, account);
                var profile = new SftpConnectionProfile
                {
                    Name = $"{legacy.Host}:{legacy.Port}", Host = legacy.Host, Port = legacy.Port, AccountId = account.Id
                };
                accounts[account.Id] = account;
                profiles[profile.Id] = profile;
                migrated[legacy.CredentialTarget] = profileId = profile.Id;
            }
            mappings[key] = new GuestFileConnection { UseSftp = legacy.UseSftp, SftpProfileId = profileId };
        }
        return settings with { SftpAccounts = accounts, SftpConnections = profiles, GuestFileConnections = mappings };
    }
}
