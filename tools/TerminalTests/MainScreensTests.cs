using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.UI.Services;

/// <summary>메인 화면 공용 부품 — 주기 새로 고침의 겹침 방지, 진행 표시 취소, 세션 만료 문구.</summary>
public static class MainScreensTests
{
    /// <summary>화면 진입·당겨서 새로 고침이 주기 새로 고침과 겹쳐 돌지 않는다(작업 로그 줄 중복의 원인).</summary>
    public static void RefreshNowNeverOverlaps(Checker c)
    {
        var running = 0;
        var maxRunning = 0;
        var calls = 0;
        var gates = new List<TaskCompletionSource>();

        async Task Refresh()
        {
            calls++;
            running++;
            maxRunning = Math.Max(maxRunning, running);
            var gate = new TaskCompletionSource();
            lock (gates) gates.Add(gate);
            await gate.Task;
            running--;
        }

        using var refresher = new PeriodicRefresher(TimeSpan.FromHours(1), Refresh);
        var first = refresher.RefreshNowAsync();
        var second = refresher.RefreshNowAsync(); // 첫 번째가 도는 중 — 기다렸다가 한 번 더
        var third = refresher.RefreshNowAsync(); // 두 번째와 같이 기다린다(세 번 돌지 않음)
        c.Equal(1, calls, "도는 중에는 새로 시작하지 않는다");

        lock (gates) gates[0].SetResult();
        // 화면 스레드가 없는 검사라 이어지는 일이 다른 스레드에서 돌 수 있다 — 잠깐 기다린다
        SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 2, 2000);
        c.Is(first.IsCompleted, "첫 새로 고침 끝");
        c.Equal(2, calls, "기다리던 요청은 끝난 뒤 한 번 더 돈다");
        lock (gates) gates[1].SetResult();
        Task.WhenAll(second, third).GetAwaiter().GetResult();
        c.Equal(2, calls, "기다리던 두 요청이 한 번으로 합쳐진다");
        c.Equal(1, maxRunning, "동시에 둘 이상 돌지 않는다");
    }

    /// <summary>진행 표시의 취소 — 단추·뒤로 가기가 취소를 한 번만 부르고, 겹친 진행 표시는 바깥 것의 취소로 돌아간다.</summary>
    public static void BusyCancel(Checker c)
    {
        var dialogs = new DialogService();
        var cancelled = 0;
        var outer = dialogs.ShowBusy("연결 중", () => cancelled++);
        c.Is(dialogs.IsBusyVisible && dialogs.CanCancelBusy, "취소할 수 있는 진행 표시");

        var inner = dialogs.ShowBusy("다른 작업");
        c.Is(!dialogs.CanCancelBusy, "안쪽(취소 없는) 진행 표시가 보이는 동안 취소 단추 없음");
        c.Is(dialogs.HandleBack(), "진행 중 뒤로 가기는 화면을 넘기지 않는다");
        c.Equal(0, cancelled, "취소 없는 진행 표시에서는 취소하지 않음");
        inner.Dispose();
        c.Equal("연결 중", dialogs.BusyText, "안쪽이 닫히면 바깥 문구로");
        c.Is(dialogs.CanCancelBusy, "바깥 취소 단추가 다시 보인다");

        c.Is(dialogs.HandleBack(), "뒤로 가기 처리");
        c.Equal(1, cancelled, "뒤로 가기가 취소를 부른다");
        c.Is(!dialogs.CanCancelBusy, "취소 뒤 단추 숨김(두 번 부르지 않게)");
        dialogs.CancelBusyCommand.Execute(null);
        c.Equal(1, cancelled, "두 번째 취소는 무시");
        outer.Dispose();
        c.Is(!dialogs.IsBusyVisible && !dialogs.HandleBack(), "진행 표시가 닫히면 뒤로 가기는 화면으로 넘어간다");
    }

    /// <summary>API 토큰 세션이 거부되면 비밀번호 세션과 다른 안내(토큰 확인)를 보인다.</summary>
    public static void ExpiryTextByAuthMode(Checker c)
    {
        static ServerSession Make(AuthMode mode)
        {
            var profile = new ConnectionProfile { Name = "lab", Host = "127.0.0.1", AuthMode = mode, ApiTokenId = "root@pam!t" };
            return new ServerSession(new ProxmoxApiClient(profile), profile, "9.0");
        }

        using var password = Make(AuthMode.Password);
        using var token = Make(AuthMode.ApiToken);
        c.Is(password.ExpiryText() != token.ExpiryText(), "토큰·비밀번호 문구가 다르다", token.ExpiryText());
        c.Is(token.ExpiryText().Contains("lab"), "서버 이름이 들어간다", token.ExpiryText());
    }
}
