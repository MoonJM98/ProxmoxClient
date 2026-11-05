using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Profiles;
using Avalonia.Platform.Storage;
using ProxmoxClient.Core.Vpn;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Login;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>
///     데스크톱 "서버 설정" 화면(WPF ProfileDialog · ProfileEditorViewModel) — 이름·주소·포트·인증서, 프록시, VPN.
///     기본 칸(이름·주소·포트·자체 서명 허용)은 기존 <see cref="ProfileEditViewModel" /> 의 속성을 그대로 쓴다.
///     모바일 편집 화면은 이 부분을 쓰지 않는다(<see cref="LoadDesktopState" /> 를 부르지 않으면 아무 일도 없다).
/// </summary>
public sealed partial class ProfileEditViewModel
{
    /// <summary>데스크톱 상태를 채운 뒤에만 true — 모바일 편집 화면의 속성 변경에는 아무 일도 하지 않는다.</summary>
    private bool _desktopReady;

    /// <summary>지문을 확인한 시점의 주소 — 주소가 바뀌면 다른 서버일 수 있으므로 저장 시 지문을 버린다.</summary>
    private string _thumbprintHost = string.Empty;
    private int _thumbprintPort;

    /// <summary>화면을 닫았을 때(true = 저장, false = 취소). 목록 화면이 다시 읽는 데 쓴다.</summary>
    public Action<bool>? Finished { get; set; }

    /// <summary>머리글 — WPF 창 제목(추가 / 편집).</summary>
    public string DialogTitle => IsNew ? L.T("ProfileDialog_M01") : L.T("ProfileDialog_M02");

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CertificateStatus))]
    public partial string? CertificateThumbprint { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProxyNone), nameof(IsProxySystem), nameof(IsProxyHttp), nameof(IsProxyHttps),
        nameof(IsProxySocks5), nameof(IsProxyDetailVisible))]
    public partial ProxyMode ProxyMode { get; set; } = ProxyMode.None;

    public bool IsProxyNone
    {
        get => ProxyMode == ProxyMode.None;
        set
        {
            if (value) ProxyMode = ProxyMode.None;
        }
    }

    public bool IsProxySystem
    {
        get => ProxyMode == ProxyMode.System;
        set
        {
            if (value) ProxyMode = ProxyMode.System;
        }
    }

    public bool IsProxyHttp
    {
        get => ProxyMode == ProxyMode.Http;
        set
        {
            if (value) ProxyMode = ProxyMode.Http;
        }
    }

    public bool IsProxyHttps
    {
        get => ProxyMode == ProxyMode.Https;
        set
        {
            if (value) ProxyMode = ProxyMode.Https;
        }
    }

    public bool IsProxySocks5
    {
        get => ProxyMode == ProxyMode.Socks5;
        set
        {
            if (value) ProxyMode = ProxyMode.Socks5;
        }
    }

    /// <summary>프록시 주소·포트 칸은 직접 연결·시스템 설정일 때 숨긴다(WPF).</summary>
    public bool IsProxyDetailVisible => ProxyMode is not (ProxyMode.None or ProxyMode.System);

    [ObservableProperty] public partial string ProxyHost { get; set; } = string.Empty;

    [ObservableProperty] public partial string ProxyPortText { get; set; } = "1080";

    [ObservableProperty] public partial string ProxyUserName { get; set; } = string.Empty;

    [ObservableProperty] public partial string ProxyPassword { get; set; } = string.Empty;

    /// <summary>VPN 은 이 화면에서 아직 연결하지 않으므로 칸은 보이기만 하고 바꿀 수 없다(저장된 값은 그대로 보존).</summary>
    [ObservableProperty] public partial bool UseVpn { get; set; }

    [ObservableProperty] public partial string VpnConfigPath { get; set; } = string.Empty;

    [ObservableProperty] public partial string VpnExePath { get; set; } = string.Empty;

    [ObservableProperty] public partial string DetectedVpnExe { get; set; } = string.Empty;

    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanVerifyCertificate))]
    public partial bool IsVerifying { get; set; }

    /// <summary>인증서 확인 단추 — 자체 서명 허용일 때만, 확인 중에는 잠근다(WPF BtnVerifyCertificate).</summary>
    public bool CanVerifyCertificate => AcceptSelfSigned && !IsVerifying;

    /// <summary>인증서 신뢰 상태 안내문(WPF ProfileEditorViewModel.CertificateStatus).</summary>
    public string CertificateStatus
    {
        get
        {
            if (!AcceptSelfSigned) return L.T("ProfileEditor_CertCaOnly");

            if (CertificateThumbprint is not { Length: > 0 } thumbprint) return L.T("ProfileEditor_CertNoTrusted");

            var formatted = ServerCertificateValidator.FormatThumbprint(thumbprint);
            return ThumbprintMatchesAddress
                ? L.T("ProfileEditor_CertThumbprint", formatted)
                : L.T("ProfileEditor_CertThumbprintStale", _thumbprintHost, _thumbprintPort);
        }
    }

    /// <summary>현재 주소·포트 기준으로 지문이 유효한지.</summary>
    private bool ThumbprintMatchesAddress =>
        string.Equals(_thumbprintHost, Host.Trim(), StringComparison.OrdinalIgnoreCase)
        && int.TryParse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
        && _thumbprintPort == port;

    /// <summary>
    ///     화면이 이 뷰모델을 받았을 때 한 번 부른다 — 원본 프로필에서 데스크톱 칸을 채운다.
    ///     (생성자는 기존 모바일 코드 그대로 두려고 여기서 채운다.)
    /// </summary>
    public void LoadDesktopState()
    {
        var source = _original;
        _thumbprintHost = source?.Host ?? string.Empty;
        _thumbprintPort = source?.Port ?? 0;
        CertificateThumbprint = source?.CertificateThumbprint;
        ProxyMode = source?.ProxyMode ?? ProxyMode.None;
        ProxyHost = source?.ProxyHost ?? string.Empty;
        ProxyPortText = source?.ProxyPort?.ToString(CultureInfo.InvariantCulture) ?? "1080";
        ProxyUserName = source?.ProxyUserName ?? string.Empty;
        ProxyPassword = source?.ProxyPassword ?? string.Empty;
        UseVpn = source?.UseVpn ?? false;
        VpnConfigPath = source?.VpnConfigPath ?? string.Empty;
        VpnExePath = source?.VpnExePath ?? string.Empty;
        DetectedVpnExe = DetectVpnExe();
        StatusText = string.Empty;
        _desktopReady = true;
    }

    /// <summary>자체 서명 허용 + 지문 없음 + 주소 입력됨일 때만 자동 확인(WPF TryAutoVerifyCertificate).</summary>
    public void TryAutoVerifyCertificate()
    {
        if (_desktopReady
            && !IsVerifying
            && AcceptSelfSigned
            && string.IsNullOrEmpty(CertificateThumbprint)
            && Host.Trim().Length > 0)
            _ = VerifyCertificateAsync(true);
    }

    partial void OnAcceptSelfSignedChanged(bool value)
    {
        OnPropertyChanged(nameof(CertificateStatus));
        if (_desktopReady) TryAutoVerifyCertificate();
    }

    partial void OnHostChanged(string value)
    {
        OnPropertyChanged(nameof(CertificateStatus));
    }

    partial void OnPortChanged(string value)
    {
        if (!_desktopReady) return;

        // WPF 의 정수 입력 칸처럼 숫자 말고는 받지 않는다
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits != value)
        {
            Port = digits;
            return;
        }

        OnPropertyChanged(nameof(CertificateStatus));
    }

    partial void OnProxyPortTextChanged(string value)
    {
        if (!_desktopReady) return;

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits != value) ProxyPortText = digits;
    }

    [RelayCommand]
    private Task VerifyCertificate() => VerifyCertificateAsync(false);

    /// <summary>
    ///     입력한 주소로 접속해 서버 인증서를 확인한다 — 처음이면 지문을 신뢰할지 묻고, 바뀌었으면 이전·새 지문을 비교해 보여 준다.
    ///     확인한 지문은 저장할 때 프로필에 기록된다(WPF VerifyCertificateAsync).
    /// </summary>
    private async Task VerifyCertificateAsync(bool automatic)
    {
        var host = Host.Trim();
        if (host.Length == 0
            || !int.TryParse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            // 주소가 아직 올바르지 않으면 자동 확인은 경고 없이 넘어간다
            if (!automatic) await AppServices.Dialogs.AlertAsync(L.T("ProfileDialog_M08"), L.T("ProfileDialog_M07"));
            return;
        }

        IsVerifying = true;
        StatusText = L.T("ProfileDialog_M09");
        try
        {
            // 현재 신뢰 중인 지문을 함께 넘겨, 바뀐 경우 이전 지문과 비교해 경고가 뜨게 한다
            var probe = new ConnectionProfile
            {
                Host = host,
                Port = port,
                AcceptSelfSignedCertificate = true,
                CertificateThumbprint = CertificateThumbprint,
                ProxyMode = ProxyMode,
                ProxyHost = ProxyHost,
                ProxyPort = int.TryParse(ProxyPortText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var proxyPort)
                    ? proxyPort
                    : null,
                ProxyUserName = ProxyUserName,
                ProxyPassword = ProxyPassword
            };

            using var client = new ProxmoxApiClient(probe);
            await LoginTrust.RunAsync(
                () => ProbeServerAsync(client),
                rejection => LoginTrust.ConfirmAsync(rejection, probe));

            ApplyVerifiedThumbprint(probe.CertificateThumbprint, host, port);
            StatusText = probe.CertificateThumbprint is null
                ? L.T("ProfileDialog_M10")
                : L.T("ProfileDialog_M11");
        }
        catch (CertificateTrustException)
        {
            StatusText = L.T("ProfileDialog_M12");
        }
        catch (Exception ex) when (ex is ProxmoxApiException or IOException or OperationCanceledException)
        {
            StatusText = L.T("ProfileDialog_M13", ex.Message);
        }
        finally
        {
            IsVerifying = false;
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

    private void ApplyVerifiedThumbprint(string? thumbprint, string host, int port)
    {
        _thumbprintHost = host;
        _thumbprintPort = port;
        CertificateThumbprint = thumbprint;
        OnPropertyChanged(nameof(CertificateStatus));
    }

    /// <summary>
    ///     모바일 저장 — PC 서버 설정과 같은 칸(신뢰한 인증서 지문·프록시)을 프로필에 옮긴다. 화면이 이 상태를
    ///     읽어 들였을 때만(<see cref="LoadDesktopState" />).
    /// </summary>
    private void ApplyConnectionOptions(ConnectionProfile profile)
    {
        if (!_desktopReady) return;

        if (ThumbprintMatchesAddress && CertificateThumbprint is { Length: > 0 } thumbprint)
            profile.CertificateThumbprint = thumbprint;
        var custom = ProxyMode is not (ProxyMode.None or ProxyMode.System);
        profile.ProxyMode = ProxyMode;
        profile.ProxyHost = custom ? ProxyHost.Trim() : null;
        profile.ProxyPort = custom && int.TryParse(ProxyPortText, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var proxyPort)
            ? proxyPort
            : null;
        profile.ProxyUserName = custom && !string.IsNullOrWhiteSpace(ProxyUserName) ? ProxyUserName.Trim() : null;
        profile.ProxyPassword = custom && !string.IsNullOrEmpty(ProxyPassword) ? ProxyPassword : null;
    }

    /// <summary>모바일 저장 전 — 프록시 칸 검사(PC 와 같은 문구). 문제가 없으면 null.</summary>
    private string? ValidateProxy()
    {
        if (!_desktopReady || ProxyMode is ProxyMode.None or ProxyMode.System) return null;
        if (string.IsNullOrWhiteSpace(ProxyHost)) return L.T("ProfileEditor_ProxyHostRequired");

        return int.TryParse(ProxyPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) &&
               port is >= 1 and <= 65535
            ? null
            : L.T("ProfileEditor_ProxyPortRange");
    }

    [RelayCommand]
    private async Task SaveSettings()
    {
        var problem = ValidateDesktop();
        if (problem is not null)
        {
            await AppServices.Dialogs.AlertAsync(L.T("ProfileDialog_M08"), problem);
            return;
        }

        var profile = BuildDesktopProfile();
        if (!await TryAsync(() => AppServices.Profiles.SaveAsync(profile))) return;

        await AppServices.Navigator.PopAsync();
        Finished?.Invoke(true);
    }

    [RelayCommand]
    private async Task CancelSettings()
    {
        await AppServices.Navigator.PopAsync();
        Finished?.Invoke(false);
    }

    /// <summary>WPF ProfileEditorViewModel.Validate 와 같은 규칙·같은 문구.</summary>
    private string? ValidateDesktop()
    {
        if (string.IsNullOrWhiteSpace(Name)) return L.T("ProfileEditor_NameRequired");

        if (string.IsNullOrWhiteSpace(Host)) return L.T("ProfileRegisterWindow_M01");

        if (!int.TryParse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
            return L.T("ProfileEditor_PortRange");

        if (ProxyMode is not (ProxyMode.None or ProxyMode.System))
        {
            if (string.IsNullOrWhiteSpace(ProxyHost)) return L.T("ProfileEditor_ProxyHostRequired");

            if (!int.TryParse(ProxyPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var proxyPort) ||
                proxyPort is < 1 or > 65535)
                return L.T("ProfileEditor_ProxyPortRange");
        }

        if (UseVpn && string.IsNullOrWhiteSpace(VpnConfigPath)) return L.T("ProfileEditor_VpnConfigRequired");

        return null;
    }

    /// <summary>WPF ProfileEditorViewModel.ToProfile 와 같다 — 원본을 복제해 화면에 없는 값(인증서 지문 등)을 보존한다.</summary>
    private ConnectionProfile BuildDesktopProfile()
    {
        var custom = ProxyMode is not (ProxyMode.None or ProxyMode.System);
        int? proxyPort = custom && int.TryParse(ProxyPortText, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;

        var profile = _original?.Clone() ?? new ConnectionProfile();
        var host = Host.Trim();
        profile.Name = Name.Trim();
        profile.Host = host;
        profile.Port = int.Parse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture);
        profile.AcceptSelfSignedCertificate = AcceptSelfSigned;
        // 주소·포트가 바뀌면 다른 서버일 수 있으므로 신뢰한 지문을 버린다(다음 연결 때 다시 확인)
        profile.CertificateThumbprint = ThumbprintMatchesAddress ? CertificateThumbprint : null;
        profile.AuthMode = AuthMode.Password; // 계정/토큰은 편집 대상 아님 — 로그인 화면에서만
        profile.UserName = UserName.Trim();
        profile.Password = null;
        profile.ApiTokenId = null;
        profile.ApiTokenSecret = null;
        profile.ProxyMode = ProxyMode;
        profile.ProxyHost = custom ? ProxyHost.Trim() : null;
        profile.ProxyPort = proxyPort;
        profile.ProxyUserName = custom && !string.IsNullOrWhiteSpace(ProxyUserName) ? ProxyUserName.Trim() : null;
        profile.ProxyPassword = custom && !string.IsNullOrEmpty(ProxyPassword) ? ProxyPassword : null;
        profile.UseVpn = UseVpn;
        profile.VpnConfigPath = UseVpn ? VpnConfigPath.Trim() : null;
        profile.VpnExePath = string.IsNullOrWhiteSpace(VpnExePath) ? null : VpnExePath.Trim();
        return profile;
    }

    /// <summary>이 기기가 OpenVPN 터널을 열 수 있는가(데스크톱) — 아니면 VPN 칸을 끈다.</summary>
    public bool VpnSupported => VpnTunnel.IsSupported;

    /// <summary>실행 파일 칸 이름 — Windows 는 WPF 와 같은 openvpn.exe.</summary>
    public string VpnExeLabel => OperatingSystem.IsWindows() ? "openvpn.exe" : L.T("Vpn_ExeLabel");

    [RelayCommand]
    private async Task BrowseVpnConfigAsync()
    {
        if (await PickFileAsync(L.T("Vpn_PickConfig"), "OpenVPN", ["*.ovpn", "*.conf"]) is { } path) VpnConfigPath = path;
    }

    [RelayCommand]
    private async Task BrowseVpnExeAsync()
    {
        if (await PickFileAsync(L.T("Vpn_PickExe"), "openvpn", OperatingSystem.IsWindows() ? ["*.exe"] : ["*"]) is { } path)
            VpnExePath = path;
    }

    private static async Task<string?> PickFileAsync(string title, string kind, string[] patterns)
    {
        if (Views.ClipboardHelper.TopLevel?.StorageProvider is not { CanOpen: true } provider) return null;

        var files = await provider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType(kind) { Patterns = patterns }]
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private static string DetectVpnExe()
    {
        try
        {
            return OpenVpnManager.DetectOpenVpnExe() ?? L.T("ProfileEditor_VpnAutoDetectFailed");
        }
        catch (Exception)
        {
            return L.T("ProfileEditor_VpnAutoDetectFailed");
        }
    }
}
