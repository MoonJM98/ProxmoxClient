using System.Net.Http;
using System.Net.Sockets;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Mobile.Localization;

namespace ProxmoxClient.Mobile.Services;

/// <summary>예외를 사용자에게 보여 줄 한두 줄로 바꾼다.</summary>
public static class ErrorText
{
    public static string Describe(Exception ex)
    {
        return ex switch
        {
            ProxmoxApiException api => api.Message,
            CertificateTrustException trust => trust.Message,
            HttpRequestException { InnerException: SocketException socket } =>
                L.T("Error_Unreachable", socket.Message),
            HttpRequestException http => L.T("Error_Unreachable", http.Message),
            TimeoutException or TaskCanceledException => L.T("Error_Timeout"),
            _ => ex.Message
        };
    }
}
