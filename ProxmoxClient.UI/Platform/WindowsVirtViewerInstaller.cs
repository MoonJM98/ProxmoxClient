using System.Buffers;
using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     Windows 용 VirtViewer 자동 설치 — 공식 배포처에서 최신 x64 MSI 를 받아 실행한다(WPF VirtViewerSetupWindow 와 같다).
///     받는 중에는 .part 파일로 두고, 다 받은 뒤에만 이름을 바꾼다(불완전한 설치 파일을 실행하지 않는다).
/// </summary>
internal sealed class WindowsVirtViewerInstaller : ISpiceViewerInstaller
{
    private const string ReleasesUrl = "https://releases.pagure.org/virt-viewer/";
    private const int DownloadBufferSize = 128 * 1024;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly Regex MsiLinkRegex =
        new(@"href=""(virt-viewer-x64-(?<ver>[\d.\-]+)\.msi)""", RegexOptions.Compiled);

    public async Task<int> InstallAsync(Action<string> state, Action<long, long> progress, CancellationToken ct)
    {
        state(Loc.T("VirtViewer_FetchingList"));
        var msiName = await FindLatestMsiAsync(ct)
                      ?? throw new IOException(Loc.T("VirtViewerSetupWindow_M03"));

        var path = Path.Combine(Path.GetTempPath(), msiName);
        state(Loc.T("VirtViewerSetupWindow_M11"));
        await DownloadAsync(new Uri(new Uri(ReleasesUrl), msiName), path, progress, ct);

        state(Loc.T("VirtViewerSetupWindow_M04", msiName));
        using var installer = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        if (installer is null) return -1;

        await installer.WaitForExitAsync(ct);
        return installer.ExitCode;
    }

    private static async Task<string?> FindLatestMsiAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var listing = await Http.GetStringAsync(ReleasesUrl, cts.Token);

        string? best = null;
        int[]? bestVersion = null;
        foreach (Match match in MsiLinkRegex.Matches(listing))
        {
            var version = Regex.Matches(match.Groups["ver"].Value, @"\d+").Select(m => int.Parse(m.Value)).ToArray();
            if (bestVersion is null || CompareVersions(version, bestVersion) > 0)
            {
                bestVersion = version;
                best = match.Groups[1].Value;
            }
        }

        return best;
    }

    private static int CompareVersions(int[] a, int[] b)
    {
        var n = Math.Max(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var left = i < a.Length ? a[i] : 0;
            var right = i < b.Length ? b[i] : 0;
            if (left != right) return left.CompareTo(right);
        }

        return 0;
    }

    private static async Task DownloadAsync(Uri uri, string filePath, Action<long, long> progress, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        var partialPath = filePath + ".part";
        var buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1;
            long read = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cts.Token))
            await using (var target = File.Create(partialPath))
            {
                int n;
                var lastUpdate = DateTime.MinValue;
                while ((n = await source.ReadAsync(buffer.AsMemory(0, DownloadBufferSize), cts.Token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), cts.Token);
                    read += n;

                    var now = DateTime.UtcNow;
                    if (now - lastUpdate > TimeSpan.FromMilliseconds(100))
                    {
                        lastUpdate = now;
                        progress(read, total);
                    }
                }
            }

            progress(read, total);
            if (read == 0 || (total > 0 && read != total))
                throw new IOException(Loc.T("VirtViewer_IncompleteDownload", read, total));

            File.Move(partialPath, filePath, true);
        }
        catch
        {
            TryDeletePartial(partialPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDeletePartial(string path)
    {
        try
        {
            File.Delete(path); // 이 다운로드가 만든 .part 파일만 정리
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 정리 실패는 무시한다(다음 다운로드가 덮어쓴다)
        }
    }
}
