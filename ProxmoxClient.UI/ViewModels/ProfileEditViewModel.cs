using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>서버 프로필 추가·편집.</summary>
public sealed partial class ProfileEditViewModel : ViewModelBase
{
    private readonly ConnectionProfile? _original;
    private readonly bool _hadSecret;

    public ProfileEditViewModel(ConnectionProfile? profile)
    {
        _original = profile;
        var source = profile ?? new ConnectionProfile();
        Name = source.Name;
        Host = source.Host;
        Port = source.Port.ToString();
        IsTokenAuth = source.AuthMode == AuthMode.ApiToken;
        UserName = source.UserName;
        SplitUser(source.UserName); // 모바일 화면은 사용자·영역을 따로(PC 와 같이)
        TokenId = source.ApiTokenId ?? string.Empty;
        AcceptSelfSigned = source.AcceptSelfSignedCertificate;
        _hadSecret = profile is not null && AppServices.Profiles.GetSecret(profile) is not null;
        RememberSecret = profile is null || _hadSecret;
    }

    public bool IsNew => _original is null;

    /// <summary>OS 보안 저장소가 있을 때만 "저장" 선택지를 보여 준다.</summary>
    public bool CanRememberSecret => AppServices.Profiles.CanRememberSecrets;

    public override string Title => IsNew ? L.T("Profile_AddTitle") : L.T("Profile_EditTitle");

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial string Host { get; set; } = string.Empty;

    [ObservableProperty] public partial string Port { get; set; } = "8006";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordAuth))]
    public partial bool IsTokenAuth { get; set; }

    public bool IsPasswordAuth
    {
        get => !IsTokenAuth;
        set => IsTokenAuth = !value;
    }

    [ObservableProperty] public partial string UserName { get; set; } = "root@pam";

    [ObservableProperty] public partial string Password { get; set; } = string.Empty;

    [ObservableProperty] public partial string TokenId { get; set; } = string.Empty;

    [ObservableProperty] public partial string TokenSecret { get; set; } = string.Empty;

    [ObservableProperty] public partial bool RememberSecret { get; set; }

    [ObservableProperty] public partial bool AcceptSelfSigned { get; set; } = true;

    /// <summary>저장된 비밀값이 있으면 입력란을 비워 두어도 그대로 쓴다는 안내.</summary>
    public string SecretWatermark => _hadSecret ? L.T("Profile_SecretUnchanged") : string.Empty;

    [RelayCommand]
    private async Task Save()
    {
        var host = NormalizeHost(Host);
        if (host.Length == 0)
        {
            await AppServices.Dialogs.AlertAsync(L.T("Profile_InvalidTitle"), L.T("Profile_HostRequired"));
            return;
        }

        if (!int.TryParse(Port.Trim(), out var port) || port is < 1 or > 65535)
        {
            await AppServices.Dialogs.AlertAsync(L.T("Profile_InvalidTitle"), L.T("Profile_PortInvalid"));
            return;
        }

        if (IsTokenAuth ? TokenId.Trim().Length == 0 || !TokenId.Contains('!') : UserOnly.Trim().Length == 0)
        {
            await AppServices.Dialogs.AlertAsync(L.T("Profile_InvalidTitle"),
                IsTokenAuth ? L.T("Profile_TokenIdInvalid") : L.T("Profile_UserRequired"));
            return;
        }

        if (ValidateProxy() is { } proxyProblem)
        {
            await AppServices.Dialogs.AlertAsync(L.T("Profile_InvalidTitle"), proxyProblem);
            return;
        }

        var profile = _original?.Clone() ?? new ConnectionProfile();
        // 주소가 바뀌면 이전 서버 인증서 지문은 의미가 없다 — 새 서버에서 다시 확인한다
        if (!string.Equals(profile.Host, host, StringComparison.OrdinalIgnoreCase) || profile.Port != port)
            profile.CertificateThumbprint = null;

        var authChanged = _original is not null && _original.AuthMode != (IsTokenAuth ? AuthMode.ApiToken : AuthMode.Password);
        profile.Name = Name.Trim().Length > 0 ? Name.Trim() : host;
        profile.Host = host;
        profile.Port = port;
        profile.AuthMode = IsTokenAuth ? AuthMode.ApiToken : AuthMode.Password;
        profile.UserName = NormalizeUser(ComposedUser);
        profile.ApiTokenId = IsTokenAuth ? TokenId.Trim() : profile.ApiTokenId;
        profile.AcceptSelfSignedCertificate = AcceptSelfSigned;
        ApplyTrustedThumbprint(profile);
        ApplyConnectionOptions(profile); // 인증서 지문·프록시(PC 서버 설정과 같은 칸)

        var ok = await TryAsync(async () =>
        {
            await AppServices.Profiles.SaveAsync(profile);

            var secret = IsTokenAuth ? TokenSecret : Password;
            if (!RememberSecret || !CanRememberSecret)
                AppServices.Profiles.ForgetSecret(profile);
            else if (secret.Length > 0)
                AppServices.Profiles.RememberSecret(profile, secret);
            else if (authChanged)
                AppServices.Profiles.ForgetSecret(profile); // 다른 방식의 비밀값은 쓸 수 없다
        });
        if (ok) await AppServices.Navigator.PopAsync();
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (_original is null) return;

        var name = string.IsNullOrWhiteSpace(_original.Name) ? _original.Host : _original.Name;
        if (!await AppServices.Dialogs.ConfirmAsync(L.T("Profile_DeleteTitle"), L.T("Profile_DeleteText", name),
                L.T("Common_Delete"), true))
            return;

        if (await TryAsync(() => AppServices.Profiles.DeleteAsync(_original)))
            await AppServices.Navigator.PopAsync();
    }

    /// <summary>"https://pve.example:8006/" 처럼 붙여 넣어도 호스트만 남긴다(포트가 있으면 포트 칸에 반영).</summary>
    private string NormalizeHost(string raw)
    {
        var text = raw.Trim();
        if (Uri.TryCreate(text.Contains("://") ? text : "https://" + text, UriKind.Absolute, out var uri) &&
            uri.Host.Length > 0)
        {
            if (!uri.IsDefaultPort && text.Contains(':' + uri.Port.ToString())) Port = uri.Port.ToString();
            return uri.Host;
        }

        return text;
    }

    /// <summary>영역(realm)을 빼먹으면 PVE 기본인 @pam 을 붙인다.</summary>
    private static string NormalizeUser(string raw)
    {
        var user = raw.Trim();
        return user.Length > 0 && !user.Contains('@') ? user + "@pam" : user;
    }
}
