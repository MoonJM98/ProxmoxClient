using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Datacenter;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Storage;

/// <summary>
///     백업 정리(웹 UI 의 Prune group) — 고른 백업의 게스트 백업들에 보존 규칙을 적용한다. 규칙은 저장소 설정으로
///     채워 두고 바꿀 수 있으며, 서버의 미리 보기로 지울 백업을 보여 준 뒤 확인을 받아야 지운다.
/// </summary>
internal static class PruneAction
{
    /// <summary>확인 창에 이름을 늘어놓을 최대 개수 — 넘으면 "외 N개".</summary>
    private const int MaxListed = 15;

    public static TableAction Create(ProxmoxApiClient api, string node, string storage)
    {
        return new TableAction
        {
            LabelKey = "Prune_Action", IconKey = "IconTrash", NeedsSelection = true,
            Run = (row, owner) => RunAsync(api, node, storage, row!, owner)
        };
    }

    private static async Task<string?> RunAsync(ProxmoxApiClient api, string node, string storage,
        IReadOnlyDictionary<string, string> row, Window? owner)
    {
        if (!int.TryParse(Value(row, "vmid"), out var vmid)) return Loc.T("Prune_NoGuest");

        var current = await CurrentRulesAsync(api, storage);
        var dialog = new FormDialog(Loc.T("Prune_Title", vmid, storage), StorageActions.PruneFields(current),
            StorageActions.ValidateKeep) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } values) return null;

        var rules = StorageActions.PruneBackups(values);
        if (rules.Length == 0) rules = "keep-all=1"; // 칸을 모두 비우면 아무것도 지우지 않는다
        var preview = await api.Storage.PrunePreviewAsync(node, storage, rules, vmid);
        var removed = preview.Where(p => Value(p, "mark") == "remove").Select(p => Value(p, "volid")).ToList();
        if (removed.Count == 0) return Loc.T("Prune_NothingToRemove", preview.Count);

        var listed = string.Join('\n', removed.Take(MaxListed));
        if (removed.Count > MaxListed) listed += '\n' + Loc.T("Prune_More", removed.Count - MaxListed);
        var answer = ThemedMessageBox.Show(owner ?? Application.Current.MainWindow!,
            Loc.T("Prune_Confirm", removed.Count, preview.Count - removed.Count, listed),
            Loc.T("TableTab_ConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return null;

        return await RunTaskAsync(api, api.Storage.PruneAsync(node, storage, rules, vmid), "Prune_Done");
    }

    /// <summary>
    ///     저장소 설정의 보존 규칙 — 설정 읽기(Datastore.Allocate)가 없는 사용자는 빈 값(모두 보관)에서 시작한다.
    /// </summary>
    private static async Task<string> CurrentRulesAsync(ProxmoxApiClient api, string storage)
    {
        try
        {
            var config = await api.Storage.GetAsync(storage);
            return config.TryGetValue("prune-backups", out var raw) ? PropertyString.FromJsonObject(raw) : "";
        }
        catch (ProxmoxApiException ex) when (ex.StatusCode == 403)
        {
            return string.Empty;
        }
    }

    private static string Value(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var v) ? v : string.Empty;
    }
}
