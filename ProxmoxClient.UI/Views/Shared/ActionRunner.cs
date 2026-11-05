using Avalonia.Controls;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Shared;

/// <summary>
///     표·작업 버튼 줄이 함께 쓰는 실행 흐름 — 확인 문구(Confirm)가 있으면 먼저 묻고, 실행은 작업 이름을 단
///     쓰기 확인 흐름(<see cref="WriteConfirmation" />) 안에서 한다. 확인 문구가 없는 작업도 서버를 바꾸는
///     요청을 보내는 순간 확인 창이 뜬다(읽기만 하는 작업은 묻지 않는다).
/// </summary>
public static class ActionRunner
{
    /// <returns>결과 문구(취소했으면 null).</returns>
    public static async Task<string?> RunAsync(TableAction action, IReadOnlyDictionary<string, string>? row,
        Control? owner)
    {
        var label = Loc.T(action.LabelKey);
        var confirmed = false;
        if (action.Confirm is { } confirm)
        {
            if (!await AppServices.Dialogs.ConfirmAsync(Loc.T("TableTab_ConfirmTitle"), confirm(row), label,
                    action.LabelKey.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
                    action.IconKey is "IconTrash"))
                return null;

            confirmed = true;
        }

        using var flow = WriteConfirmation.Begin(label, confirmed);
        try
        {
            return await action.Run(row, owner);
        }
        catch (WriteCancelledException)
        {
            return null;
        }
    }
}

/// <summary>WPF ThemedMessageBox 대신 — 모바일에는 창이 없으므로 모두 비동기 대화상자다.</summary>
public static class Ask
{
    public static Task<bool> ConfirmAsync(string text, string? title = null, bool destructive = false)
    {
        return AppServices.Dialogs.ConfirmAsync(title ?? Loc.T("TableTab_ConfirmTitle"), text, null, destructive);
    }

    public static Task InfoAsync(string text, string? title = null)
    {
        return AppServices.Dialogs.AlertAsync(title ?? string.Empty, text);
    }

    public static Task ErrorAsync(string text)
    {
        return AppServices.Dialogs.AlertAsync(Loc.T("Error_Title"), text);
    }
}
