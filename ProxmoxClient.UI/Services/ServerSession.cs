using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.UI.Services;

/// <summary>로그인한 서버 하나와의 연결 — 클러스터 화면이 닫히면 함께 정리된다.</summary>
public sealed class ServerSession(ProxmoxApiClient api, ConnectionProfile profile, string version) : IDisposable
{
    private Task<ClusterOverview>? _prefetchedOverview;

    /// <summary>로그인 직후 미리 요청해 둔 클러스터 현황 — 첫 화면이 왕복을 한 번 덜 기다린다(한 번만 쓴다).</summary>
    internal Task<ClusterOverview>? TakePrefetchedOverview()
    {
        return Interlocked.Exchange(ref _prefetchedOverview, null);
    }

    internal void Prefetch(Task<ClusterOverview> overview)
    {
        _prefetchedOverview = overview;
    }

    public ProxmoxApiClient Api { get; } = api;

    public ConnectionProfile Profile { get; } = profile;

    /// <summary>PVE 버전 문자열(예: 9.0.3).</summary>
    public string Version { get; } = version;

    /// <summary>로그인 사용자의 권한 요약(화면이 버튼·탭을 권한에 맞게 보인다). 읽지 못하면 빈 권한.</summary>
    public PermissionsInfo Permissions { get; internal set; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Host : Profile.Name;

    /// <summary>로그인 사용자(user@realm) — API 토큰이면 토큰 ID.</summary>
    public string UserDisplay => Api.AuthenticatedUser ?? Profile.ApiTokenId ?? Profile.UserName;

    /// <summary>
    ///     세션 만료(갱신 거부)를 지켜보다가 한 번 알리고 로그인 화면으로 돌아간다 — 그대로 두면 화면마다 같은 오류가 쌓인다.
    /// </summary>
    internal void WatchExpiry()
    {
        Api.SessionExpiredDetected += () => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed) return;

            await AppServices.Dialogs.AlertAsync(Localization.L.T("Session_ExpiredTitle"),
                Localization.L.T("Session_ExpiredText", DisplayName));
            if (!_disposed) await AppServices.Navigator.PopToRootAsync();
        });
    }

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        Api.Dispose();
    }
}
