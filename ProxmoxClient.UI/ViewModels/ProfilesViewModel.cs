using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.ViewModels;

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
        await OnLoadedAsync();
    }

    public bool IsLoaded => _loaded;

    /// <summary>데스크톱 로그인 화면(Views/Login)이 목록을 읽은 뒤 마지막 로그인 서버를 고르는 자리. 모바일은 아무것도 하지 않는다.</summary>
    private partial Task OnLoadedAsync();

    /// <summary>화면을 떠날 때(데스크톱 로그인 화면의 진행 중 브라우저 로그인 취소 등).</summary>
    public override void OnDisappearing(bool removed)
    {
        base.OnDisappearing(removed);
        OnLeaving(removed);
    }

    private partial void OnLeaving(bool removed);

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
                checkLabel: AppServices.Profiles.CanRememberSecrets ? L.T("Profile_RememberSecret") : null,
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
        var ok = await TryAsync(async () =>
            {
                await VpnTunnel.EnsureAsync(profile); // VPN 을 쓰는 서버는 터널부터(지원 안 하는 기기면 안내 오류)
                session = await LoginAsync(profile, item);
            },
            L.T("Connect_Busy", item.DisplayName));
        if (!ok || session is null) return;

        if (remember)
            try
            {
                AppServices.Profiles.RememberSecret(item.Profile, secret);
            }
            catch (Exception ex)
            {
                await AppServices.Dialogs.AlertAsync(L.T("Error_Title"), ex.Message); // 연결은 그대로 진행
            }

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
            // 서버 상태를 바꾸는 요청은 모두 사용자 확인을 거친다(통신 계층에서 강제)
            var api = new ProxmoxApiClient(profile) { WriteGuard = WriteConfirmation.GuardAsync };
            try
            {
                await api.LoginAsync();
                // 버전 확인과 첫 화면 데이터를 동시에 요청한다(화면 전환을 기다리지 않고 미리 받기 시작)
                var overview = api.GetClusterOverviewAsync();
                _ = overview.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                var permissionsTask = api.GetPermissionsSummaryAsync();
                var version = await api.GetVersionAsync();
                var session = new ServerSession(api, profile, version.Version);
                session.WatchExpiry();
                try
                {
                    session.Permissions = await permissionsTask;
                }
                catch (Exception)
                {
                    // 권한 요약을 못 읽어도 로그인은 진행 — 서버가 요청마다 권한을 다시 확인한다
                }
                session.Prefetch(overview);
                return session;
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
