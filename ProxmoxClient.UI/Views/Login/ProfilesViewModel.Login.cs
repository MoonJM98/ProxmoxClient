using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Core.Vpn;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Login;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>
///     데스크톱 로그인 화면(WPF LoginWindow) 상태 — 저장된 서버 고르기, 아이디·비밀번호·인증 영역, 로그인.
///     연결 자체는 기존 <see cref="ProfilesViewModel" /> 의 로그인(<c>LoginAsync</c>: 인증서 확인·쓰기 확인·권한 미리 읽기)을 그대로 쓴다.
/// </summary>
public sealed partial class ProfilesViewModel
{
    /// <summary>서버가 인증 영역을 못 돌려줄 때 쓰는 기본값(WPF 와 같다).</summary>
    private static readonly IReadOnlyList<PveAuthDomain> DefaultRealms =
    [
        new() { Realm = "pam", Type = "pam" },
        new() { Realm = "pve", Type = "pve" }
    ];

    private readonly LastLoginStore _lastLoginStore = new();
    private LastLogin? _lastLogin;

    /// <summary>마지막으로 고른 서버 — 목록을 다시 읽어도 같은 서버를 고른다(WPF ReloadAfterManagerAsync).</summary>
    private Guid? _rememberedServerId;

    /// <summary>최신 인증 영역 요청만 반영 — 서버를 빠르게 바꿀 때 이전 응답이 나중에 도착해 목록을 덮지 않도록.</summary>
    private int _realmRequestId;

    /// <summary>OpenID 브라우저 로그인 대기 — 화면을 떠나면 멈춘다.</summary>
    private CancellationTokenSource? _openIdWait;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCredentials))]
    public partial ProfileItem? SelectedServer { get; set; }

    /// <summary>자격 증명 칸은 서버를 고른 뒤에만 보인다(WPF CredentialPanel).</summary>
    public bool ShowCredentials => SelectedServer is not null;

    /// <summary>인증 영역 목록(서버에서 받은 것, 못 받으면 pam/pve).</summary>
    public ObservableCollection<PveAuthDomain> Realms { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenIdRealm), nameof(IsPasswordEntryVisible), nameof(OpenIdHint))]
    public partial PveAuthDomain? SelectedRealm { get; set; }

    public bool IsOpenIdRealm => SelectedRealm?.Type == "openid";

    /// <summary>OpenID 영역이면 비밀번호 칸을 숨긴다(WPF UpdateOpenIdUi).</summary>
    public bool IsPasswordEntryVisible => !IsOpenIdRealm;

    /// <summary>OpenID 영역일 때 리디렉션 주소 안내.</summary>
    public string OpenIdHint => IsOpenIdRealm ? L.T("OpenId_Hint", OpenIdFlow.RedirectUrl) : string.Empty;

    [ObservableProperty] public partial string UserText { get; set; } = string.Empty;

    [ObservableProperty] public partial string LoginPassword { get; set; } = string.Empty;

    /// <summary>상태 줄(WPF StatusText).</summary>
    [ObservableProperty] public partial string Status { get; set; } = string.Empty;

    /// <summary>로그인 진행 중 — 로그인 버튼을 잠근다.</summary>
    [ObservableProperty] public partial bool IsSigningIn { get; set; }

    /// <summary>인증 영역 다시 가져오는 중 — 새로고침 단추를 잠근다.</summary>
    [ObservableProperty] public partial bool IsFetchingRealms { get; set; }

    partial void OnSelectedServerChanged(ProfileItem? value)
    {
        if (value is null) return;

        _rememberedServerId = value.Profile.Id;
        var userName = value.Profile.UserName;
        var at = userName.IndexOf('@');
        UserText = at > 0 ? userName[..at] : userName;
        LoginPassword = string.Empty;

        // 아직 신뢰하지 않은 서버면 여기서 지문 확인 창을 띄운다 — 신뢰해야 인증 영역을 가져올 수 있다
        _ = LoadRealmsAsync(value.Profile, true);
    }

    private partial void OnLeaving(bool removed)
    {
        // 화면을 닫으면 브라우저 로그인 대기도 멈춘다(WPF: 창을 닫으면 CancelOpenIdWait)
        if (removed) _openIdWait?.Cancel();
    }

    /// <summary>목록을 다시 읽은 뒤 — 같은 서버를 다시 고르고, 처음이면 마지막 로그인 서버(없으면 첫 서버)를 고른다.</summary>
    private partial Task OnLoadedAsync() => LoadLoginStateAsync();

    private async Task LoadLoginStateAsync()
    {
        if (!PlatformInfo.IsDesktop) return;

        try
        {
            _lastLogin ??= await _lastLoginStore.LoadAsync();
        }
        catch (Exception)
        {
            // 마지막 로그인 정보를 못 읽어도 목록은 보여 준다
        }

        ProfileItem? pick = null;
        if (_rememberedServerId is { } id) pick = Profiles.FirstOrDefault(p => p.Profile.Id == id);
        pick ??= Profiles.FirstOrDefault(p => p.Profile.Id == _lastLogin?.ProfileId) ?? Profiles.FirstOrDefault();
        SelectedServer = pick;
    }

    [RelayCommand]
    private async Task ReloadRealms()
    {
        if (SelectedServer is not { } item)
        {
            Status = L.T("LoginWindow_M02");
            return;
        }

        IsFetchingRealms = true;
        Status = L.T("LoginWindow_M03");
        try
        {
            if (await LoadRealmsAsync(item.Profile, true) is { } domains)
                Status = L.T("LoginWindow_M04", domains.Count);
        }
        finally
        {
            IsFetchingRealms = false;
        }
    }

    [RelayCommand]
    private Task OpenServerManager()
    {
        return AppServices.Navigator.PushViewAsync(L.T("ServerManagerWindow_01"),
            new ServerManagerView());
    }

    [RelayCommand]
    private async Task SignIn()
    {
        if (IsSigningIn) return;

        if (SelectedServer is not { } item)
        {
            Status = L.T("LoginWindow_M05");
            return;
        }

        if (IsOpenIdRealm)
        {
            await SignInOpenIdAsync(item);
            return;
        }

        if (BuildPasswordProfile(item) is not { } runtime) return;

        IsSigningIn = true;
        try
        {
            // VPN 을 쓰는 서버는 터널을 먼저 연다(WPF 와 같다) — 실패하면 로그인하지 않는다
            try
            {
                await VpnTunnel.EnsureAsync(runtime, text => Status = text);
            }
            catch (Exception ex) when (ex is OpenVpnException or IOException or UnauthorizedAccessException)
            {
                Status = L.T("MainViewModel_M14", ex.Message);
                return;
            }

            await SignInWithAsync(item, runtime);
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    /// <summary>입력한 아이디·암호로 이번 연결용 프로필을 만든다 — 빠진 값이 있으면 상태줄에 알리고 null.</summary>
    private ConnectionProfile? BuildPasswordProfile(ProfileItem item)
    {
        var user = UserText.Trim();
        if (user.Length == 0 || LoginPassword.Length == 0)
        {
            Status = L.T("LoginWindow_M06");
            return null;
        }

        string userName;
        if (user.Contains('@'))
            userName = user; // 전체 아이디(user@realm)를 직접 입력한 경우
        else if (SelectedRealm is { Realm.Length: > 0 } realm)
            userName = $"{user}@{realm.Realm}";
        else
        {
            Status = L.T("LoginWindow_M07");
            return null;
        }

        var runtime = item.Profile.Clone();
        runtime.AuthMode = AuthMode.Password;
        runtime.ApiTokenId = null;
        runtime.ApiTokenSecret = null;
        runtime.UserName = userName;
        runtime.Password = SecureStringHelper.FromString(LoginPassword);
        return runtime;
    }

    /// <summary>
    ///     서버에 로그인한다(기존 연결 로직 <c>LoginAsync</c> — 인증서 확인·쓰기 확인·권한 미리 읽기 포함).
    ///     성공하면 아이디를 기억하고 클러스터 화면으로 간다. 실패하면 상태 줄에 알린다(WPF VerifyLoginAsync).
    /// </summary>
    private async Task SignInWithAsync(ProfileItem item, ConnectionProfile runtime)
    {
        Status = L.T("LoginWindow_M09");
        ServerSession session;
        try
        {
            session = await LoginAsync(runtime, item);
        }
        catch (WriteCancelledException)
        {
            Status = string.Empty;
            return;
        }
        catch (Exception ex)
        {
            Status = L.T("LoginWindow_M10", ex.Message);
            return;
        }

        await RememberIdentityAsync(item, runtime);
        LoginPassword = string.Empty;
        Status = string.Empty;
        await AppServices.Navigator.PushAsync(new ClusterViewModel(session));
    }

    /// <summary>OpenID 영역 로그인 — 브라우저에서 IdP 로그인 후 받은 티켓을 "비밀번호"로 넘긴다(WPF ConnectOpenIdAsync).</summary>
    private async Task SignInOpenIdAsync(ProfileItem item)
    {
        if (item.Profile.UseVpn)
        {
            Status = L.T("OpenId_NoVpn");
            return;
        }

        var realm = SelectedRealm!.Realm;
        var runtime = item.Profile.Clone();
        runtime.AuthMode = AuthMode.Password;
        runtime.ApiTokenId = null;
        runtime.ApiTokenSecret = null;

        IsSigningIn = true;
        Status = L.T("OpenId_Waiting");
        var client = new ProxmoxApiClient(runtime);
        var wait = new CancellationTokenSource();
        _openIdWait = wait;
        try
        {
            await LoginTrust.RunAsync(
                () => OpenIdFlow.RunAsync(client, realm, wait.Token),
                rejection => LoginTrust.ConfirmAsync(rejection, runtime),
                () => PersistTrustAsync(item.Profile));

            // 서버가 정한 아이디와 받은 티켓으로 다시 로그인한다(WPF 와 같이 저장된 아이디를 기억한다)
            runtime.Password = SecureStringHelper.FromString(client.AuthTicket ?? string.Empty);
            await SignInWithAsync(item, runtime);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            Status = string.Empty; // 창을 닫아 멈춘 것 — 알릴 곳이 없다
        }
        catch (Exception ex)
        {
            Status = L.T("LoginWindow_M10", ex.Message);
        }
        finally
        {
            _openIdWait = null;
            wait.Dispose();
            client.Dispose();
            IsSigningIn = false;
        }
    }

    /// <summary>사용자가 신뢰한 지문을 저장된 프로필에 기록(저장 실패해도 이번 로그인은 메모리의 지문으로 계속).</summary>
    private static async Task PersistTrustAsync(ConnectionProfile stored)
    {
        try
        {
            await AppServices.Profiles.SaveAsync(stored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 지문 저장 실패는 이번 연결에만 영향 — 다음 연결 때 다시 확인한다
        }
    }

    /// <summary>
    ///     서버의 인증 영역을 가져온다(캐시 없이 매번). 인증서 확인이 필요하면 지문 확인 창을 띄운다.
    ///     더 새 요청이 있거나 조회에 실패하면 null(실패 땐 기본값 pam/pve 를 보여 준다).
    /// </summary>
    private async Task<IReadOnlyList<PveAuthDomain>?> LoadRealmsAsync(ConnectionProfile profile, bool interactive)
    {
        var requestId = ++_realmRequestId;
        var client = new ProxmoxApiClient(profile);
        try
        {
            IReadOnlyList<PveAuthDomain> domains;
            if (interactive)
            {
                IReadOnlyList<PveAuthDomain> fetched = [];
                await LoginTrust.RunAsync(
                    async () => fetched = await client.GetAuthDomainsAsync(),
                    async rejection => requestId == _realmRequestId && await LoginTrust.ConfirmAsync(rejection, profile),
                    () => PersistTrustAsync(profile));
                domains = fetched;
            }
            else
            {
                domains = await client.GetAuthDomainsAsync();
            }

            return ApplyRealms(requestId, profile, domains.Count > 0 ? domains : DefaultRealms);
        }
        catch (CertificateTrustException)
        {
            // 사용자가 신뢰를 거절했거나(또는 그 사이 다른 서버 선택) 확인하지 않은 상태
            Status = L.T("LoginWindow_M01");
            ApplyRealms(requestId, profile, DefaultRealms);
            return null;
        }
        catch (Exception ex) when (ex is ProxmoxApiException or IOException or OperationCanceledException)
        {
            ApplyRealms(requestId, profile, DefaultRealms); // 조회 실패 시 기본값(pam/pve)
            return null;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>더 새 요청이 없고 같은 서버가 계속 선택돼 있을 때만 목록에 반영.</summary>
    private IReadOnlyList<PveAuthDomain>? ApplyRealms(int requestId, ConnectionProfile profile,
        IReadOnlyList<PveAuthDomain> domains)
    {
        if (requestId != _realmRequestId
            || SelectedServer is not { } current
            || current.Profile.Id != profile.Id)
            return null;

        FillRealms(domains, profile.UserName);
        return domains;
    }

    /// <summary>기본 영역 먼저, 그다음 이름순. 선택은 마지막 로그인 영역 → 저장된 아이디의 영역 → 기본 영역 순.</summary>
    private void FillRealms(IReadOnlyList<PveAuthDomain> domains, string fullUserName)
    {
        var ordered = domains
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Realm, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Realms.Clear();
        foreach (var domain in ordered) Realms.Add(domain);

        var preferred = PreferredRealm(fullUserName);
        SelectedRealm = ordered.FirstOrDefault(d => string.Equals(d.Realm, preferred, StringComparison.OrdinalIgnoreCase))
                        ?? ordered.FirstOrDefault(d => d.IsDefault)
                        ?? ordered[0];
    }

    /// <summary>선택된 서버가 마지막 로그인 서버면 그때의 영역, 아니면 저장된 아이디(user@realm)의 영역.</summary>
    private string? PreferredRealm(string fullUserName)
    {
        if (SelectedServer is { } item && _lastLogin is { } last && last.ProfileId == item.Profile.Id)
            return last.Realm;

        var at = fullUserName.LastIndexOf('@');
        return at >= 0 && at < fullUserName.Length - 1 ? fullUserName[(at + 1)..] : null;
    }

    /// <summary>아이디·영역을 기억한다(자격 증명은 저장하지 않는다). 아이디가 바뀌었을 때만 프로필을 쓴다.</summary>
    private async Task RememberIdentityAsync(ProfileItem item, ConnectionProfile runtime)
    {
        var stored = item.Profile;
        var changed = stored.UserName != runtime.UserName || stored.ApiTokenId != runtime.ApiTokenId;
        stored.UserName = runtime.UserName;
        stored.ApiTokenId = runtime.ApiTokenId;

        var at = runtime.UserName.LastIndexOf('@');
        var realm = at >= 0 ? runtime.UserName[(at + 1)..] : SelectedRealm?.Realm ?? string.Empty;
        var lastLogin = new LastLogin(stored.Id, realm);

        try
        {
            if (changed) await AppServices.Profiles.SaveAsync(stored);

            await _lastLoginStore.SaveAsync(lastLogin);
            _lastLogin = lastLogin;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 기억하기 실패는 로그인 자체에 영향이 없다(WPF 는 로그에 남긴다)
        }
    }
}
