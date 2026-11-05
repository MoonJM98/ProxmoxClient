using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.Mobile.Services;

/// <summary>로그인한 서버 하나와의 연결 — 클러스터 화면이 닫히면 함께 정리된다.</summary>
public sealed class ServerSession(ProxmoxApiClient api, ConnectionProfile profile, string version) : IDisposable
{
    public ProxmoxApiClient Api { get; } = api;

    public ConnectionProfile Profile { get; } = profile;

    /// <summary>PVE 버전 문자열(예: 9.0.3).</summary>
    public string Version { get; } = version;

    public string DisplayName => string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Host : Profile.Name;

    /// <summary>로그인 사용자(user@realm) — API 토큰이면 토큰 ID.</summary>
    public string UserDisplay => Api.AuthenticatedUser ?? Profile.ApiTokenId ?? Profile.UserName;

    public void Dispose()
    {
        Api.Dispose();
    }
}
