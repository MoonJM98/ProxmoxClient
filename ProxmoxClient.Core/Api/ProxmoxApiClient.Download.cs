using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Api;

/// <summary>JSON 이 아닌 파일 응답(백업 속 파일 등)을 스트림으로 받아 저장한다 — 메모리에 통째로 올리지 않는다.</summary>
public sealed partial class ProxmoxApiClient
{
    private const int DownloadBufferSize = 81920;

    /// <summary>받는 도중 이만큼 아무 데이터도 오지 않으면 끊긴 것으로 본다.</summary>
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     GET 응답 본문을 destination 에 그대로 쓴다. headerTimeout 은 응답이 시작될 때까지의 제한 시간이고, 받는
    ///     동안에는 <see cref="DownloadIdleTimeout" /> 넘게 데이터가 끊기면 멈춘다. progress 에는 받은 바이트 수를 알린다.
    /// </summary>
    internal async Task DownloadAsync(string relativePath, Stream destination, TimeSpan headerTimeout,
        IProgress<long>? progress, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, relativePath), true, ct,
            headerTimeout).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ProxmoxApiException((int)response.StatusCode, Excerpt(body));
        }

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[DownloadBufferSize];
            long total = 0;
            while (true)
            {
                idle.CancelAfter(DownloadIdleTimeout); // 조각을 받을 때마다 다시 잰다
                var read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                if (read == 0) break;

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                progress?.Report(total);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProxmoxApiException(0, null, Res.T("ProxmoxApiClient_10"));
        }
        catch (Exception ex) when (ex is HttpRequestException or HttpIOException)
        {
            throw new ProxmoxApiException((int)response.StatusCode, null, Res.T("ProxmoxApiClient_12", ex.Message));
        }
    }
}
