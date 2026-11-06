using ProxmoxClient.UI.Platform;

/// <summary>앱 기록 파일 돌리기·끊긴 터미널 목록 남기기(휴대폰·태블릿) 검사.</summary>
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

    public static void OrphanedTerminalsArePersistedOnTouch(Checker c)
    {
        var path = Path.Combine(Path.GetTempPath(), "orphans-" + Guid.NewGuid().ToString("N"), "open-terminals.txt");
        var savedPath = SessionKeepAlive.StatePath;
        var savedForm = PlatformInfo.FormFactor;
        var a = new object();
        var b = new object();
        try
        {
            SessionKeepAlive.StatePath = path;
            PlatformInfo.FormFactor = FormFactor.Mobile;
            SessionKeepAlive.Acquire(a, "100 · ct");
            SessionKeepAlive.Acquire(b, "pve · Shell");
            c.Equal("100 · ct|pve · Shell", string.Join("|", File.ReadAllLines(path)), "열린 터미널 기록");
            SessionKeepAlive.Release(a);
            c.Equal("pve · Shell", string.Join("|", File.ReadAllLines(path)), "닫으면 빠진다");

            // 앱이 죽은 뒤 다음 실행 — 읽으면 지운다
            c.Equal("pve · Shell", string.Join("|", SessionKeepAlive.TakeOrphanedSessions()), "지난 실행의 터미널");
            c.Is(!File.Exists(path), "읽은 뒤 지운다");
            c.Equal(0, SessionKeepAlive.TakeOrphanedSessions().Count, "두 번째는 없다");

            SessionKeepAlive.Acquire(b, "pve · Shell");
            SessionKeepAlive.Release(b);
            c.Is(!File.Exists(path), "모두 닫으면 파일이 없다");

            // 데스크톱은 창을 닫는 것이 정상 종료 — 남기지 않는다
            PlatformInfo.FormFactor = FormFactor.Desktop;
            SessionKeepAlive.Acquire(a, "desk");
            c.Is(!File.Exists(path), "데스크톱은 남기지 않는다");
            SessionKeepAlive.Release(a);
        }
        finally
        {
            SessionKeepAlive.Release(a);
            SessionKeepAlive.Release(b);
            SessionKeepAlive.StatePath = savedPath;
            PlatformInfo.FormFactor = savedForm;
            if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
