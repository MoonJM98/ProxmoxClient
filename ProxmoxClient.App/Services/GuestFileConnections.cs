using System.ComponentModel;
using System.Net.Sockets;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Files;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Vnc;
using Renci.SshNet.Common;

namespace ProxmoxClient.App.Services;

/// <summary>게스트 → 접속 프로필 → 계정 프로필. 비밀번호의 저장 키는 계정 ID다.</summary>
internal sealed class GuestFileConnections(Window owner, ProxmoxApiClient api, PveResource guest, bool defaultSftp = false)
{
    private readonly ConsoleSettingsStore _store = new();
    private readonly SftpProfileStore _profiles = new();
    private readonly string _key = GuestFileConnection.GuestKey(api.Profile.Id, guest.Kind.ToString(), guest.VmId);
    private string? _secret;
    private SftpAccountProfile? _secretAccount;

    public async Task<GuestFileConnection> LoadAsync()
    {
        var settings = await _profiles.LoadAsync();
        var connection = settings.GuestFileConnections.GetValueOrDefault(_key) ?? new GuestFileConnection();
        return defaultSftp ? connection with { UseSftp = true } : connection;
    }

    private Task<ConsoleSettings> SaveAsync(GuestFileConnection connection) => _store.UpdateAsync(settings => settings with
    {
        GuestFileConnections = new Dictionary<string, GuestFileConnection>(settings.GuestFileConnections) { [_key] = connection }
    });

    public async Task<bool> SelectAsync(bool sftp, bool configure = false)
    {
        _secret = null;
        _secretAccount = null;
        var connection = await LoadAsync();
        // 메뉴에서 고른 방식은 접속 설정 완료·인증 성공 여부와 무관하게 게스트 기본값으로 기억한다.
        if (!configure)
        {
            connection = connection with { UseSftp = sftp };
            await SaveAsync(connection);
        }
        var settings = await _profiles.LoadAsync();
        if (sftp && (configure || connection.SftpProfileId is not { } id || !settings.SftpConnections.ContainsKey(id)))
        {
            var dialog = new SftpProfilesWindow(connection.SftpProfileId) { Owner = owner };
            if (dialog.ShowDialog() != true || dialog.SelectedProfileId is not { } selected) return false;
            settings = await _profiles.LoadAsync();
            if (!settings.SftpConnections.TryGetValue(selected, out var profile)) return false;
            connection = new GuestFileConnection { UseSftp = true, SftpProfileId = selected };
            _secret = dialog.SelectedPassword;
            _secretAccount = dialog.SelectedAccount;
        }
        await SaveAsync(connection with { UseSftp = sftp });
        return true;
    }

    private (string Secret, bool Remember)? PromptSecret(SftpAccountProfile account, SftpConnectionProfile profile, bool failed)
    {
        var password = account.AuthenticationMode == SftpAuthenticationMode.Password;
        var title = password ? failed ? "Sftp_AuthFailed" : "Sftp_PasswordFor"
            : failed ? "Sftp_KeyAuthFailed" : "Sftp_PassphraseFor";
        var dialog = new FormDialog(Loc.T(title, account.Name, profile.Name),
        [
            new() { Key = "secret", LabelKey = password ? "Sftp_Password" : "Sftp_Passphrase", Kind = FormFieldKind.Password },
            new() { Key = "remember", LabelKey = password ? "Sftp_Remember" : "Sftp_RememberPassphrase", Kind = FormFieldKind.Bool,
                Initial = account.RememberSecret ? "1" : "0" }
        ], values => values["secret"].Length > 1280 ? Loc.T(password ? "Sftp_PasswordLong" : "Sftp_PassphraseLong") : null) { Owner = owner };
        return dialog.ShowDialog() == true && dialog.Result is { } result
            ? (result["secret"], result["remember"] == "1") : null;
    }

    public async Task<IGuestFileSystem> OpenAsync(CancellationToken ct)
    {
        var connection = await LoadAsync();
        ct.ThrowIfCancellationRequested();
        if (!connection.UseSftp)
            return guest.Kind == ResourceKind.Qemu
                ? await AgentFileSystem.OpenAsync(api, guest.Node, guest.VmId, ct)
                : await ContainerFileSystem.OpenAsync(api, guest.Node, guest.VmId, ct);
        var settings = await _profiles.LoadAsync(ct);
        if (connection.SftpProfileId is not { } profileId || !settings.SftpConnections.ContainsKey(profileId))
        {
            if (!await SelectAsync(true, true)) throw new OperationCanceledException(ct);
            connection = await LoadAsync();
            settings = await _profiles.LoadAsync(ct);
        }
        ct.ThrowIfCancellationRequested();
        if (connection.SftpProfileId is not { } selectedId || !settings.SftpConnections.TryGetValue(selectedId, out var profile)
            || !settings.SftpAccounts.TryGetValue(profile.AccountId, out var account))
            throw new GuestFileException(Loc.T("Sftp_SelectAccount"));

        var fingerprint = settings.SftpHostKeys.GetValueOrDefault(profile.ServerKey);
        try
        {
            var secret = _secretAccount == account ? _secret : null;
            if (secret is null && account.RememberSecret) secret = WindowsCredentialStore.Read(account.SecretTarget);
            var remember = account.RememberSecret;
            var password = account.AuthenticationMode == SftpAuthenticationMode.Password;
            var passwordRetries = 0;
            var keyPrompts = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (password && secret is null)
                {
                    var entered = PromptSecret(account, profile, passwordRetries > 0) ?? throw new OperationCanceledException(ct);
                    secret = entered.Secret;
                    remember = entered.Remember;
                }
                SftpFileSystem? files = null;
                try
                {
                    files = await SftpFileSystem.OpenAsync(profile.Host, profile.Port, account, secret,
                        received => owner.Dispatcher.Invoke(() =>
                        {
                            if (ct.IsCancellationRequested || !owner.IsLoaded) return false;
                            if (fingerprint == received) return true;
                            var message = Loc.T(fingerprint is null ? "Sftp_TrustKey" : "Sftp_KeyChanged",
                                profile.ServerKey, received, fingerprint ?? string.Empty);
                            if (ThemedMessageBox.Show(owner, message, Loc.T("Sftp_HostKey"), MessageBoxButton.YesNo,
                                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
                            fingerprint = received;
                            return true;
                        }), ct);
                    ct.ThrowIfCancellationRequested();
                    // 사용하지 않은 키 암호나 다른 창에서 바뀐 계정의 암호는 저장하지 않는다.
                    if (!await _profiles.SaveAuthenticatedSecretAsync(account,
                            password || files.UsedPassphrase ? secret : null,
                            (password || files.UsedPassphrase) && remember))
                        throw new GuestFileException(Loc.T("Sftp_AccountChanged"));
                    await _store.UpdateAsync(currentSettings => currentSettings with
                    {
                        SftpHostKeys = fingerprint is null ? currentSettings.SftpHostKeys :
                            new Dictionary<string, string>(currentSettings.SftpHostKeys) { [profile.ServerKey] = fingerprint }
                    }, ct);
                    return files;
                }
                catch (SshAuthenticationException) when (password && passwordRetries++ == 0)
                {
                    files?.Dispose();
                    secret = null;
                }
                catch (GuestFileException ex) when ((ex is SftpPassphraseRequiredException or SftpPassphraseException) && keyPrompts < 2)
                {
                    files?.Dispose();
                    ct.ThrowIfCancellationRequested();
                    var entered = PromptSecret(account, profile, keyPrompts++ > 0 || ex is SftpPassphraseException)
                        ?? throw new OperationCanceledException(ct);
                    secret = entered.Secret;
                    remember = entered.Remember;
                }
                catch { files?.Dispose(); throw; }
            }
        }
        catch (Exception ex) when (ex is SshException or SocketException or Win32Exception)
        {
            throw new GuestFileException(ex.Message);
        }
        finally { _secret = null; _secretAccount = null; }
    }
}
