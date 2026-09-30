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
        var uri = new UriBuilder("wss", api.Profile.Host, api.Profile.Port) // ClientWebSocket 은 ws/wss 스키마만 허용
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
        var uri = new UriBuilder("wss", api.Profile.Host, api.Profile.Port)
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
        if (api.AuthTicket is { } authTicket)
        {
            var cookies = new CookieContainer();
            cookies.Add(new Cookie("PVEAuthCookie", authTicket, "/", uri.Host));
            websocket.Options.Cookies = cookies;
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

    private static void ApplyProxy(ClientWebSocket websocket, ConnectionProfile profile)
    {
        if (profile.ProxyMode == ProxyMode.None
            || string.IsNullOrWhiteSpace(profile.ProxyHost)
            || profile.ProxyPort is not > 0)
            return;

        var scheme = profile.ProxyMode == ProxyMode.Socks5 ? "socks5" : "http"; // 웹소켓 프록시는 http CONNECT 사용
        var proxy = new WebProxy($"{scheme}://{profile.ProxyHost}:{profile.ProxyPort!.Value}");
        if (!string.IsNullOrEmpty(profile.ProxyUserName))
            proxy.Credentials = new NetworkCredential(profile.ProxyUserName, profile.ProxyPassword ?? string.Empty);

        websocket.Options.Proxy = proxy;
    }
}