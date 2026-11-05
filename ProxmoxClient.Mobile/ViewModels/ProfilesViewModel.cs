using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Mobile.Localization;
using ProxmoxClient.Mobile.Services;

namespace ProxmoxClient.Mobile.ViewModels;

/// <summary>첫 화면 — 저장한 서버 목록. 누르면 로그인해 클러스터 화면으로 간다.</summary>
public sealed partial class ProfilesViewModel : ViewModelBase
{
    /// <summary>한 번의 연결에서 인증서 확인을 묻는 최대 횟수(재시도 사이에 또 바뀌는 비정상 상황 대비).</summary>
    private const int MaxCertificatePrompts = 2;

    private bool _loaded;

    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    [ObservableProperty] public partial bool IsEmpty { get; private set; }

    public override async void OnAppearing()
    {
        // 편집 화면에서 돌아올 때마다 다시 읽는다(저장·삭제 반영)
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        var profiles = await AppServices.Profiles.LoadAsync();
        Profiles.Clear();
        foreach (var profile in profiles)
            Profiles.Add(new ProfileItem(profile, AppServices.Profiles.GetSecret(profile) is not null));

        IsEmpty = Profiles.Count == 0;
        _loaded = true;
    }

    public bool IsLoaded => _loaded;

    [RelayCommand]
    private Task Add()
    {
        return AppServices.Navigator.PushAsync(new ProfileEditViewModel(null));
    }

    [RelayCommand]
    private Task Edit(ProfileItem item)
    {
        return AppServices.Navigator.PushAsync(new ProfileEditViewModel(item.Profile));
    }

    [RelayCommand]
    private async Task Connect(ProfileItem item)
    {
        if (IsBusy) return;

        var profile = item.Profile.Clone();
        var secret = AppServices.Profiles.GetSecret(profile);
        var remember = false;
        if (secret is null)
        {
            var isToken = profile.AuthMode == AuthMode.ApiToken;
            var answer = await AppServices.Dialogs.PromptAsync(
                item.DisplayName,
                isToken ? L.T("Connect_TokenPrompt", profile.ApiTokenId) : L.T("Connect_PasswordPrompt", profile.UserName),
                isPassword: true,
                watermark: isToken ? L.T("Profile_TokenSecret") : L.T("Profile_Password"),
                checkLabel: L.T("Profile_RememberSecret"),
                isChecked: true);
            if (answer is null || answer.Text.Length == 0) return;

            secret = answer.Text;
            remember = answer.IsChecked;
        }

        if (profile.AuthMode == AuthMode.ApiToken)
            profile.ApiTokenSecret = SecureStringHelper.FromString(secret);
        else
            profile.Password = SecureStringHelper.FromString(secret);

        ServerSession? session = null;
        var ok = await TryAsync(async () => session = await LoginAsync(profile, item),
            L.T("Connect_Busy", item.DisplayName));
        if (!ok || session is null) return;

        if (remember) AppServices.Profiles.RememberSecret(item.Profile, secret);

        await AppServices.Navigator.PushAsync(new ClusterViewModel(session));
    }

    /// <summary>
    ///     로그인하고 버전을 읽어 연결을 확인한다. 서버 인증서를 아직 신뢰하지 않았으면 지문을 보여 주고,
    ///     사용자가 신뢰하면 프로필에 지문을 저장한 뒤 다시 시도한다.
    /// </summary>
    private static async Task<ServerSession> LoginAsync(ConnectionProfile profile, ProfileItem item)
    {
        for (var prompt = 0;; prompt++)
        {
            var api = new ProxmoxApiClient(profile);
            try
            {
                await api.LoginAsync();
                var version = await api.GetVersionAsync();
                return new ServerSession(api, profile, version.Version);
            }
            catch (CertificateTrustException ex) when (prompt < MaxCertificatePrompts)
            {
                api.Dispose();
                if (!await ConfirmCertificateAsync(ex.Rejection)) throw;

                profile.CertificateThumbprint = ex.Rejection.Thumbprint;
                item.Profile.CertificateThumbprint = ex.Rejection.Thumbprint;
                await AppServices.Profiles.SaveAsync(item.Profile);
            }
            catch
            {
                api.Dispose();
                throw;
            }
        }
    }

    private static Task<bool> ConfirmCertificateAsync(CertificateRejection rejection)
    {
        var details = L.T("Cert_Details",
            rejection.Host,
            ServerCertificateValidator.FormatThumbprint(rejection.Thumbprint),
            rejection.Subject,
            rejection.Issuer,
            rejection.Expires);

        return rejection.IsMismatch
            ? AppServices.Dialogs.ConfirmAsync(L.T("Cert_MismatchTitle"),
                L.T("Cert_MismatchText", ServerCertificateValidator.FormatThumbprint(rejection.PinnedThumbprint!),
                    details),
                L.T("Cert_Trust"), true)
            : AppServices.Dialogs.ConfirmAsync(L.T("Cert_NewTitle"), L.T("Cert_NewText", details), L.T("Cert_Trust"));
    }
}

/// <summary>목록의 프로필 한 줄.</summary>
public sealed class ProfileItem(ConnectionProfile profile, bool hasSavedSecret)
{
    public ConnectionProfile Profile { get; } = profile;

    public string DisplayName => string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Host : Profile.Name;

    public string Address => Profile.Port == 8006 ? Profile.Host : $"{Profile.Host}:{Profile.Port}";

    public string Account => Profile.AuthMode == AuthMode.ApiToken ? Profile.ApiTokenId ?? string.Empty : Profile.UserName;

    public bool HasSavedSecret { get; } = hasSavedSecret;

    public string Initial => DisplayName.Length > 0 ? char.ToUpperInvariant(DisplayName[0]).ToString() : "?";
}
