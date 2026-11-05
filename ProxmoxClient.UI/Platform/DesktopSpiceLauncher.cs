using System.ComponentModel;
using System.Diagnostics;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     데스크톱(Windows · Linux · macOS) SPICE 실행기 — remote-viewer 를 .vv 파일 인자로 띄운다(WPF SpiceLauncher 와 같은 규칙).
///     연결 파일은 호출마다 고유 이름의 임시 파일이며, 클라이언트가 지우지 못했으면 나중에 지운다(비밀번호가 담겨 있다).
/// </summary>
internal sealed class DesktopSpiceLauncher(
    Func<string?> locate,
    SpiceSetupGuide guide,
    ISpiceViewerInstaller? installer) : ISpiceLauncher
{
    /// <summary>remote-viewer 가 이 시간 안에 끝나면 접속 실패로 본다.</summary>
    private static readonly TimeSpan EarlyExitWindow = TimeSpan.FromSeconds(5);

    /// <summary>remote-viewer 가 .vv 를 지우지 못했을 때 대신 지우기까지 기다리는 시간.</summary>
    private static readonly TimeSpan FileCleanupDelay = TimeSpan.FromSeconds(15);

    public SpiceSetupGuide Guide => guide;

    public ISpiceViewerInstaller? Installer => installer;

    public SpiceViewerStatus Probe()
    {
        var path = locate();
        return new SpiceViewerStatus(path is not null, path);
    }

    public async Task<SpiceOpenResult> OpenAsync(string vvContent, string title, Action<string> report)
    {
        var viewer = locate();
        if (viewer is null) return new SpiceOpenResult(SpiceOpenState.ViewerMissing);

        string? vvPath = null;
        try
        {
            vvPath = Path.Combine(Path.GetTempPath(), $"pve-spice-{Guid.NewGuid():N}.vv");
            await File.WriteAllTextAsync(vvPath, vvContent);

            var startInfo = new ProcessStartInfo(viewer) { UseShellExecute = false };
            startInfo.ArgumentList.Add(vvPath); // 경로 따옴표·공백 처리를 런타임에 맡김
            var process = Process.Start(startInfo)
                          ?? throw new InvalidOperationException(Loc.T("ConsoleWindow_RemoteViewerFailed"));

            report(Loc.T("ConsoleWindow_M16", Path.GetFileName(viewer)));
            _ = WatchAsync(process, vvPath, report);
            vvPath = null; // 정리는 감시 작업이 맡는다
            return new SpiceOpenResult(SpiceOpenState.Started);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
                                       or InvalidOperationException)
        {
            return new SpiceOpenResult(SpiceOpenState.Failed, Loc.T("ConsoleWindow_M18", ex.Message));
        }
        finally
        {
            if (vvPath is not null) TryDeleteFile(vvPath); // 실행 전에 실패 — 비밀번호 파일을 남기지 않는다
        }
    }

    /// <summary>remote-viewer 가 곧바로 종료되면(접속 실패·파일 오류) 알리고, 남은 .vv 를 정리한다.</summary>
    private static async Task WatchAsync(Process process, string vvPath, Action<string> report)
    {
        using (process)
        {
            using var earlyExit = new CancellationTokenSource(EarlyExitWindow);
            try
            {
                await process.WaitForExitAsync(earlyExit.Token);
                if (process.ExitCode != 0) report(Loc.T("ConsoleWindow_M23", process.ExitCode));
            }
            catch (OperationCanceledException)
            {
                // 계속 실행 중 — 정상
            }
        }

        await Task.Delay(FileCleanupDelay);
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
            // 임시 파일 — 다음 실행 때 다시 시도하지 않는다
        }
    }
}
