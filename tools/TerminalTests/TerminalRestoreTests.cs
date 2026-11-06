using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;

/// <summary>앱이 터미널을 연 채로 죽었을 때 남기는 목록(휴대폰·태블릿)과 다음 실행에서 서버별로 꺼내기 검사.</summary>
public static class TerminalRestoreTests
{
    private static readonly ConnectionProfile ProfileA = new() { Name = "lab", Host = "10.0.0.1" };
    private static readonly ConnectionProfile ProfileB = new() { Name = "", Host = "pve2.local" };

    public static void OpenTerminalEntryRoundTrip(Checker c)
    {
        var guest = new PveResource { Id = "lxc/200", Kind = ResourceKind.Lxc, VmId = 200, Name = "web", Node = "pve" };
        var ct = OpenTerminalEntry.From(ProfileA, ConsoleTarget.ForGuest("pve", ResourceKind.Lxc, 200), "200 · web", guest);
        c.Equal(ProfileA.Id, ct.ProfileId, "프로필");
        c.Equal("lab", ct.ProfileName, "서버 이름");
        c.Is(ct.CanReopen, "다시 열 수 있다");
        c.Equal("nodes/pve/lxc/200", ct.ToTarget().BasePath, "CT 경로");
        c.Equal("web", ct.ToGuest()?.Name, "게스트 이름");
        c.Equal("lxc/200", ct.ToGuest()?.Id, "게스트 id");

        var serial = OpenTerminalEntry.From(ProfileA, ConsoleTarget.ForGuest("pve 2", ResourceKind.Qemu, 100), "serial", null);
        c.Equal("nodes/pve%202/qemu/100", serial.ToTarget().BasePath, "VM 직렬 경로(노드 이스케이프)");
        c.Equal(ResourceKind.Qemu, serial.ToGuest()?.Kind, "VM 종류");

        var shell = OpenTerminalEntry.From(ProfileB, ConsoleTarget.ForNode("pve", "upgrade"), "pve · upgrade", null);
        c.Equal("pve2.local", shell.ProfileName, "이름이 없으면 주소");
        c.Equal("nodes/pve", shell.ToTarget().BasePath, "노드 경로");
        c.Equal("upgrade", shell.ToTarget().Command, "노드 명령");
        c.Is(shell.ToGuest() is null, "노드 셸은 게스트 없음");

        c.Is(!new OpenTerminalEntry { Title = "x" }.CanReopen, "대상이 없으면 다시 열 수 없다");
        c.Is(!(shell with { GuestKind = "bogus", VmId = 1 }).CanReopen, "모르는 종류");
    }

    public static void PendingTerminalsSurviveRestartPerProfile(Checker c)
    {
        var dir = Path.Combine(Path.GetTempPath(), "orphans-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "open-terminals.txt");
        var crashed = Path.Combine(dir, "crashed.txt");
        var savedPath = SessionKeepAlive.StatePath;
        var savedForm = PlatformInfo.FormFactor;
        object a = new(), b = new(), d = new();
        var entryA = OpenTerminalEntry.From(ProfileA, ConsoleTarget.ForGuest("pve", ResourceKind.Lxc, 200), "200 · web", null);
        var entryB = OpenTerminalEntry.From(ProfileB, ConsoleTarget.ForNode("pve"), "pve · Shell", null);
        try
        {
            PlatformInfo.FormFactor = FormFactor.Mobile;
            SessionKeepAlive.StatePath = path;
            SessionKeepAlive.Acquire(a, entryA);
            SessionKeepAlive.Acquire(b, entryB);
            c.Equal(2, SessionKeepAlive.ReadState(path).Count, "열린 터미널 기록");
            c.Equal(entryA, SessionKeepAlive.ReadState(path)[0], "파일 모양 그대로 되읽기");
            c.Is(!File.Exists(path + ".tmp"), "임시 파일은 남지 않는다");
            SessionKeepAlive.Release(a);
            c.Equal(1, SessionKeepAlive.ReadState(path).Count, "닫으면 빠진다");
            SessionKeepAlive.Acquire(a, entryA);

            // 앱이 죽은 순간의 파일로 다음 실행을 흉내 낸다
            File.Copy(path, crashed);
            SessionKeepAlive.Release(a);
            SessionKeepAlive.Release(b);
            c.Is(!File.Exists(path), "모두 닫으면 파일이 없다");
            SessionKeepAlive.StatePath = crashed;
            c.Equal(2, SessionKeepAlive.Pending.Count, "지난 실행의 터미널");
            c.Equal(1, TerminalRestore.PendingFor(ProfileA.Id).Count, "서버 A 몫");
            c.Equal(0, TerminalRestore.PendingFor(Guid.NewGuid()).Count, "다른 서버는 없음");

            // 다른 서버의 터미널을 새로 열어도 남은 기록은 파일에 그대로(또 죽어도 잃지 않는다)
            var entryD = entryB with { Title = "pve · Shell 2" };
            SessionKeepAlive.Acquire(d, entryD);
            c.Equal(3, SessionKeepAlive.ReadState(crashed).Count, "남은 기록 + 새 터미널");

            var taken = SessionKeepAlive.TakePending(ProfileA.Id);
            c.Equal("200 · web", string.Join("|", taken.Select(entry => entry.Title)), "서버 A 를 꺼낸다");
            c.Equal(0, TerminalRestore.PendingFor(ProfileA.Id).Count, "꺼내면 빠진다");
            c.Equal("pve · Shell 2|pve · Shell", string.Join("|", SessionKeepAlive.ReadState(crashed).Select(e => e.Title)),
                "파일에서도 빠진다");

            SessionKeepAlive.DropPendingExcept([ProfileA.Id]);
            c.Equal(0, SessionKeepAlive.Pending.Count, "지운 서버의 기록은 버린다");
            SessionKeepAlive.Release(d);
            c.Is(!File.Exists(crashed), "다 끝나면 파일이 없다");

            // 이전 버전 파일(제목 줄)은 버린다
            File.WriteAllLines(path, ["100 · ct", "pve · Shell"]);
            SessionKeepAlive.StatePath = path;
            c.Equal(0, SessionKeepAlive.Pending.Count, "이전 버전 파일");

            // 데스크톱은 창을 닫는 것이 정상 종료 — 남기지 않는다
            File.Delete(path);
            PlatformInfo.FormFactor = FormFactor.Desktop;
            SessionKeepAlive.StatePath = path;
            SessionKeepAlive.Acquire(a, entryA);
            c.Is(!File.Exists(path), "데스크톱은 남기지 않는다");
            c.Equal(0, SessionKeepAlive.Pending.Count, "데스크톱은 다시 열 기록 없음");
        }
        finally
        {
            SessionKeepAlive.Release(a);
            SessionKeepAlive.Release(b);
            SessionKeepAlive.Release(d);
            SessionKeepAlive.StatePath = savedPath;
            PlatformInfo.FormFactor = savedForm;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
