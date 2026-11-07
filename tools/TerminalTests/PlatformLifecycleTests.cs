using ProxmoxClient.UI.Platform;

/// <summary>앱 기록 파일 돌리기 검사(끊긴 터미널 목록은 TerminalRestoreTests).</summary>
public static class PlatformLifecycleTests
{
    public static void AppLogRotatesAtLimit(Checker c)
    {
        var dir = Path.Combine(Path.GetTempPath(), "applog-" + Guid.NewGuid().ToString("N"));
        var saved = AppLog.Directory;
        try
        {
            AppLog.Directory = dir;
            AppLog.Info("first");
            c.Is(File.Exists(AppLog.CurrentPath), "기록 파일이 생긴다");
            c.Is(AppLog.ReadAll().Contains("[INFO] first"), "기록 내용", AppLog.ReadAll());

            // 한도를 넘긴 파일은 이전 파일로 돌리고 새로 쓴다
            File.WriteAllText(AppLog.CurrentPath, new string('x', (int)AppLog.MaxFileBytes));
            AppLog.Error("crash", new InvalidOperationException("boom"));
            c.Is(File.Exists(AppLog.PreviousPath), "이전 파일로 돌림");
            c.Is(new FileInfo(AppLog.CurrentPath).Length < 4096, "새 파일은 작다", new FileInfo(AppLog.CurrentPath).Length);
            var all = AppLog.ReadAll();
            c.Is(all.IndexOf("xxxx", StringComparison.Ordinal) < all.IndexOf("boom", StringComparison.Ordinal),
                "오래된 것부터 이어 붙인다");
            c.Is(AppLog.ReadTail(100).Length <= 102, "끝부분만", AppLog.ReadTail(100).Length);

            // 한 번 더 넘기면 가장 오래된 파일은 버린다(최대 두 개)
            File.WriteAllText(AppLog.CurrentPath, new string('y', (int)AppLog.MaxFileBytes));
            AppLog.Warn("again");
            c.Is(!AppLog.ReadAll().Contains("xxxx"), "가장 오래된 파일은 버림");
            c.Equal(2, Directory.GetFiles(dir).Length, "파일 두 개");
        }
        finally
        {
            AppLog.Directory = saved;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
