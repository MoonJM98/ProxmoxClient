using System.Text.RegularExpressions;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     remote-viewer(VirtViewer) 실행 파일 탐지 — 운영체제마다 다르다. 셋 다 없으면 null(설치 안내를 보인다).
///     <see cref="SpiceLaunchers" /> 가 운영체제에 맞는 것을 고른다.
/// </summary>
public static class RemoteViewerLocators
{
    /// <summary>PATH 에서 실행 파일 이름 찾기(Windows 는 확장자가 붙은 이름을 넘긴다).</summary>
    internal static string? FindOnPath(string fileName, char separator)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            try
            {
                var probe = Path.Combine(dir, fileName);
                if (File.Exists(probe)) return probe;
            }
            catch (ArgumentException)
            {
                // PATH 에 잘못된 문자가 든 항목
            }

        return null;
    }
}

/// <summary>
///     Windows: PROXMOXCLIENT_REMOTEVIEWER 환경변수 → Program Files\VirtViewer*\bin(버전이 가장 높은 설치) → PATH.
///     WPF 앱의 RemoteViewerLocator 와 같은 순서.
/// </summary>
public static partial class WindowsRemoteViewerLocator
{
    private const string ExeName = "remote-viewer.exe";

    public static string? Detect()
    {
        var fromEnv = Environment.GetEnvironmentVariable("PROXMOXCLIENT_REMOTEVIEWER");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        return FromProgramFiles() ?? RemoteViewerLocators.FindOnPath(ExeName, ';');
    }

    private static string? FromProgramFiles()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in roots.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            try
            {
                // "VirtViewer v11.0-256" 과 "VirtViewer v9.0" 은 문자열로 정렬하면 v9 가 앞서므로 숫자 버전으로 비교
                var dirs = Directory.GetDirectories(root, "VirtViewer*")
                    .OrderByDescending(VersionOf, VersionComparer.Instance);
                foreach (var dir in dirs)
                {
                    var exe = Path.Combine(dir, "bin", ExeName);
                    if (File.Exists(exe)) return exe;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 접근할 수 없는 폴더는 건너뜀
            }

        return null;
    }

    private static int[] VersionOf(string directory)
    {
        return NumberRegex().Matches(Path.GetFileName(directory))
            .Select(m => int.TryParse(m.Value, out var value) ? value : 0)
            .ToArray();
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();

    private sealed class VersionComparer : IComparer<int[]>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(int[]? x, int[]? y)
        {
            x ??= [];
            y ??= [];
            for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var left = i < x.Length ? x[i] : 0;
                var right = i < y.Length ? y[i] : 0;
                if (left != right) return left.CompareTo(right);
            }

            return 0;
        }
    }
}

/// <summary>macOS: /Applications/RemoteViewer.app 의 실행 파일 → Homebrew(/opt/homebrew, /usr/local) → PATH.</summary>
public static class MacRemoteViewerLocator
{
    private const string AppBundle = "/Applications/RemoteViewer.app/Contents/MacOS";

    public static string? Detect()
    {
        if (Directory.Exists(AppBundle))
        {
            foreach (var name in new[] { "remote-viewer", "RemoteViewer" })
            {
                var exe = Path.Combine(AppBundle, name);
                if (File.Exists(exe)) return exe;
            }

            // 번들 실행 파일 이름이 다를 수 있다 — 그 폴더의 첫 파일
            try
            {
                var first = Directory.GetFiles(AppBundle).FirstOrDefault();
                if (first is not null) return first;
            }
            catch (IOException)
            {
                // 읽을 수 없으면 다음 후보로
            }
        }

        foreach (var brew in new[] { "/opt/homebrew/bin/remote-viewer", "/usr/local/bin/remote-viewer" })
            if (File.Exists(brew)) return brew;

        return RemoteViewerLocators.FindOnPath("remote-viewer", ':');
    }
}

/// <summary>Linux 와 그 밖의 데스크톱: PATH 의 remote-viewer(배포판 패키지 virt-viewer 가 넣는다).</summary>
public static class LinuxRemoteViewerLocator
{
    public static string? Detect()
    {
        return RemoteViewerLocators.FindOnPath("remote-viewer", ':')
               ?? (File.Exists("/usr/bin/remote-viewer") ? "/usr/bin/remote-viewer" : null);
    }
}
