using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Guest;

/// <summary>
///     게스트 이전 — 창을 열기 전에 서버의 사전 확인(GET .../migrate)으로 옮길 수 없는 노드와 이유, 함께 옮길 로컬
///     디스크, 온라인 이전을 막는 로컬 자원을 알려 준다(웹 UI 의 이전 창 경고와 같다). 확인을 못 읽으면 그냥 연다.
/// </summary>
internal static class GuestMigrateAction
{
    public static TableAction Create(ProxmoxApiClient api, PveResource guest)
    {
        return new TableAction
        {
            LabelKey = "GuestLife_Migrate", IconKey = "IconSwitch",
            Run = (_, owner) => RunAsync(api, guest, owner)
        };
    }

    private static async Task<string?> RunAsync(ProxmoxApiClient api, PveResource guest, Window? owner)
    {
        var check = await TryCheckAsync(api, guest);
        var online = (await api.GetNodesAsync())
            .Where(n => n.Node != guest.Node && string.Equals(n.Status, "online", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Node)
            .ToList();
        var targets = online
            .Where(n => check is null || !check.NotAllowedNodes.ContainsKey(n))
            .Select(n => (n, n))
            .ToList();
        if (targets.Count == 0) return Loc.T("Migrate_NoTarget", Blocked(check, online));

        var isVm = guest.Kind == ResourceKind.Qemu;
        var fields = new List<FormField>
        {
            new()
            {
                Key = "target", LabelKey = "Table_Target", Kind = FormFieldKind.Choice, Choices = targets,
                Required = true, Hint = Blocked(check, online) is { Length: > 0 } blocked
                    ? Loc.T("Migrate_Blocked", blocked)
                    : null
            },
            new()
            {
                Key = isVm ? "online" : "restart", Kind = FormFieldKind.Bool,
                LabelKey = isVm ? "GuestLife_Online" : "GuestLife_Restart", Initial = guest.IsRunning ? "1" : "0",
                Hint = check is { LocalResources.Count: > 0 }
                    ? Loc.T("Migrate_LocalResources", string.Join(", ", check.LocalResources))
                    : null
            }
        };
        if (isVm)
            fields.Add(new FormField
            {
                Key = "with-local-disks", LabelKey = "NodePower_LocalDisks", Kind = FormFieldKind.Bool,
                Initial = check?.NeedsLocalDisks == true ? "1" : "0",
                Hint = check is { NeedsLocalDisks: true }
                    ? Loc.T("Migrate_LocalDisks", string.Join("\n", check.LocalDisks))
                    : null
            });

        return await SubmitAsync(owner, Loc.T("GuestLife_MigrateTitle", guest.VmId), fields,
            values => api.Guests.MigrateAsync(guest, values), "GuestLife_MigrateStarted", titleIsKey: false);
    }

    /// <summary>사전 확인 — 권한·버전 문제 등으로 못 읽으면 null(확인 없이 이전 창을 연다).</summary>
    private static async Task<MigrationCheck?> TryCheckAsync(ProxmoxApiClient api, PveResource guest)
    {
        try
        {
            return await api.GetMigrationCheckAsync(guest.Node, guest.Kind, guest.VmId);
        }
        catch (Exception ex) when (ex is ProxmoxApiException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException)
        {
            App.Log($"[이전] {guest.VmId} 사전 확인 실패: {ex.Message}");
            return null;
        }
    }

    /// <summary>켜져 있지만 옮길 수 없는 노드와 이유 — "pve2: local-zfs" 줄들.</summary>
    private static string Blocked(MigrationCheck? check, IReadOnlyList<string> online)
    {
        if (check is null) return string.Empty;
        return string.Join("\n", online.Where(check.NotAllowedNodes.ContainsKey)
            .Select(n => check.NotAllowedNodes[n] is { Length: > 0 } why ? $"{n}: {why}" : n));
    }
}
