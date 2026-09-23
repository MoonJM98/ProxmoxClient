using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Views;

/// <summary>SPICE — virt-viewer 로 여는 대체 콘솔.</summary>
public partial class ConsoleWindow
{
    private async void OnOpenSpice(object sender, RoutedEventArgs e)
    {
        await TryOpenSpiceAsync();
    }
    private async Task TryOpenSpiceAsync()
    {
        // 1) SPICE 클라이언트 확인 — 없으면 설치할지 먼저 묻는다(SPICE 티켓은 짧게만 유효하므로 설치가 끝난 뒤 발급)
        var viewer = EnsureSpiceViewer();
        if (viewer is null) return;

        BtnSpice.IsEnabled = false;
        string? vvPath = null;
        try
        {
            SetState(Loc.T("ConsoleWindow_M15"));
            var spice = await _api.GetSpiceProxyAsync(_node, _vmid);

            // 비밀번호가 담긴 파일 — 호출마다 고유 이름(여러 콘솔 동시 실행 시 덮어쓰기 방지)
            vvPath = Path.Combine(Path.GetTempPath(), $"pve-spice-{_vmid}-{Guid.NewGuid():N}.vv");
            await File.WriteAllTextAsync(vvPath, spice.ToVvFile(_guestTitle));

            var startInfo = new ProcessStartInfo(viewer) { UseShellExecute = false };
            startInfo.ArgumentList.Add(vvPath); // 경로 따옴표·공백 처리를 런타임에 맡김
            var process = Process.Start(startInfo)
                          ?? throw new InvalidOperationException(Loc.T("ConsoleWindow_RemoteViewerFailed"));

            SetState(Loc.T("ConsoleWindow_M16", Path.GetFileName(viewer)));
            _ = WatchSpiceViewerAsync(process, vvPath);
            vvPath = null; // 정리는 감시 작업이 맡는다
        }
        catch (ProxmoxApiException ex)
        {
            SetState(Loc.T("ConsoleWindow_M17", ex.Message));
        }
        catch (Exception ex) when (ex is CertificateTrustException or IOException or UnauthorizedAccessException
                                       or Win32Exception or InvalidOperationException)
        {
            SetState(Loc.T("ConsoleWindow_M18", ex.Message));
        }
        finally
        {
            if (vvPath is not null) TryDeleteFile(vvPath); // 실행 전에 실패 — 비밀번호 파일을 남기지 않는다

            BtnSpice.IsEnabled = true;
        }
    }
    /// <summary>remote-viewer 경로. 없으면 설치 여부를 묻고 설치 창을 연다. 끝내 없으면 null(상태 표시).</summary>
    private string? EnsureSpiceViewer()
    {
        var viewer = RemoteViewerLocator.Detect();
        if (viewer is not null) return viewer;

        var answer = ThemedMessageBox.Show(this,
            Loc.T("ConsoleWindow_M19"),
            Loc.T("ConsoleWindow_M20"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            SetState(Loc.T("ConsoleWindow_M21"));
            return null;
        }

        new VirtViewerSetupWindow { Owner = this }.ShowDialog();
        viewer = RemoteViewerLocator.Detect();
        if (viewer is null) SetState(Loc.T("ConsoleWindow_M22"));

        return viewer;
    }
    /// <summary>remote-viewer 가 곧바로 종료되면(접속 실패·파일 오류) 알리고, 남은 .vv(비밀번호 포함)를 정리한다.</summary>
    private async Task WatchSpiceViewerAsync(Process process, string vvPath)
    {
        using (process)
        {
            using var earlyExit = new CancellationTokenSource(SpiceEarlyExitWindow);
            try
            {
                await process.WaitForExitAsync(earlyExit.Token);
                if (!_closed && process.ExitCode != 0) SetState(Loc.T("ConsoleWindow_M23", process.ExitCode));
            }
            catch (OperationCanceledException)
            {
                // 계속 실행 중 — 정상
            }
        }

        await Task.Delay(SpiceFileCleanupDelay);
        TryDeleteFile(vvPath); // 정상이면 remote-viewer 가 이미 지웠다(delete-this-file)
    }
    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log($"[SPICE] 임시 연결 파일 삭제 실패: {ex.Message}");
        }
    }
}
