using System.Text;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     앱 기록(로그) 파일 — 처리하지 못한 예외(앱이 죽는 원인)와 플랫폼 경고를 남긴다.
///     app.log 가 1MB 를 넘으면 app.1.log 로 돌리고 새로 쓴다(최대 약 2MB). 설정 화면에서 보거나 파일로 저장한다.
///     기록에 실패해도 앱 동작에는 영향이 없다(조용히 넘어간다).
/// </summary>
public static class AppLog
{
    /// <summary>파일 하나의 최대 크기 — 넘으면 이전 파일(app.1.log)로 돌린다.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _directory;
    private static bool _installed;

    /// <summary>
    ///     기록 폴더. 헤드가 바꿔 넣을 수 있다(Android = 앱 전용 FilesDir/logs).
    ///     기본은 로컬 앱 데이터 폴더 아래 ProxmoxClient/logs.
    /// </summary>
    public static string Directory
    {
        get => _directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxmoxClient", "logs");
        set => _directory = value;
    }

    public static string CurrentPath => Path.Combine(Directory, "app.log");

    public static string PreviousPath => Path.Combine(Directory, "app.1.log");

    /// <summary>
    ///     처리하지 못한 예외를 기록한다. 앱이 죽는 예외는 기록만 하고 그대로 죽게 둔다(숨기지 않는다).
    ///     관찰되지 않은 Task 예외는 기록하고 관찰된 것으로 표시한다(.NET 기본도 앱을 죽이지 않는다).
    ///     UI 스레드 예외는 <see cref="LogDispatcherException" /> 를 App 이 연결한다.
    /// </summary>
    public static void InstallGlobalHandlers()
    {
        if (_installed) return;

        _installed = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Error(e.IsTerminating ? "Unhandled (fatal)" : "Unhandled", e.ExceptionObject as Exception,
                e.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Error("UnobservedTask", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>UI 스레드에서 처리하지 못한 예외 — 기록만 한다(Handled 는 건드리지 않는다).</summary>
    public static void LogDispatcherException(Exception exception)
    {
        Error("UI thread", exception);
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Warn(string message)
    {
        Write("WARN", message);
    }

    public static void Error(string context, Exception? exception, string? fallback = null)
    {
        Write("ERROR", exception is null ? $"{context}: {fallback}" : $"{context}: {exception}");
    }

    /// <summary>이전 파일과 지금 파일을 이어 붙인 전체 기록(오래된 것부터). 없으면 빈 문자열.</summary>
    public static string ReadAll()
    {
        lock (Gate)
        {
            var text = new StringBuilder();
            foreach (var path in new[] { PreviousPath, CurrentPath })
                try
                {
                    if (File.Exists(path)) text.Append(File.ReadAllText(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 읽지 못한 파일은 건너뛴다
                }

            return text.ToString();
        }
    }

    /// <summary>끝에서 maxChars 글자만(화면에 보일 만큼).</summary>
    public static string ReadTail(int maxChars)
    {
        var all = ReadAll();
        return all.Length <= maxChars ? all : "…\n" + all[^maxChars..];
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
        System.Diagnostics.Trace.WriteLine(line.TrimEnd());
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                Rotate(CurrentPath, PreviousPath, MaxFileBytes);
                File.AppendAllText(CurrentPath, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 기록하지 못해도 앱은 계속 간다
            }
        }
    }

    /// <summary>지금 파일이 limit 이상이면 이전 파일로 옮긴다(이전 파일은 버린다).</summary>
    internal static void Rotate(string current, string previous, long limit)
    {
        var info = new FileInfo(current);
        if (!info.Exists || info.Length < limit) return;

        File.Move(current, previous, true);
    }
}
