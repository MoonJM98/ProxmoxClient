using System.ComponentModel;
using System.Diagnostics;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     데스크톱(Windows · Linux · macOS) SPICE 실행기 — remote-viewer 를 .vv 파일 인자로 띄운다(WPF SpiceLauncher 와 같은 규칙).
///     연결 파일은 호출마다 고유 이름의 임시 파일(나만 읽기, 0600)이며, 클라이언트가 지우지 못했으면 나중에 지운다
///     (비밀번호가 담겨 있다). 지난 실행이 남긴 파일은 실행 후 첫 사용 때 지운다.
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
            CleanupLeftovers();
            vvPath = Path.Combine(Path.GetTempPath(), $"{FilePrefix}{Guid.NewGuid():N}.vv");
            await WritePrivateAsync(vvPath, vvContent);

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

    /// <summary>
    ///     .vv(비밀번호 포함)를 나만 읽을 수 있게 새로 만든다 — Linux 의 /tmp 는 모두가 함께 쓰므로 기본 권한(0644)이면
    ///     다른 사용자가 읽는다. 있던 파일·링크를 따라가지 않도록 새 파일만 만든다(CreateNew).
    /// </summary>
    private static async Task WritePrivateAsync(string path, string content)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
    }

    private const string FilePrefix = "pve-spice-";

    /// <summary>이보다 오래된 .vv 만 지운다 — 다른 창·인스턴스가 방금 띄운 remote-viewer 가 아직 읽지 않았을 수 있다.</summary>
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(2);

    private static int _cleanedUp;

    /// <summary>
    ///     지난 실행이 지우지 못하고 남긴 .vv(앱이 정리 전에 끝남)를 지운다 — 실행마다 한 번. 다른 사용자의 파일은
    ///     권한이 없어 지워지지 않는다.
    /// </summary>
    internal static void CleanupLeftovers()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) == 1) return;

        try
        {
            var cutoff = DateTime.UtcNow - LeftoverAge;
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), FilePrefix + "*.vv"))
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    TryDeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 임시 폴더를 읽지 못했다 — 다음 실행 때 다시 해 본다
        }
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
