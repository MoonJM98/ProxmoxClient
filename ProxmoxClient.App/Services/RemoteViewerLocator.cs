using System.IO;
using System.Text.RegularExpressions;

namespace ProxmoxClient.App.Services;

/// <summary>remote-viewer(VirtViewer) 실행 파일 탐지.</summary>
public static partial class RemoteViewerLocator
{
    private const string ExeName = "remote-viewer.exe";

    /// <summary>
    ///     remote-viewer.exe 경로 탐지: PROXMOXCLIENT_REMOTEVIEWER 환경변수 →
    ///     Program Files\VirtViewer*\bin(버전이 가장 높은 설치) → PATH. 없으면 null.
    /// </summary>
    public static string? Detect()
    {
        var fromEnv = Environment.GetEnvironmentVariable("PROXMOXCLIENT_REMOTEVIEWER");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        return FromProgramFiles() ?? FromPath();
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
                // "VirtViewer v11.0-256" 과 "VirtViewer v9.0" 을 문자열로 정렬하면 v9 가 앞서므로 숫자 버전으로 비교
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

    private static string? FromPath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            try
            {
                var probe = Path.Combine(dir, ExeName);
                if (File.Exists(probe)) return probe;
            }
            catch (ArgumentException)
            {
                // PATH 에 잘못된 문자가 든 항목
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