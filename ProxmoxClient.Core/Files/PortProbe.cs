using System.Net.Sockets;

namespace ProxmoxClient.Core.Files;

/// <summary>PC 에서 게스트의 파일 서비스 포트(SSH·SMB·FTP)에 닿는지 — 연결 방식을 추천하는 데만 쓴다.</summary>
public static class PortProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(900);

    /// <summary>열려 있는(TCP 연결이 되는) 포트 — 모두 동시에, 시간 안에 답이 없으면 닫힌 것으로 본다.</summary>
    public static async Task<IReadOnlySet<int>> OpenPortsAsync(string host, IEnumerable<int> ports, TimeSpan timeout,
        CancellationToken ct = default)
    {
        var checks = ports.Distinct().Select(async port => (port, open: await IsOpenAsync(host, port, timeout, ct)
            .ConfigureAwait(false)));
        var results = await Task.WhenAll(checks).ConfigureAwait(false);
        return results.Where(r => r.open).Select(r => r.port).ToHashSet();
    }

    public static async Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }
}
