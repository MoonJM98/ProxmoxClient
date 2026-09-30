using System.Diagnostics;
using System.Net;
using System.Text;
using System.Web;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Services;

/// <summary>
///     OpenID 로그인 — 이 PC 의 localhost 주소에서 잠깐 기다리며 기본 브라우저로 IdP 로그인 창을 연다. IdP 가 그 주소로
///     돌려준 code·state 를 받아 서버에 넘기면 티켓이 나온다. 리디렉션 주소는 IdP 의 허용 목록에 있어야 한다.
/// </summary>
internal static class OpenIdLogin
{
    /// <summary>로그인 뒤 IdP 가 돌아올 이 PC 의 포트 — IdP 에 http://localhost:8766/ 을 등록한다.</summary>
    public const int RedirectPort = 8766;

    /// <summary>사용자가 브라우저에서 로그인을 마칠 때까지 기다리는 최대 시간.</summary>
    private static readonly TimeSpan LoginWait = TimeSpan.FromMinutes(5);

    public static string RedirectUrl => $"http://localhost:{RedirectPort}/";

    /// <summary>브라우저로 로그인하고 티켓을 받는다 — 성공하면 client 는 로그인된 상태, 돌려주는 값은 서버가 정한 아이디.</summary>
    public static async Task<string> RunAsync(ProxmoxApiClient client, string realm, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUrl);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(Loc.T("OpenId_PortBusy", RedirectPort, ex.Message), ex);
        }

        var authUrl = await client.GetOpenIdAuthUrlAsync(realm, RedirectUrl, ct);
        if (!Uri.TryCreate(authUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidOperationException(Loc.T("OpenId_BadUrl", authUrl));
        // 이번 로그인의 state — 다른 프로그램·웹 페이지가 보낸 가짜 응답(다른 계정으로 로그인시키기)을 거른다
        var expectedState = HttpUtility.ParseQueryString(uri.Query)["state"];
        if (string.IsNullOrEmpty(expectedState)) throw new InvalidOperationException(Loc.T("OpenId_BadUrl", authUrl));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

        var (code, state) = await WaitForCallbackAsync(listener, expectedState, ct);
        return await client.CompleteOpenIdLoginAsync(code, state, RedirectUrl, ct);
    }

    /// <summary>
    ///     IdP 가 돌려준 요청을 기다린다 — 이 PC 에서 온, 이번 로그인의 state 가 붙은 요청만 받는다(파비콘·가짜 요청은 무시).
    /// </summary>
    private static async Task<(string Code, string State)> WaitForCallbackAsync(HttpListener listener,
        string expectedState, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LoginWait);
        await using var stop = timeout.Token.Register(listener.Stop);
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(Loc.T("OpenId_Cancelled"), ex, ct);
                throw new TimeoutException(Loc.T("OpenId_TimedOut"), ex);
            }

            var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
            if (!context.Request.IsLocal || query["state"] != expectedState)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }

            if (query["error"] is { Length: > 0 } error)
            {
                await RespondAsync(context, Loc.T("OpenId_PageFailed"));
                throw new InvalidOperationException(Loc.T("OpenId_IdpError", error, query["error_description"]));
            }

            if (query["code"] is { Length: > 0 } code)
            {
                await RespondAsync(context, Loc.T("OpenId_PageDone"));
                return (code, expectedState);
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, string message)
    {
        var html = $"<!doctype html><meta charset=\"utf-8\"><title>Proxmox</title>"
                   + $"<p style=\"font-family:sans-serif\">{WebUtility.HtmlEncode(message)}</p>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
}
