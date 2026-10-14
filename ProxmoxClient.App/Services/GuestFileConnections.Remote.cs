using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Input;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Files;

namespace ProxmoxClient.App.Services;

/// <summary>
///     SMB·FTP — 게스트마다 주소·공유/포트·사용자를 기억하고, 비밀번호는 고르면 Windows 자격 증명 관리자에 둔다.
///     설정 창을 열 때 게스트 IP(에이전트·CT interfaces)와 열린 포트(SSH·SMB·FTP)를 알아봐 안내한다.
///     FTPS 인증서는 시스템이 믿지 않으면(자체 서명 등) 처음·바뀌었을 때 묻고 지문을 기억한다.
/// </summary>
internal sealed partial class GuestFileConnections
{
    private const int MaxLoginAttempts = 3;
    private const int MaxProbedAddresses = 3;
    private static readonly TimeSpan AddressTimeout = TimeSpan.FromSeconds(4);

    /// <summary>방금 설정 창에서 받은 비밀번호 — 바로 이어지는 연결에 한 번만 쓴다.</summary>
    private string? _remoteSecret;

    /// <summary>
    ///     이번에 신뢰한 FTPS 인증서([주소]:포트 → 지문) — 저장 전에도(로그인 재시도·데이터 연결마다 오는 확인)
    ///     다시 묻지 않게. 연결에 성공하면 설정에도 저장한다.
    /// </summary>
    private readonly Dictionary<string, string> _trustedCertificates = [];

    private string CredentialTarget(GuestFileTransport transport) =>
        $"ProxmoxClient/{(transport == GuestFileTransport.Smb ? "SMB" : "FTP")}/{_key}";

    /// <summary>설정 창 — configure 이거나 아직 덜 채워졌을 때만. 취소하면 false.</summary>
    private async Task<bool> ConfigureRemoteAsync(GuestFileTransport transport, GuestFileConnection connection,
        bool configure)
    {
        var current = connection.RemoteFor(transport);
        if (!configure && current.IsComplete(transport)) return true;

        var detection = await DetectAsync(transport);
        var entered = RemoteConnectionForm.Show(owner, transport, guest.Name, current, detection);
        if (entered is not var (settings, password)) return false;

        // 기억하기로 한 비밀번호는 로그인에 성공한 뒤에 저장한다(RememberSecretAsync) — 틀린 비밀번호를 남기지 않게
        if (password.Length > 0) _remoteSecret = password;
        if (!settings.RememberPassword) WindowsCredentialStore.Delete(CredentialTarget(transport));
        await SaveAsync(connection.WithTransport(transport).WithRemote(transport, settings));
        return true;
    }

    /// <summary>게스트 IP 와 열린 파일 서비스 포트 — 이 방식의 포트가 열린 주소를 앞에(몇 초 안에 끝낸다).</summary>
    private async Task<RemoteDetection> DetectAsync(GuestFileTransport transport)
    {
        owner.Cursor = Cursors.Wait;
        try
        {
            var addresses = await api.GetGuestAddressesAsync(guest.Node, guest.Kind, guest.VmId)
                .WaitAsync(AddressTimeout);
            if (addresses.Count == 0) return RemoteDetection.None;

            var ports = RemoteDetection.Services.Select(s => s.Port).ToArray();
            var probed = await Task.WhenAll(addresses.Take(MaxProbedAddresses).Select(async a =>
                (Address: a, Open: await PortProbe.OpenPortsAsync(a, ports, PortProbe.DefaultTimeout))));
            var wanted = transport == GuestFileTransport.Smb
                ? new[] { RemoteFileSettings.SmbPort }
                : [RemoteFileSettings.FtpPort, RemoteFileSettings.FtpsImplicitPort];
            var best = probed.FirstOrDefault(p => wanted.Any(p.Open.Contains));
            if (best.Address is null) best = probed[0];
            return new RemoteDetection(addresses, best.Address, best.Open);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return RemoteDetection.None; // 모르면 안내 없이 — 직접 적는다
        }
        finally
        {
            owner.Cursor = null;
        }
    }

    /// <summary>SMB·FTP 로 연다 — 비밀번호가 없으면 묻고, 로그인이 틀리면 다시 묻는다(몇 번까지).</summary>
    private async Task<IGuestFileSystem> OpenRemoteAsync(GuestFileConnection connection, CancellationToken ct)
    {
        var transport = connection.EffectiveTransport;
        if (!connection.RemoteFor(transport).IsComplete(transport))
        {
            if (!await SelectAsync(transport, true)) throw new OperationCanceledException(ct);
            connection = await LoadAsync();
        }

        var settings = connection.RemoteFor(transport);
        var target = CredentialTarget(transport);
        var secret = _remoteSecret ?? (settings.RememberPassword ? WindowsCredentialStore.Read(target) : null);
        _remoteSecret = null;
        var remember = settings.RememberPassword;
        for (var attempt = 0;; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (secret is null && settings.UserName.Length > 0)
            {
                var asked = RemoteConnectionForm.AskPassword(owner, settings, attempt > 0)
                            ?? throw new OperationCanceledException(ct);
                (secret, remember) = asked;
            }

            IGuestFileSystem? files = null;
            try
            {
                files = await OpenRemoteOnceAsync(transport, settings, secret, ct);
                await RememberSecretAsync(connection, transport, settings, secret, remember);
                return files;
            }
            catch when (files is not null)
            {
                files.Dispose(); // 연 뒤의 저장이 실패했다 — SMB 연결이 계정째 남지 않게
                throw;
            }
            catch (GuestFileException ex) when (ex is SmbAuthenticationException or FtpLoginException
                                                && settings.UserName.Length > 0 && attempt < MaxLoginAttempts - 1)
            {
                secret = null; // 틀렸다 — 다시 묻는다
            }
        }
    }

    private async Task<IGuestFileSystem> OpenRemoteOnceAsync(GuestFileTransport transport,
        RemoteFileSettings settings, string? secret, CancellationToken ct)
    {
        if (transport == GuestFileTransport.Smb)
            return await SmbFileSystem.OpenAsync(settings.Host, settings.Share, settings.UserName, secret, ct);

        var serverKey = $"[{settings.Host}]:{settings.FtpPortOrDefault}";
        var saved = (await _store.LoadAsync()).FtpCertificates.GetValueOrDefault(serverKey);
        var files = await FtpFileSystem.OpenAsync(settings.Host, settings, secret, (thumbprint, certificate) =>
            owner.Dispatcher.Invoke(() =>
            {
                lock (_trustedCertificates)
                    if (thumbprint == saved || _trustedCertificates.GetValueOrDefault(serverKey) == thumbprint)
                        return true;
                if (ct.IsCancellationRequested || !owner.IsLoaded) return false;
                if (!AskTrust(serverKey, certificate, thumbprint, saved)) return false;
                lock (_trustedCertificates) _trustedCertificates[serverKey] = thumbprint;
                return true;
            }), ct);
        string? accepted;
        lock (_trustedCertificates) accepted = _trustedCertificates.GetValueOrDefault(serverKey);
        try
        {
            if (accepted is not null && accepted != saved)
                await _store.UpdateAsync(s => s with
                {
                    FtpCertificates = new Dictionary<string, string>(s.FtpCertificates) { [serverKey] = accepted }
                }, ct);
        }
        catch
        {
            files.Dispose();
            throw;
        }

        return files;
    }

    private bool AskTrust(string server, X509Certificate2 certificate, string thumbprint, string? known)
    {
        const string day = "yyyy-MM-dd";
        var message = Loc.T(known is null ? "Remote_CertNew" : "Remote_CertChanged", server, certificate.Subject,
            certificate.Issuer, certificate.NotBefore.ToString(day), certificate.NotAfter.ToString(day), thumbprint,
            known ?? string.Empty);
        return ThemedMessageBox.Show(owner, message, Loc.T("Remote_CertTitle"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    /// <summary>로그인에 성공한 뒤에만 비밀번호를 기억하거나 지운다(틀린 비밀번호를 남기지 않게).</summary>
    private async Task RememberSecretAsync(GuestFileConnection connection, GuestFileTransport transport,
        RemoteFileSettings settings, string? secret, bool remember)
    {
        var target = CredentialTarget(transport);
        if (!remember) WindowsCredentialStore.Delete(target);
        else if (secret is not null) WindowsCredentialStore.Write(target, settings.UserName, secret);
        if (remember != settings.RememberPassword)
            await SaveAsync(connection.WithRemote(transport, settings with { RememberPassword = remember }));
    }
}
