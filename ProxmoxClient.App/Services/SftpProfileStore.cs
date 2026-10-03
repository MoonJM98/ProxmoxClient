using ProxmoxClient.Core.Files;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Services;

internal sealed class SftpProfileStore(ConsoleSettingsStore? store = null)
{
    private readonly ConsoleSettingsStore _store = store ?? new();

    public async Task<ConsoleSettings> LoadAsync(CancellationToken ct = default)
    {
        var settings = await _store.LoadAsync(ct);
        if (!settings.GuestFileConnections.Values.Any(c => c.SftpProfileId is null && c.Host.Length > 0 && c.UserName.Length > 0))
            return settings;
        var oldTargets = new List<string>();
        settings = await _store.UpdateAsync(current => SftpProfileMigration.Migrate(current, (legacy, account) =>
        {
            if (legacy.RememberPassword && WindowsCredentialStore.Read(legacy.CredentialTarget) is { } password)
            {
                WindowsCredentialStore.Write(account.CredentialTarget, account.UserName, password);
                oldTargets.Add(legacy.CredentialTarget);
            }
        }), ct);
        // 새 매핑과 자격 증명이 저장된 뒤에만 호스트 기반의 기존 항목을 정리한다.
        foreach (var target in oldTargets) WindowsCredentialStore.Delete(target);
        return settings;
    }

    public Task<ConsoleSettings> SaveAccountAsync(SftpAccountProfile account, string? secret) =>
        _store.UpdateAsync(settings =>
        {
            account = account.Normalize();
            var old = settings.SftpAccounts.GetValueOrDefault(account.Id);
            var identityChanged = old is not null && (old.UserName != account.UserName
                || old.AuthenticationMode != account.AuthenticationMode || old.PrivateKeyPath != account.PrivateKeyPath
                || old.CertificatePath != account.CertificatePath);
            if (identityChanged)
            {
                WindowsCredentialStore.Delete(account.CredentialTarget);
                WindowsCredentialStore.Delete(account.PassphraseCredentialTarget);
            }
            if (!account.RememberSecret) WindowsCredentialStore.Delete(account.SecretTarget);
            else if (secret is not null) WindowsCredentialStore.Write(account.SecretTarget, account.UserName, secret);
            account = account with { CredentialRevision = (old?.CredentialRevision ?? 0) + 1 };
            return settings with
            {
                SftpAccounts = new Dictionary<Guid, SftpAccountProfile>(settings.SftpAccounts) { [account.Id] = account }
            };
        });

    public Task<bool> SaveAuthenticatedPasswordAsync(SftpAccountProfile account, string password) =>
        SaveAuthenticatedSecretAsync(account, password, account.RememberPassword);

    /// <summary>연결 시작 시의 계정이 그대로인 경우에만 인증한 암호와 저장 선택을 반영한다.</summary>
    public async Task<bool> SaveAuthenticatedSecretAsync(SftpAccountProfile snapshot, string? secret, bool remember)
    {
        var found = false;
        await _store.UpdateAsync(settings =>
        {
            if (!settings.SftpAccounts.TryGetValue(snapshot.Id, out var latest) || latest != snapshot)
                return settings;
            found = true;
            var changed = latest.RememberSecret != remember;
            if (remember && secret is not null)
            {
                changed |= WindowsCredentialStore.Read(latest.SecretTarget) != secret;
                if (changed) WindowsCredentialStore.Write(latest.SecretTarget, latest.UserName, secret);
            }
            else if (!remember) WindowsCredentialStore.Delete(latest.SecretTarget);
            if (!changed) return settings;
            var updated = latest.AuthenticationMode == SftpAuthenticationMode.Password
                ? latest with { RememberPassword = remember }
                : latest with { RememberPassphrase = remember };
            return settings with
            {
                SftpAccounts = new Dictionary<Guid, SftpAccountProfile>(settings.SftpAccounts)
                    { [snapshot.Id] = updated with { CredentialRevision = latest.CredentialRevision + 1 } }
            };
        });
        return found;
    }

    public Task<ConsoleSettings> SaveConnectionAsync(SftpConnectionProfile profile) => _store.UpdateAsync(settings =>
    {
        if (!settings.SftpAccounts.ContainsKey(profile.AccountId)) throw new InvalidOperationException("Sftp_SelectAccount");
        return settings with
        {
            SftpConnections = new Dictionary<Guid, SftpConnectionProfile>(settings.SftpConnections) { [profile.Id] = profile }
        };
    });

    public Task<ConsoleSettings> DeleteAccountAsync(Guid id) => _store.UpdateAsync(settings =>
    {
        if (settings.SftpConnections.Values.Any(profile => profile.AccountId == id))
            throw new InvalidOperationException("Sftp_AccountInUse");
        if (settings.SftpAccounts.TryGetValue(id, out var account))
        {
            WindowsCredentialStore.Delete(account.CredentialTarget);
            WindowsCredentialStore.Delete(account.PassphraseCredentialTarget);
        }
        return settings with { SftpAccounts = settings.SftpAccounts.Where(pair => pair.Key != id).ToDictionary() };
    });

    public Task<ConsoleSettings> DeleteConnectionAsync(Guid id) => _store.UpdateAsync(settings => settings with
    {
        SftpConnections = settings.SftpConnections.Where(pair => pair.Key != id).ToDictionary(),
        GuestFileConnections = settings.GuestFileConnections.ToDictionary(pair => pair.Key,
            pair => pair.Value.SftpProfileId == id ? pair.Value with { SftpProfileId = null } : pair.Value)
    });

    public Task<ConsoleSettings> ForgetPasswordAsync(Guid id) => _store.UpdateAsync(settings =>
    {
        if (!settings.SftpAccounts.TryGetValue(id, out var account)) return settings;
        WindowsCredentialStore.Delete(account.SecretTarget);
        var updated = account.AuthenticationMode == SftpAuthenticationMode.Password
            ? account with { RememberPassword = false }
            : account with { RememberPassphrase = false };
        return settings with
        {
            SftpAccounts = new Dictionary<Guid, SftpAccountProfile>(settings.SftpAccounts)
                { [id] = updated with { CredentialRevision = account.CredentialRevision + 1 } }
        };
    });
}
