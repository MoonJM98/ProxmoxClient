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
///     모바일 서버 편집 — 인증 영역 가져오기(데스크톱 로그인 화면의 영역 목록과 같은 일). 서버 주소로 영역을 읽어
///     고르게 하고, 고르면 사용자 칸을 "이름@영역" 으로 맞춘다. 처음 보는 인증서면 지문을 확인받고, 저장할 때 그 지문을
///     프로필에 남긴다(같은 주소일 때만).
/// </summary>
public sealed partial class ProfileEditViewModel
{
    private (string Host, int Port, string Thumbprint)? _trusted;

    public ObservableCollection<PveAuthDomain> Realms { get; } = [];

    [ObservableProperty] public partial PveAuthDomain? SelectedRealm { get; set; }

    [ObservableProperty] public partial bool IsFetchingRealms { get; set; }

    [ObservableProperty] public partial string RealmStatus { get; set; } = string.Empty;

    public bool HasRealms => Realms.Count > 0;

    /// <summary>영역을 고르면 사용자 칸의 @영역 부분을 바꾼다(이름은 그대로).</summary>
    partial void OnSelectedRealmChanged(PveAuthDomain? value)
    {
        if (value is null) return;

        var user = UserName.Trim();
        var at = user.IndexOf('@');
        var name = at >= 0 ? user[..at] : user;
        UserName = $"{(name.Length > 0 ? name : "root")}@{value.Realm}";
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
            if (probe.CertificateThumbprint is { } thumb) _trusted = (host, port, thumb);

            Realms.Clear();
            foreach (var domain in domains) Realms.Add(domain);
            OnPropertyChanged(nameof(HasRealms));
            var current = UserName.Contains('@') ? UserName[(UserName.IndexOf('@') + 1)..] : "pam";
            SelectedRealm = Realms.FirstOrDefault(r => r.Realm == current) ?? Realms.FirstOrDefault();
            RealmStatus = Realms.Count > 0 ? L.T("Realm_Fetched", Realms.Count) : L.T("Realm_None");
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
