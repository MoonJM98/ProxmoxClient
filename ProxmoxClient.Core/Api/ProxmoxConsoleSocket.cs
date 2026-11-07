using System.Net;
using System.Net.WebSockets;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     vncproxy/termproxy 가 연 포트로 붙는 Proxmox 콘솔 웹소켓(vncwebsocket) 연결 — VNC 와 CT 터미널이 공유한다.
///     RDP 콘솔은 rdpproxy 토큰 경로(…/rdp/{token})로 붙는다.
/// </summary>
internal static class ProxmoxConsoleSocket
{
    /// <summary>프록시는 생성 후 짧은 시간만 연결을 기다리므로 그 안에 붙어야 한다.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(25);

    /// <summary>
    ///     연결 유지 확인 — 이 간격으로 ping 을 보내고 <see cref="KeepAliveTimeout" /> 안에 pong 이 없으면 연결을 끊는다.
    ///     회선이 조용히 죽어도(와이파이 전환·절전·NAT 만료) 수신 루프가 영영 기다리지 않고 "연결 끊김"으로 끝난다.
    /// </summary>
    internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(10);

    public static Task<ClientWebSocket> ConnectAsync(
        ProxmoxApiClient api, string node, ResourceKind kind, int vmid, int port, string ticket, CancellationToken ct)
    {
        return ConnectAsync(api, ConsoleTarget.ForGuest(node, kind, vmid), port, ticket, ct);
    }

    [Versioning.PveApi("GET", "/nodes/{node}/vncwebsocket")]
    [Versioning.PveApi("GET", "/nodes/{node}/qemu/{vmid}/vncwebsocket")]
    [Versioning.PveApi("GET", "/nodes/{node}/lxc/{vmid}/vncwebsocket")]
    public static Task<ClientWebSocket> ConnectAsync(
        ProxmoxApiClient api, ConsoleTarget target, int port, string ticket, CancellationToken ct)
    {
        var uri = new UriBuilder("wss", ProxmoxApiClient.UriHost(api.Profile.Host), api.Profile.Port) // ClientWebSocket 은 ws/wss 스키마만 허용
        {
            Path = $"/api2/json/{target.BasePath}/vncwebsocket",
            Query = $"port={port}&vncticket={Uri.EscapeDataString(ticket)}"
        }.Uri;

        return OpenAsync(api, uri, "binary", ct);
    }

    /// <summary>
    ///     RDP 콘솔 웹소켓(RDCleanPath) — pveproxy 가 세션(쿠키·API 토큰)과 VM.Console 권한을 확인한 뒤
    ///     rdpproxy 로 넘긴다. 서브프로토콜은 쓰지 않는다(웹 UI 의 IronRDP 클라이언트와 같다).
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/qemu/{vmid}/rdp/{token}", Experimental = true)]
    public static Task<ClientWebSocket> ConnectRdpAsync(
        ProxmoxApiClient api, string node, int vmid, string token, CancellationToken ct)
    {
        var uri = new UriBuilder("wss", ProxmoxApiClient.UriHost(api.Profile.Host), api.Profile.Port)
        {
            Path = $"/api2/json/nodes/{Uri.EscapeDataString(node)}/qemu/{vmid}/rdp/{Uri.EscapeDataString(token)}"
        }.Uri;

        return OpenAsync(api, uri, null, ct);
    }

    private static async Task<ClientWebSocket> OpenAsync(
        ProxmoxApiClient api, Uri uri, string? subProtocol, CancellationToken ct)
    {
        var profile = api.Profile;
        var websocket = new ClientWebSocket();
        if (subProtocol is not null) websocket.Options.AddSubProtocol(subProtocol);
        websocket.Options.KeepAliveInterval = KeepAliveInterval;
        websocket.Options.KeepAliveTimeout = KeepAliveTimeout;
        if (api.AuthTicket is { } authTicket)
        {
            if (uri.HostNameType == UriHostNameType.IPv6)
            {
                // 쿠키 도메인은 IPv6 주소를 받지 않는다 — 머리글로 직접 보낸다
                websocket.Options.SetRequestHeader("Cookie", "PVEAuthCookie=" + authTicket);
            }
            else
            {
                var cookies = new CookieContainer();
                cookies.Add(new Cookie("PVEAuthCookie", authTicket, "/", uri.Host));
                websocket.Options.Cookies = cookies;
            }
        }
        else if (profile.AuthMode == AuthMode.ApiToken)
        {
            var secret = SecureStringHelper.ToPlainString(profile.ApiTokenSecret);
            websocket.Options.SetRequestHeader("Authorization", $"PVEAPIToken={profile.ApiTokenId}={secret}");
        }

        // API 와 같은 검증기 — 신뢰한 지문(TOFU)과 다른 인증서면 콘솔 연결도 차단
        var validator = api.CertificateValidator;
        websocket.Options.RemoteCertificateValidationCallback =
            (_, certificate, _, errors) => validator.Validate(certificate, errors);

        ApplyProxy(websocket, profile);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConnectTimeout);
        try
        {
            await websocket.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);
            return websocket;
        }
        catch (Exception ex)
        {
            websocket.Dispose(); // 타임아웃/거부 시 소켓 누수 방지
            if (validator.TryCreateTrustException(ex) is { } trust) throw trust;

            throw;
        }
    }

    /// <summary>
    ///     API 클라이언트(ConfigureProxy)와 같은 프록시 선택 — 직접 연결이면 시스템 프록시도 쓰지 않고(Proxy=null),
    ///     시스템이면 기본값을 그대로 두며, 지정 프록시는 같은 스키마(http·https·socks5)로 붙는다.
    /// </summary>
    internal static void ApplyProxy(ClientWebSocket websocket, ConnectionProfile profile)
    {
        switch (profile.ProxyMode)
        {
            case ProxyMode.None:
                websocket.Options.Proxy = null; // 기본값(시스템 프록시)으로 새지 않게
                return;
            case ProxyMode.System:
                return; // ClientWebSocket 기본값이 시스템 프록시
        }

        // 지정 프록시인데 주소가 비었으면 API 클라이언트처럼 프록시 없이 붙는다
        if (string.IsNullOrWhiteSpace(profile.ProxyHost) || profile.ProxyPort is not > 0)
        {
            websocket.Options.Proxy = null;
            return;
        }

        var scheme = profile.ProxyMode switch
        {
            ProxyMode.Https => "https",
            ProxyMode.Socks5 => "socks5",
            _ => "http"
        };
        websocket.Options.Proxy = ProxmoxApiClient.BuildProxy(scheme, profile);
    }
}