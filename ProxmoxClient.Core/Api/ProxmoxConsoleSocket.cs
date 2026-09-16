using System.Net;
using System.Net.WebSockets;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     vncproxy/termproxy 가 연 포트로 붙는 Proxmox 콘솔 웹소켓(vncwebsocket) 연결 — VNC 와 CT 터미널이 공유한다.
/// </summary>
internal static class ProxmoxConsoleSocket
{
    /// <summary>프록시는 생성 후 짧은 시간만 연결을 기다리므로 그 안에 붙어야 한다.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(25);

    public static async Task<ClientWebSocket> ConnectAsync(
        ProxmoxApiClient api, string node, ResourceKind kind, int vmid, int port, string ticket, CancellationToken ct)
    {
        var profile = api.Profile;
        var uri = new UriBuilder("wss", profile.Host, profile.Port) // ClientWebSocket 은 ws/wss 스키마만 허용
        {
            Path = $"/api2/json/nodes/{Uri.EscapeDataString(node)}/{kind.ApiSegment()}/{vmid}/vncwebsocket",
            Query = $"port={port}&vncticket={Uri.EscapeDataString(ticket)}"
        }.Uri;

        var websocket = new ClientWebSocket();
        websocket.Options.AddSubProtocol("binary");
        if (api.AuthTicket is { } authTicket)
        {
            var cookies = new CookieContainer();
            cookies.Add(new Cookie("PVEAuthCookie", authTicket, "/", uri.Host));
            websocket.Options.Cookies = cookies;
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