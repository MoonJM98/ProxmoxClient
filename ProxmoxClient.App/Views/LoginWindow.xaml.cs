using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.App.Views;

public partial class LoginWindow : Window
{
    /// <summary>서버가 인증 영역을 못 돌려줄 때 쓰는 기본값.</summary>
    private static readonly IReadOnlyList<PveAuthDomain> DefaultRealms =
    [
        new() { Realm = "pam", Type = "pam" },
        new() { Realm = "pve", Type = "pve" }
    ];

    private readonly LastLoginStore _lastLoginStore = new();
    private readonly ProfileStore _store = new();
    private bool _busy;
    private bool _fillingRealm;

    private LastLogin? _lastLogin;

    /// <summary>최신 요청만 반영 — 서버를 빠르게 바꿀 때 이전 응답이 나중에 도착해 목록을 덮지 않도록.</summary>
    private int _realmRequestId;

    private string? _selectedRealm;

    public LoginWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Loaded += async (_, _) => await LoadSavedProfilesAsync();
    }

    /// <summary>로그인 성공 시 전달할 프로필(자격 증명은 메모리에만 존재).</summary>
    public ConnectionProfile? ResultProfile { get; private set; }

    private ConnectionProfile? Selected => SavedList.SelectedItem as ConnectionProfile;

    private async Task LoadSavedProfilesAsync()
    {
        try
        {
            _lastLogin ??= await _lastLoginStore.LoadAsync();
            var profiles = await _store.LoadAsync();
            SavedList.ItemsSource = profiles;
            if (profiles.Count > 0)
            {
                var last = profiles.FirstOrDefault(p => p.Id == _lastLogin?.ProfileId);
                SavedList.SelectedItem = last ?? profiles[0];
            }
        }
        catch
        {
            // 저장된 프로필 로드 실패는 새 서버 등록으로 진행 가능
        }
    }

    private void OnSavedListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedList.SelectedItem is not ConnectionProfile profile)
        {
            CredentialPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var userName = profile.UserName;
        var at = userName.IndexOf('@');
        UserBox.Text = at > 0 ? userName[..at] : userName;
        PassBox.Clear();
        CredentialPanel.Visibility = Visibility.Visible;

        // 아직 신뢰하지 않은 서버면 여기서 지문 확인 창을 띄운다 — 신뢰해야 인증 영역을 가져올 수 있다
        _ = LoadRealmsAsync(profile, true);
    }

    /// <param name="interactive">
    ///     인증서 확인이 필요하면 지문 확인 창을 띄운다. 창을 띄우는 사이 다른 서버를 선택했으면 묻지 않고 중단한다.
    /// </param>
    /// <returns>화면에 반영한 인증 영역 목록. 더 새 요청이 있거나 조회에 실패하면 null.</returns>
    private async Task<IReadOnlyList<PveAuthDomain>?> LoadRealmsAsync(ConnectionProfile profile,
        bool interactive = false)
    {
        // 캐시 없이 매번 조회 — 서버를 다시 선택하거나 서버에서 인증 영역을 추가·삭제해도 곧바로 반영된다
        var requestId = ++_realmRequestId;
        var client = new ProxmoxApiClient(profile);
        try
        {
            IReadOnlyList<PveAuthDomain> domains;
            if (interactive)
            {
                IReadOnlyList<PveAuthDomain> fetched = [];
                await CertificateTrust.RunAsync(
                    async () => fetched = await client.GetAuthDomainsAsync(),
                    rejection => requestId == _realmRequestId && CertificateTrust.Confirm(this, rejection, profile),
                    () => TrustStoredCertificateAsync(profile, profile));
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
            SetStatus(Loc.T("LoginWindow_M01"));
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
    private IReadOnlyList<PveAuthDomain>? ApplyRealms(
        int requestId, ConnectionProfile profile, IReadOnlyList<PveAuthDomain> domains)
    {
        if (requestId != _realmRequestId
            || SavedList.SelectedItem is not ConnectionProfile current
            || current.Id != profile.Id)
            return null;

        FillRealmBox(domains, profile.UserName);
        return domains;
    }

    private void FillRealmBox(IReadOnlyList<PveAuthDomain> domains, string fullUserName)
    {
        _fillingRealm = true;
        try
        {
            var ordered = domains
                .OrderByDescending(d => d.IsDefault)
                .ThenBy(d => d.Realm, StringComparer.OrdinalIgnoreCase)
                .ToList();
            RealmBox.ItemsSource = ordered;

            var preferred = PreferredRealm(fullUserName);
            var pick =
                ordered.FirstOrDefault(d => string.Equals(d.Realm, preferred, StringComparison.OrdinalIgnoreCase))
                ?? ordered.FirstOrDefault(d => d.IsDefault)
                ?? ordered[0];
            RealmBox.SelectedItem = pick;
            _selectedRealm = pick.Realm;
        }
        finally
        {
            _fillingRealm = false;
        }
    }

    /// <summary>선택된 서버가 마지막 로그인 서버면 그때의 영역, 아니면 저장된 아이디(user@realm)의 영역.</summary>
    private string? PreferredRealm(string fullUserName)
    {
        if (Selected is { } profile && _lastLogin is { } last && last.ProfileId == profile.Id) return last.Realm;

        var at = fullUserName.LastIndexOf('@');
        return at >= 0 && at < fullUserName.Length - 1 ? fullUserName[(at + 1)..] : null;
    }

    /// <summary>인증 영역 다시 가져오기 — 캐시를 버리고 서버에서 새로 조회(필요하면 인증서 지문 확인).</summary>
    private async void OnReloadRealms(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile)
        {
            SetStatus(Loc.T("LoginWindow_M02"));
            return;
        }

        BtnReloadRealms.IsEnabled = false;
        SetStatus(Loc.T("LoginWindow_M03"));
        try
        {
            if (await LoadRealmsAsync(profile, true) is { } domains) SetStatus(Loc.T("LoginWindow_M04", domains.Count));
        }
        finally
        {
            BtnReloadRealms.IsEnabled = true;
        }
    }

    private void OnRealmChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingRealm) return;

        if (RealmBox.SelectedItem is PveAuthDomain domain) _selectedRealm = domain.Realm;
    }

    private async void OnSavedConnect(object sender, RoutedEventArgs e)
    {
        await ConnectSelectedAsync();
    }

    private async void OnPassBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ConnectSelectedAsync();
        }
    }

    private async Task ConnectSelectedAsync()
    {
        if (_busy) return;

        if (Selected is not { } stored)
        {
            SetStatus(Loc.T("LoginWindow_M05"));
            return;
        }

        var runtime = stored.Clone();
        runtime.AuthMode = AuthMode.Password;
        runtime.ApiTokenId = null;
        runtime.ApiTokenSecret = null;

        var user = UserBox.Text.Trim();
        runtime.Password = PassBox.SecurePassword; // WPF PasswordBox → SecureString 직접 전달
        if (user.Length == 0 || PassBox.SecurePassword.Length == 0)
        {
            SetStatus(Loc.T("LoginWindow_M06"));
            return;
        }

        if (user.Contains('@'))
        {
            runtime.UserName = user; // 전체 아이디(user@realm)를 직접 입력한 경우
        }
        else if (_selectedRealm is { Length: > 0 } realm)
        {
            runtime.UserName = $"{user}@{realm}";
        }
        else
        {
            SetStatus(Loc.T("LoginWindow_M07"));
            return;
        }

        if (runtime.UseVpn)
        {
            RememberIdentity(stored, runtime);
            SetStatus(Loc.T("LoginWindow_M08"));
            ResultProfile = runtime;
            DialogResult = true;
            return;
        }

        _busy = true;
        BtnLogin.IsEnabled = false;
        SetStatus(Loc.T("LoginWindow_M09"));

        var client = new ProxmoxApiClient(runtime);
        try
        {
            if (runtime.AuthMode == AuthMode.ApiToken)
                await client.GetClusterResourcesAsync(); // 토큰 실제 검증(권한 필요 엔드포인트)
            else
                await CertificateTrust.RunAsync(
                    () => client.LoginAsync(),
                    rejection => CertificateTrust.Confirm(this, rejection, runtime),
                    () => TrustStoredCertificateAsync(stored, runtime));

            RememberIdentity(stored, runtime);
            ResultProfile = runtime;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("LoginWindow_M10", ex.Message));
        }
        finally
        {
            client.Dispose();
            _busy = false;
            BtnLogin.IsEnabled = true;
        }
    }

    /// <summary>사용자가 신뢰한 인증서 지문을 저장된 프로필에 기록(저장 실패해도 이번 로그인은 메모리의 지문으로 계속).</summary>
    private async Task TrustStoredCertificateAsync(ConnectionProfile stored, ConnectionProfile runtime)
    {
        stored.CertificateThumbprint = runtime.CertificateThumbprint;
        try
        {
            await _store.SaveAsync(stored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log($"[로그인] 인증서 지문 저장 실패: {ex.Message}");
        }
    }

    private async void RememberIdentity(ConnectionProfile stored, ConnectionProfile runtime)
    {
        var changed = stored.UserName != runtime.UserName
                      || stored.ApiTokenId != runtime.ApiTokenId;
        stored.UserName = runtime.UserName;
        stored.ApiTokenId = runtime.ApiTokenId;

        var at = runtime.UserName.LastIndexOf('@');
        var realm = at >= 0 ? runtime.UserName[(at + 1)..] : _selectedRealm ?? string.Empty;
        var lastLogin = new LastLogin(stored.Id, realm);

        try
        {
            if (changed) await _store.SaveAsync(stored); // 아이디만 기록(자격 증명은 직렬화에서 제외됨)

            await _lastLoginStore.SaveAsync(lastLogin);
            _lastLogin = lastLogin;
        }
        catch (Exception ex)
        {
            App.Log($"[로그인] 마지막 로그인 정보 저장 실패: {ex}");
        }
    }

    private void OnOpenManager(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var manager = new ServerManagerWindow { Owner = this };
        manager.ShowDialog();

        _ = ReloadAfterManagerAsync();
    }

    private async Task ReloadAfterManagerAsync()
    {
        var selectedId = Selected?.Id;
        await LoadSavedProfilesAsync();
        if (selectedId is { } id && SavedList.ItemsSource is IReadOnlyList<ConnectionProfile> items)
            SavedList.SelectedItem = items.FirstOrDefault(p => p.Id == id) ?? SavedList.SelectedItem;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}