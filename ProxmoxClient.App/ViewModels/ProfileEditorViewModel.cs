using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Core.Vpn;

namespace ProxmoxClient.App.ViewModels;

public partial class ProfileEditorViewModel : ObservableObject
{
    private readonly Guid _id;

    /// <summary>편집 전 원본 — 편집 화면에 없는 값(신뢰한 인증서 지문, 복호화 못 한 암호화 값)을 보존하는 데 쓴다.</summary>
    private readonly ConnectionProfile? _source;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CertificateStatus))]
    private bool _acceptSelfSigned = true;

    [ObservableProperty] private string _apiTokenId = string.Empty;
    [ObservableProperty] private string _apiTokenSecret = string.Empty;

    [ObservableProperty] private AuthMode _authMode = AuthMode.Password;

    /// <summary>신뢰한 서버 인증서 지문(편집 화면에서 확인·갱신 가능).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CertificateStatus))]
    private string? _certificateThumbprint;

    [ObservableProperty] private string _detectedVpnExe = string.Empty;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CertificateStatus))]
    private string _host = string.Empty;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _password = string.Empty;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CertificateStatus))]
    private string _portText = "8006";

    [ObservableProperty] private string _proxyHost = string.Empty;

    [ObservableProperty] private ProxyMode _proxyMode = ProxyMode.None;
    [ObservableProperty] private string _proxyPassword = string.Empty;
    [ObservableProperty] private string _proxyPortText = "1080";
    [ObservableProperty] private string _proxyUserName = string.Empty;

    // 지문을 확인한 시점의 주소 — 주소가 바뀌면 다른 서버일 수 있으므로 저장 시 지문을 버린다
    private string _thumbprintHost = string.Empty;
    private int _thumbprintPort;

    [ObservableProperty] private bool _useVpn;
    [ObservableProperty] private string _userName = "root@pam";
    [ObservableProperty] private string _vpnConfigPath = string.Empty;
    [ObservableProperty] private string _vpnExePath = string.Empty;

    public ProfileEditorViewModel(ConnectionProfile? source)
    {
        _id = source?.Id ?? Guid.NewGuid();
        _source = source;
        if (source is null)
        {
            _detectedVpnExe = OpenVpnManager.DetectOpenVpnExe() ?? Loc.T("ProfileEditor_VpnAutoDetectFailed");
            return;
        }

        Name = source.Name;
        Host = source.Host;
        PortText = source.Port.ToString(CultureInfo.InvariantCulture);
        AcceptSelfSigned = source.AcceptSelfSignedCertificate;
        CertificateThumbprint = source.CertificateThumbprint;
        _thumbprintHost = source.Host;
        _thumbprintPort = source.Port;
        AuthMode = source.AuthMode;
        UserName = source.UserName;
        Password = SecureStringHelper.ToPlainString(source.Password) ?? string.Empty;
        ApiTokenId = source.ApiTokenId ?? string.Empty;
        ApiTokenSecret = SecureStringHelper.ToPlainString(source.ApiTokenSecret) ?? string.Empty;
        ProxyMode = source.ProxyMode;
        ProxyHost = source.ProxyHost ?? string.Empty;
        ProxyPortText = source.ProxyPort?.ToString(CultureInfo.InvariantCulture) ?? "1080";
        ProxyUserName = source.ProxyUserName ?? string.Empty;
        ProxyPassword = source.ProxyPassword ?? string.Empty;
        UseVpn = source.UseVpn;
        VpnConfigPath = source.VpnConfigPath ?? string.Empty;
        VpnExePath = source.VpnExePath ?? string.Empty;
        _detectedVpnExe = OpenVpnManager.DetectOpenVpnExe() ?? Loc.T("ProfileEditor_VpnAutoDetectFailed");
    }

    /// <summary>현재 주소 기준으로 지문이 유효한지 — 주소·포트가 바뀌면 저장 시 버린다.</summary>
    private bool ThumbprintMatchesAddress =>
        string.Equals(_thumbprintHost, Host.Trim(), StringComparison.OrdinalIgnoreCase)
        && int.TryParse(PortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
        && _thumbprintPort == port;

    /// <summary>인증서 신뢰 상태 안내문.</summary>
    public string CertificateStatus
    {
        get
        {
            if (!AcceptSelfSigned) return Loc.T("ProfileEditor_CertCaOnly");

            if (CertificateThumbprint is not { Length: > 0 } thumbprint) return Loc.T("ProfileEditor_CertNoTrusted");

            var formatted = ServerCertificateValidator.FormatThumbprint(thumbprint);
            return ThumbprintMatchesAddress
                ? Loc.T("ProfileEditor_CertThumbprint", formatted)
                : Loc.T("ProfileEditor_CertThumbprintStale", _thumbprintHost, _thumbprintPort);
        }
    }

    /// <summary>편집 화면에서 서버 인증서를 확인·갱신한 결과 반영.</summary>
    public void SetVerifiedThumbprint(string? thumbprint, string host, int port)
    {
        _thumbprintHost = host;
        _thumbprintPort = port;
        CertificateThumbprint = thumbprint;
        OnPropertyChanged(nameof(CertificateStatus));
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return Loc.T("ProfileEditor_NameRequired");

        if (string.IsNullOrWhiteSpace(Host)) return Loc.T("ProfileRegisterWindow_M01");

        if (!int.TryParse(PortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
            return Loc.T("ProfileEditor_PortRange");

        if (ProxyMode is not (ProxyMode.None or ProxyMode.System))
        {
            if (string.IsNullOrWhiteSpace(ProxyHost)) return Loc.T("ProfileEditor_ProxyHostRequired");

            if (!int.TryParse(ProxyPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var proxyPort) ||
                proxyPort is < 1 or > 65535)
                return Loc.T("ProfileEditor_ProxyPortRange");
        }

        if (UseVpn && string.IsNullOrWhiteSpace(VpnConfigPath)) return Loc.T("ProfileEditor_VpnConfigRequired");

        return null;
    }

    public ConnectionProfile ToProfile()
    {
        var port = int.Parse(PortText, NumberStyles.Integer, CultureInfo.InvariantCulture);
        int? proxyPort = ProxyMode is ProxyMode.None or ProxyMode.System
            ? null
            : int.Parse(ProxyPortText, NumberStyles.Integer, CultureInfo.InvariantCulture);

        // 원본 복제에서 시작 — 편집 화면에 없는 값(신뢰한 인증서 지문 등)이 저장 시 사라지지 않도록
        var profile = _source?.Clone() ?? new ConnectionProfile();
        var host = Host.Trim();
        profile.Id = _id;
        profile.Name = Name.Trim();
        profile.Host = host;
        profile.Port = port;
        profile.AcceptSelfSignedCertificate = AcceptSelfSigned;
        // 주소·포트가 바뀌면 다른 서버일 수 있으므로 신뢰한 지문을 버린다(다음 연결 때 다시 확인)
        profile.CertificateThumbprint = ThumbprintMatchesAddress ? CertificateThumbprint : null;
        profile.AuthMode = AuthMode.Password; // 계정/토큰은 편집 대상 아님 — 로그인 화면에서만
        profile.UserName = UserName.Trim();
        profile.Password = null;
        profile.ApiTokenId = null;
        profile.ApiTokenSecret = null;
        profile.ProxyMode = ProxyMode;
        profile.ProxyHost = ProxyMode is ProxyMode.None or ProxyMode.System ? null : ProxyHost.Trim();
        profile.ProxyPort = proxyPort;
        profile.ProxyUserName =
            ProxyMode is ProxyMode.None or ProxyMode.System || string.IsNullOrWhiteSpace(ProxyUserName)
                ? null
                : ProxyUserName.Trim();
        profile.ProxyPassword = ProxyMode is ProxyMode.None or ProxyMode.System || string.IsNullOrEmpty(ProxyPassword)
            ? null
            : ProxyPassword;
        profile.UseVpn = UseVpn;
        profile.VpnConfigPath = UseVpn ? VpnConfigPath.Trim() : null;
        profile.VpnExePath = string.IsNullOrWhiteSpace(VpnExePath) ? null : VpnExePath.Trim();
        return profile;
    }
}