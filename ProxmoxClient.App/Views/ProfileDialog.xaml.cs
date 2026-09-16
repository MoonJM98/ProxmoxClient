using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.App.ViewModels;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.App.Views;

public partial class ProfileDialog : Window
{
    private readonly ProfileEditorViewModel _editor;

    /// <summary>인증서 확인 진행 중 — 자동 실행과 버튼이 겹치지 않도록.</summary>
    private bool _verifying;

    public ProfileDialog(ConnectionProfile? existing)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _editor = new ProfileEditorViewModel(existing);
        DataContext = _editor;
        ProxyPasswordBox.Password = _editor.ProxyPassword;
        Title = existing is null ? Loc.T("ProfileDialog_M01") : Loc.T("ProfileDialog_M02");

        // 신뢰한 지문이 없으면(자체 서명 허용 중) 버튼을 누르지 않아도 같은 확인 절차를 자동으로 진행
        Loaded += (_, _) => TryAutoVerifyCertificate();
        _editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileEditorViewModel.AcceptSelfSigned)) TryAutoVerifyCertificate();
        };
    }

    public ConnectionProfile? ResultProfile { get; private set; }

    /// <summary>자체 서명 허용 + 지문 없음 + 주소 입력됨일 때만 자동 확인(새 프로필에서 주소가 비면 건너뜀).</summary>
    private void TryAutoVerifyCertificate()
    {
        if (!_verifying
            && _editor.AcceptSelfSigned
            && string.IsNullOrEmpty(_editor.CertificateThumbprint)
            && _editor.Host.Trim().Length > 0)
            _ = VerifyCertificateAsync(true);
    }

    private void OnBrowseVpnConfig(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("ProfileDialog_M03"),
            Filter = Loc.T("ProfileDialog_M04")
        };
        if (dialog.ShowDialog(this) == true)
        {
            _editor.VpnConfigPath = dialog.FileName;
            if (!_editor.UseVpn) _editor.UseVpn = true;
        }
    }

    private void OnBrowseVpnExe(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("ProfileDialog_M05"),
            Filter = Loc.T("ProfileDialog_M06")
        };
        if (dialog.ShowDialog(this) == true) _editor.VpnExePath = dialog.FileName;
    }

    /// <summary>
    ///     입력한 주소로 접속해 서버 인증서를 확인한다 — 처음이면 지문을 신뢰할지 묻고, 바뀌었으면 이전·새 지문을 비교해 보여 준다.
    ///     확인한 지문은 저장할 때 프로필에 기록된다(연결 전에 미리 갱신할 수 있다).
    /// </summary>
    private async void OnVerifyCertificate(object sender, RoutedEventArgs e)
    {
        await VerifyCertificateAsync(false);
    }

    /// <param name="automatic">
    ///     지문이 비어 자동으로 실행된 경우. 주소가 아직 올바르지 않으면 경고창 없이 조용히 넘어간다.
    /// </param>
    private async Task VerifyCertificateAsync(bool automatic)
    {
        var host = _editor.Host.Trim();
        if (host.Length == 0
            || !int.TryParse(_editor.PortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            if (!automatic)
                ThemedMessageBox.Show(this, Loc.T("ProfileDialog_M07"), Loc.T("ProfileDialog_M08"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        _verifying = true;
        BtnVerifyCertificate.IsEnabled = false;
        StatusText.Text = Loc.T("ProfileDialog_M09");
        try
        {
            // 현재 신뢰 중인 지문을 함께 넘겨, 바뀐 경우 이전 지문과 비교해 경고가 뜨게 한다
            var probe = new ConnectionProfile
            {
                Host = host,
                Port = port,
                AcceptSelfSignedCertificate = true,
                CertificateThumbprint = _editor.CertificateThumbprint,
                ProxyMode = _editor.ProxyMode,
                ProxyHost = _editor.ProxyHost,
                ProxyPort = int.TryParse(_editor.ProxyPortText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var proxyPort)
                    ? proxyPort
                    : null,
                ProxyUserName = _editor.ProxyUserName,
                ProxyPassword = ProxyPasswordBox.Password
            };

            using var client = new ProxmoxApiClient(probe);
            await CertificateTrust.RunAsync(
                () => ProbeServerAsync(client),
                rejection => CertificateTrust.Confirm(this, rejection, probe));

            _editor.SetVerifiedThumbprint(probe.CertificateThumbprint, host, port);
            StatusText.Text = probe.CertificateThumbprint is null
                ? Loc.T("ProfileDialog_M10")
                : Loc.T("ProfileDialog_M11");
        }
        catch (CertificateTrustException)
        {
            StatusText.Text = Loc.T("ProfileDialog_M12");
        }
        catch (Exception ex) when (ex is ProxmoxApiException or IOException or OperationCanceledException)
        {
            StatusText.Text = Loc.T("ProfileDialog_M13", ex.Message);
        }
        finally
        {
            _verifying = false;
            BtnVerifyCertificate.IsEnabled = _editor.AcceptSelfSigned;
        }
    }

    /// <summary>TLS 핸드셰이크만 확인하면 되므로, 서버가 응답(권한 오류 포함)하면 성공으로 본다.</summary>
    private static async Task ProbeServerAsync(ProxmoxApiClient client)
    {
        try
        {
            await client.GetVersionAsync();
        }
        catch (ProxmoxApiException ex) when (ex.StatusCode != 0)
        {
            // 서버가 HTTP 응답을 돌려줬다 = 인증서 검증 통과(인증이 필요한 서버도 여기로 온다)
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _editor.ProxyPassword = ProxyPasswordBox.Password;

        var error = _editor.Validate();
        if (error is not null)
        {
            ThemedMessageBox.Show(this, error, Loc.T("ProfileDialog_M08"), MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ResultProfile = _editor.ToProfile();
        DialogResult = true;
    }
}