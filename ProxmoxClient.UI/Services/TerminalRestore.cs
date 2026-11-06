using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Services;

/// <summary>
///     앱이 터미널을 연 채로 죽었을 때(Android 가 배경 앱을 정리 등) 다음 실행에서 터미널을 다시 연다.
///     같은 서버(프로필)에 사용자가 직접 로그인한 뒤에만 묻는다 — 자동 로그인은 하지 않는다.
///     다른 서버에 로그인하면 기록은 그대로 남아 서버 목록에 안내가 보인다.
/// </summary>
public static class TerminalRestore
{
    /// <summary>이 서버에서 다시 열 터미널(파일 순서 = 열었던 순서).</summary>
    public static IReadOnlyList<OpenTerminalEntry> PendingFor(Guid profileId)
    {
        return SessionKeepAlive.Pending.Where(entry => entry.ProfileId == profileId).ToList();
    }

    /// <summary>로그인 직후(클러스터 화면을 띄운 뒤) — 이 서버에 남은 터미널이 있으면 다시 열지 묻는다.</summary>
    public static async Task OfferAsync(ServerSession session)
    {
        var entries = PendingFor(session.Profile.Id);
        if (entries.Count == 0) return;

        var reopen = await AppServices.Dialogs.ConfirmAsync(L.T("Restore_PromptTitle"),
            L.T("Restore_PromptMessage", entries.Count, string.Join("\n", entries.Select(entry => "• " + entry.Title))),
            L.T("Restore_Reopen"));
        // 다시 열든 닫든 기록은 지운다(같은 질문을 또 하지 않게)
        entries = SessionKeepAlive.TakePending(session.Profile.Id);
        if (!reopen) return;

        await ReopenAsync(session, entries);
    }

    /// <summary>열었던 순서대로 쌓는다 — 마지막 터미널이 맨 위, 나머지는 뒤로 가기로 차례로 보인다(보일 때 연결).</summary>
    public static async Task ReopenAsync(ServerSession session, IReadOnlyList<OpenTerminalEntry> entries)
    {
        foreach (var entry in entries)
        {
            AppLog.Info($"reopen terminal: {entry.Title}");
            await AppServices.Navigator.PushAsync(new TerminalViewModel(session.Api, entry.ToTarget(), entry.Title,
                entry.ToGuest()));
        }
    }
}
