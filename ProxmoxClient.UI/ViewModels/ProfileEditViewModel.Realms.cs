using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Login;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>
///     모바일 서버 편집 — PC 로그인 화면처럼 <b>사용자</b>와 <b>인증 영역</b>을 따로 받는다(저장할 때 "사용자@영역").
///     영역은 화면을 열면 서버에서 자동으로 가져오고(새로 고침 단추도 있다), 가져오기 전에는 PC 와 같은 기본값(pam·pve)
///     과 저장된 영역을 보여 준다. 처음 보는 인증서면 지문을 확인받고, 저장할 때 그 지문을 프로필에 남긴다(같은 주소일 때만).
/// </summary>
public sealed partial class ProfileEditViewModel
{
    private (string Host, int Port, string Thumbprint)? _trusted;

    public ObservableCollection<PveAuthDomain> Realms { get; } = [];

    [ObservableProperty] public partial PveAuthDomain? SelectedRealm { get; set; }

    [ObservableProperty] public partial bool IsFetchingRealms { get; set; }

    [ObservableProperty] public partial string RealmStatus { get; set; } = string.Empty;

    public bool HasRealms => Realms.Count > 0;

    /// <summary>사용자 이름(영역 없이 — 예: root). PC 로그인 화면의 '사용자 이름' 칸.</summary>
    [ObservableProperty] public partial string UserOnly { get; set; } = "root";

    /// <summary>저장할 사용자 — "사용자@영역"(영역을 안 골랐으면 pam).</summary>
    public string ComposedUser
    {
        get
        {
            var user = UserOnly.Trim();
            if (user.Contains('@')) return user; // 직접 user@realm 을 넣었으면 그대로
            return $"{user}@{SelectedRealm?.Realm ?? "pam"}";
        }
    }

    /// <summary>저장된 "사용자@영역" 을 두 칸으로 — 영역 목록은 PC 와 같은 기본값(pam·pve)에 저장된 영역을 더해 시작.</summary>
    private void SplitUser(string stored)
    {
        var at = stored.IndexOf('@');
        UserOnly = at >= 0 ? stored[..at] : stored.Length > 0 ? stored : "root";
        var realm = at >= 0 ? stored[(at + 1)..] : "pam";
        ApplyRealms([new PveAuthDomain { Realm = "pam", Type = "pam" }, new PveAuthDomain { Realm = "pve", Type = "pve" }],
            realm);
    }

    private void ApplyRealms(IReadOnlyList<PveAuthDomain> domains, string? keep)
    {
        Realms.Clear();
        foreach (var domain in domains) Realms.Add(domain);
        // 저장돼 있던 영역이 목록에 없으면(아직 못 가져옴) 그대로 보이게 넣어 둔다
        if (keep is { Length: > 0 } && Realms.All(r => r.Realm != keep))
            Realms.Add(new PveAuthDomain { Realm = keep, Type = keep });
        SelectedRealm = Realms.FirstOrDefault(r => r.Realm == keep)
                        ?? Realms.FirstOrDefault(r => r.IsDefault)
                        ?? Realms.FirstOrDefault();
        OnPropertyChanged(nameof(HasRealms));
    }

    /// <summary>화면이 열릴 때 — 주소가 있으면 영역을 서버에서 가져온다(PC 로그인 화면이 서버를 고를 때와 같다).</summary>
    public void AutoFetchRealms()
    {
        if (IsTokenAuth || Host.Trim().Length == 0 || Realms.Any(r => r.Comment.Length > 0)) return;

        _ = FetchRealmsAsync();
    }

    [RelayCommand]
    private async Task FetchRealmsAsync()
    {
        if (IsFetchingRealms) return;

        var host = NormalizeHost(Host);
        if (host.Length == 0 || !int.TryParse(Port.Trim(), out var port) || port is < 1 or > 65535)
        {
            RealmStatus = L.T("Profile_HostRequired");
            return;
        }

        var probe = _original?.Clone() ?? new ConnectionProfile();
        if (!string.Equals(probe.Host, host, StringComparison.OrdinalIgnoreCase) || probe.Port != port)
            probe.CertificateThumbprint = null;
        probe.Host = host;
        probe.Port = port;
        probe.AcceptSelfSignedCertificate = AcceptSelfSigned;
        if (_trusted is { } t && t.Host == host && t.Port == port) probe.CertificateThumbprint = t.Thumbprint;

        IsFetchingRealms = true;
        RealmStatus = L.T("Realm_Fetching");
        try
        {
            using var client = new ProxmoxApiClient(probe);
            IReadOnlyList<PveAuthDomain> domains = [];
            await LoginTrust.RunAsync(
                async () => domains = await client.GetAuthDomainsAsync(),
                rejection => LoginTrust.ConfirmAsync(rejection, probe));
            if (probe.CertificateThumbprint is { } thumb)
            {
                _trusted = (host, port, thumb);
                _thumbprintHost = host;
                _thumbprintPort = port;
                CertificateThumbprint = thumb; // PC 와 같은 '신뢰한 지문' 표시
            }

            if (domains.Count > 0) ApplyRealms(domains, SelectedRealm?.Realm);
            RealmStatus = domains.Count > 0 ? string.Empty : L.T("Realm_None"); // 성공은 목록이 말해 준다
        }
        catch (CertificateTrustException)
        {
            RealmStatus = L.T("LoginWindow_M01");
        }
        catch (Exception ex) when (ex is ProxmoxApiException or HttpRequestException or IOException
                                       or OperationCanceledException)
        {
            RealmStatus = L.T("Realm_Failed", ErrorText.Describe(ex));
        }
        finally
        {
            IsFetchingRealms = false;
        }
    }

    /// <summary>저장할 때 — 영역을 가져오며 신뢰한 지문을(같은 주소면) 프로필에 남긴다.</summary>
    private void ApplyTrustedThumbprint(ConnectionProfile profile)
    {
        if (_trusted is { } t && string.Equals(t.Host, profile.Host, StringComparison.OrdinalIgnoreCase) &&
            t.Port == profile.Port)
            profile.CertificateThumbprint = t.Thumbprint;
    }
}
