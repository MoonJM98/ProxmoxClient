using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.App.Views;

public partial class ProfileRegisterWindow : Window
{
    private bool _busy;
    private string _verifiedHost = string.Empty;
    private int _verifiedPort;
    private bool _verifiedSelfSigned;
    private string? _verifiedThumbprint;

    public ProfileRegisterWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
    }

    /// <summary>등록 성공 시 만들어진 프로필(서버 정보만 포함, 자격 증명 없음).</summary>
    public ConnectionProfile? ResultProfile { get; private set; }

    private async void OnVerify(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            SetStatus(Loc.T("ProfileRegisterWindow_M01"));
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            SetStatus(Loc.T("ProfileRegisterWindow_M02"));
            return;
        }

        _busy = true;
        BtnVerify.IsEnabled = false;
        BtnRegister.IsEnabled = false;
        SetStatus(Loc.T("ProfileRegisterWindow_M03"));

        var probe = new ConnectionProfile
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? host : NameBox.Text.Trim(),
            Host = host,
            Port = port,
            AcceptSelfSignedCertificate = SelfSignedCheck.IsChecked == true,
            AuthMode = AuthMode.Password
        };

        ProxmoxApiClient? client = null;
        try
        {
            var api = new ProxmoxApiClient(probe);
            client = api;
            IReadOnlyList<PveAuthDomain> domains = [];
            // 첫 연결 — 자체 서명 인증서면 지문을 확인받고, 신뢰한 지문을 등록할 프로필에 담는다
            await CertificateTrust.RunAsync(
                async () => domains = await api.GetAuthDomainsAsync(),
                rejection => CertificateTrust.Confirm(this, rejection, probe));

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
                : Loc.T("ProfileRegister_AuthUnknown");

            _verifiedHost = host;
            _verifiedPort = port;
            _verifiedSelfSigned = probe.AcceptSelfSignedCertificate;
            _verifiedThumbprint = probe.CertificateThumbprint;

            ServerInfoText.Text = $"{versionLabel} · {host}:{port}";
            AuthTypesText.Text = Loc.T("ProfileRegister_AuthTypes", authTypes);
            ServerInfoPanel.Visibility = Visibility.Visible;
            BtnRegister.IsEnabled = true;
            SetStatus(Loc.T("ProfileRegisterWindow_M04"));
        }
        catch (Exception ex)
        {
            ServerInfoPanel.Visibility = Visibility.Collapsed;
            BtnRegister.IsEnabled = false;
            SetStatus(Loc.T("ProfileRegisterWindow_M05", ex.Message));
        }
        finally
        {
            client?.Dispose();
            _busy = false;
            BtnVerify.IsEnabled = true;
        }
    }

    private void OnRegister(object sender, RoutedEventArgs e)
    {
        if (_busy || _verifiedHost.Length == 0) return;

        ResultProfile = new ConnectionProfile
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? _verifiedHost : NameBox.Text.Trim(),
            Host = _verifiedHost,
            Port = _verifiedPort,
            AcceptSelfSignedCertificate = _verifiedSelfSigned,
            CertificateThumbprint = _verifiedThumbprint,
            AuthMode = AuthMode.Password,
            UserName = "root@pam"
        };
        DialogResult = true;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}