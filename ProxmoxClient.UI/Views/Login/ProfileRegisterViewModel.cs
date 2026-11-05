using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views.Login;

/// <summary>
///     "새 서버 등록"(WPF ProfileRegisterWindow) — 주소를 확인한 뒤에만 등록할 수 있다.
///     아이디·비밀번호는 받지 않는다(로그인 화면에서 입력).
/// </summary>
public sealed partial class ProfileRegisterViewModel(Action<ConnectionProfile>? registered) : ViewModelBase
{
    private VerifiedServer? _verified;

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial string Host { get; set; } = string.Empty;

    [ObservableProperty] public partial string Port { get; set; } = "8006";

    [ObservableProperty] public partial bool SelfSigned { get; set; } = true;

    [ObservableProperty] public partial bool IsVerifying { get; set; }

    [ObservableProperty] public partial bool ServerInfoVisible { get; set; }

    [ObservableProperty] public partial string ServerInfo { get; set; } = string.Empty;

    [ObservableProperty] public partial string AuthTypes { get; set; } = string.Empty;

    /// <summary>등록 단추 — 확인이 끝난 뒤에만 켠다(WPF BtnRegister.IsEnabled).</summary>
    [ObservableProperty] public partial bool CanRegister { get; set; }

    [ObservableProperty] public partial string Status { get; set; } = string.Empty;

    partial void OnPortChanged(string value)
    {
        // WPF 의 정수 입력 칸처럼 숫자만 받는다
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits != value) Port = digits;
    }

    [RelayCommand]
    private async Task Verify()
    {
        if (IsVerifying) return;

        var host = Host.Trim();
        if (host.Length == 0)
        {
            Status = L.T("ProfileRegisterWindow_M01");
            return;
        }

        if (!int.TryParse(Port.Trim(), out var port) || port is < 1 or > 65535)
        {
            Status = L.T("ProfileRegisterWindow_M02");
            return;
        }

        IsVerifying = true;
        CanRegister = false;
        Status = L.T("ProfileRegisterWindow_M03");

        var probe = new ConnectionProfile
        {
            Name = string.IsNullOrWhiteSpace(Name) ? host : Name.Trim(),
            Host = host,
            Port = port,
            AcceptSelfSignedCertificate = SelfSigned,
            AuthMode = AuthMode.Password
        };

        ProxmoxApiClient? client = null;
        try
        {
            var api = new ProxmoxApiClient(probe);
            client = api;
            IReadOnlyList<PveAuthDomain> domains = [];
            // 첫 연결 — 자체 서명 인증서면 지문을 확인받고, 신뢰한 지문을 등록할 프로필에 담는다
            await LoginTrust.RunAsync(
                async () => domains = await api.GetAuthDomainsAsync(),
                rejection => LoginTrust.ConfirmAsync(rejection, probe));

            string versionLabel;
            try
            {
                var version = await client.GetVersionAsync();
                versionLabel = $"Proxmox VE {version.Version}";
            }
            catch (ProxmoxApiException)
            {
                versionLabel = "Proxmox VE"; // /version이 인증을 요구하는 서버가 있음
            }

            var authTypes = domains.Count > 0
                ? string.Join(", ", domains
                    .OrderByDescending(d => d.IsDefault)
                    .ThenBy(d => d.Realm, StringComparer.OrdinalIgnoreCase)
                    .Select(d => d.DisplayLabel))
                : L.T("ProfileRegister_AuthUnknown");

            _verified = new VerifiedServer(host, port, probe.AcceptSelfSignedCertificate,
                probe.CertificateThumbprint);

            ServerInfo = $"{versionLabel} · {host}:{port}";
            AuthTypes = L.T("ProfileRegister_AuthTypes", authTypes);
            ServerInfoVisible = true;
            CanRegister = true;
            Status = L.T("ProfileRegisterWindow_M04");
        }
        catch (Exception ex)
        {
            ServerInfoVisible = false;
            CanRegister = false;
            Status = L.T("ProfileRegisterWindow_M05", ex.Message);
        }
        finally
        {
            client?.Dispose();
            IsVerifying = false;
        }
    }

    [RelayCommand]
    private async Task Register()
    {
        if (IsVerifying || _verified is not { } verified) return;

        var profile = new ConnectionProfile
        {
            Name = string.IsNullOrWhiteSpace(Name) ? verified.Host : Name.Trim(),
            Host = verified.Host,
            Port = verified.Port,
            AcceptSelfSignedCertificate = verified.SelfSigned,
            CertificateThumbprint = verified.Thumbprint,
            AuthMode = AuthMode.Password,
            UserName = "root@pam"
        };

        if (!await TryAsync(() => AppServices.Profiles.SaveAsync(profile))) return;

        registered?.Invoke(profile);
        await AppServices.Navigator.PopAsync();
    }

    [RelayCommand]
    private Task Close() => AppServices.Navigator.PopAsync();

    private sealed record VerifiedServer(string Host, int Port, bool SelfSigned, string? Thumbprint);
}
