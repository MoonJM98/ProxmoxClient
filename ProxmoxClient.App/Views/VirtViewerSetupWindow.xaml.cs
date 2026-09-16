using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;

namespace ProxmoxClient.App.Views;

public partial class VirtViewerSetupWindow : Window
{
    private const string ReleasesUrl = "https://releases.pagure.org/virt-viewer/";

    private const int DownloadBufferSize = 128 * 1024;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly Regex MsiLinkRegex =
        new(@"href=""(virt-viewer-x64-(?<ver>[\d.\-]+)\.msi)""", RegexOptions.Compiled);

    /// <summary>창을 닫으면 진행 중인 목록 조회·다운로드·설치 대기를 취소한다.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    private bool _busy;

    public VirtViewerSetupWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        RefreshStatus();
        Closed += (_, _) =>
        {
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
        };
    }

    /// <summary>창이 닫힐 시점에 remote-viewer가 감지되어 있으면 true.</summary>
    public bool InstalledDetected { get; private set; }

    private void RefreshStatus()
    {
        var path = RemoteViewerLocator.Detect();
        InstalledDetected = path is not null;
        StatusText.Text = path is null
            ? Loc.T("VirtViewerSetupWindow_M01")
            : Loc.T("VirtViewerSetupWindow_M02", path);
        BtnInstall.IsEnabled = path is null;
    }

    private async void OnAutoInstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        BtnInstall.IsEnabled = false;
        BtnPage.IsEnabled = false;
        ShowProgress(Loc.T("VirtViewer_FetchingList"));
        var ct = _lifetimeCts.Token;
        try
        {
            var msiName = await FindLatestMsiAsync(ct);
            if (msiName is null)
            {
                SetProgressError(Loc.T("VirtViewerSetupWindow_M03"));
                return;
            }

            var path = Path.Combine(Path.GetTempPath(), msiName);
            await DownloadAsync(new Uri(new Uri(ReleasesUrl), msiName), path, ct);

            SetState(Loc.T("VirtViewerSetupWindow_M04", msiName));
            HideProgress();
            using var installer = Process.Start(
                new ProcessStartInfo(path) { UseShellExecute = true });
            if (installer is null)
            {
                SetState(Loc.T("VirtViewerSetupWindow_M05"));
                return;
            }

            await installer.WaitForExitAsync(ct);
            RefreshStatus();
            if (InstalledDetected)
            {
                SetState(Loc.T("VirtViewerSetupWindow_M06"));
                Close(); // 호출한 콘솔 창이 곧바로 연결을 진행
                return;
            }

            SetState(installer.ExitCode == 0
                ? Loc.T("VirtViewerSetupWindow_M07")
                : Loc.T("VirtViewerSetupWindow_M08", installer.ExitCode));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 창을 닫아 취소
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or Win32Exception or OperationCanceledException)
        {
            SetProgressError(Loc.T("VirtViewerSetupWindow_M09", ex.Message));
        }
        finally
        {
            _busy = false;
            if (!ct.IsCancellationRequested)
            {
                BtnPage.IsEnabled = true;
                BtnRescan.IsEnabled = true;
                BtnInstall.IsEnabled = !InstalledDetected; // 실패 후 다시 시도할 수 있게
            }
        }
    }

    private void OnOpenPage(object sender, RoutedEventArgs e)
    {
        Process.Start(
            new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
    }

    private void OnRescan(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        if (InstalledDetected) SetState(Loc.T("VirtViewerSetupWindow_M10"));
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        Close();
    }

    private async Task<string?> FindLatestMsiAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var listing = await Http.GetStringAsync(ReleasesUrl, cts.Token);

        string? best = null;
        int[]? bestVersion = null;
        foreach (Match match in MsiLinkRegex.Matches(listing))
        {
            var version = ParseVersion(match.Groups["ver"].Value);
            if (bestVersion is null || CompareVersions(version, bestVersion) > 0)
            {
                bestVersion = version;
                best = match.Groups[1].Value;
            }
        }

        return best;
    }

    private static int[] ParseVersion(string text)
    {
        return Regex.Matches(text, @"\d+").Select(m => int.Parse(m.Value)).ToArray();
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

    /// <summary>임시 파일로 받은 뒤 완료되면 이름을 바꾼다 — 중단·실패 시 불완전한 설치 파일을 남기거나 실행하지 않는다.</summary>
    private async Task DownloadAsync(Uri uri, string filePath, CancellationToken ct)
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
                        ReportProgress(read, total);
                    }
                }
            }

            ReportProgress(read, total);
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
        }
    }

    private void ReportProgress(long read, long total)
    {
        var (downloaded, sizeText) = total > 0
            ? ($"{read / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0:F1} MB", (double)read / total * 100)
            : ($"{read / 1024.0 / 1024.0:F1} MB", 0.0);

        Dispatcher.BeginInvoke(() =>
        {
            DownloadProgress.Value = sizeText;
            ProgressText.Text = Loc.T("VirtViewer_Downloading", downloaded);
        });
    }

    private void ShowProgress(string text)
    {
        DownloadProgress.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;
        ProgressText.Text = text;
        DownloadProgress.IsIndeterminate = true;
        SetState(Loc.T("VirtViewerSetupWindow_M11"));
    }

    private void HideProgress()
    {
        DownloadProgress.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
        DownloadProgress.IsIndeterminate = false;
    }

    private void SetProgressError(string text)
    {
        DownloadProgress.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Visible;
        ProgressText.Text = text;
    }

    private void SetState(string text)
    {
        StatusText.Text = text;
    }
}