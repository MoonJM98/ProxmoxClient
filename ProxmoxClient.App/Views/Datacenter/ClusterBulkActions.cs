using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     클러스터 전체 게스트 일괄 작업(9.0+, 웹 UI 데이터센터의 Bulk Actions) — 노드와 상관없이 고른 게스트를
///     한 번에 시작·종료·일시 중지·이전한다. 서버가 한 작업으로 묶어 처리하므로 진행은 작업 탭에서 본다.
/// </summary>
internal static class ClusterBulkActions
{
    public static IReadOnlyList<TableAction> Create(ProxmoxApiClient api, bool canPower, bool canMigrate)
    {
        var feature = api.Cluster.Feature(nameof(ClusterApi.BulkGuestAsync));
        var actions = new List<TableAction>();
        if (canPower)
        {
            actions.Add(Bulk(api, feature, "start", "DcBulk_Start", "IconPlay", g => g.Status == "stopped",
            [
                new FormField { Key = "timeout", LabelKey = "NodePower_Timeout", Trim = true }
            ]));
            actions.Add(Bulk(api, feature, "shutdown", "DcBulk_Shutdown", "IconPower", g => g.IsRunning,
            [
                new FormField { Key = "timeout", LabelKey = "NodePower_Timeout", Initial = "180", Trim = true },
                new FormField
                {
                    Key = "force-stop", LabelKey = "NodePower_ForceStop", Kind = FormFieldKind.Bool, Initial = "1"
                }
            ]));
            actions.Add(Bulk(api, feature, "suspend", "DcBulk_Suspend", "IconPause",
                g => g.IsRunning && g.Kind == ResourceKind.Qemu,
            [
                new FormField { Key = "to-disk", LabelKey = "DcBulk_ToDisk", Kind = FormFieldKind.Bool }
            ]));
        }

        if (canMigrate) actions.Add(Migrate(api, feature));
        return actions;
    }

    /// <summary>일괄 이전 — 목적지 노드에 이미 있는 게스트는 서버가 건너뛴다(창의 안내 문구).</summary>
    private static TableAction Migrate(ProxmoxApiClient api, Core.Api.Versioning.ApiFeature feature)
    {
        return new TableAction
        {
            LabelKey = "DcBulk_Migrate", IconKey = "IconSwitch", Requires = feature,
            Run = async (_, owner) =>
            {
                var targets = (await api.GetNodesAsync())
                    .Where(n => string.Equals(n.Status, "online", StringComparison.OrdinalIgnoreCase))
                    .Select(n => (n.Node, n.Node))
                    .ToList();
                return await RunAsync(api, owner, "migrate", "DcBulk_Migrate", _ => true,
                [
                    new FormField
                    {
                        Key = "target", LabelKey = "Table_Target", Kind = FormFieldKind.Choice,
                        Choices = targets, Required = true, Hint = Loc.T("DcBulk_MigrateHint")
                    },
                    new FormField { Key = "online", LabelKey = "GuestLife_Online", Kind = FormFieldKind.Bool,
                        Initial = "1" },
                    new FormField
                    {
                        Key = "with-local-disks", LabelKey = "NodePower_LocalDisks", Kind = FormFieldKind.Bool
                    }
                ]);
            }
        };
    }

    private static TableAction Bulk(ProxmoxApiClient api, Core.Api.Versioning.ApiFeature feature, string action,
        string labelKey, string iconKey, Func<PveResource, bool> eligible, IReadOnlyList<FormField> extra)
    {
        return new TableAction
        {
            LabelKey = labelKey, IconKey = iconKey, Requires = feature,
            Run = (_, owner) => RunAsync(api, owner, action, labelKey, eligible, extra)
        };
    }

    /// <summary>조건에 맞는 게스트(템플릿 제외)를 모두 골라 둔 창을 띄우고 일괄 작업을 시작한다.</summary>
    private static async Task<string?> RunAsync(ProxmoxApiClient api, Window? owner, string action,
        string labelKey, Func<PveResource, bool> eligible, IReadOnlyList<FormField> extra)
    {
        var guests = (await api.GetClusterResourcesAsync())
            .Where(r => r.Kind is ResourceKind.Qemu or ResourceKind.Lxc && !r.IsTemplate)
            .Where(eligible)
            .OrderBy(r => r.VmId)
            .ToList();
        if (guests.Count == 0) return Loc.T("DcBulk_NoGuests");

        var choices = guests.Select(g => (g.VmId.ToString(), $"{g.VmId}  {g.Name} - {g.Node}")).ToList();
        return await SubmitTaskAsync(api, owner, Loc.T(labelKey),
        [
            new FormField
            {
                // 클러스터 전체라 미리 고르지 않는다 — 한 번의 확인으로 모든 게스트가 멈추거나 옮겨지지 않게
                Key = "vms", LabelKey = "NodePower_Guests", Kind = FormFieldKind.MultiChoice, Choices = choices,
                Required = true
            },
            ..extra,
            new FormField { Key = "max-workers", LabelKey = "NodePower_MaxWorkers", Trim = true, Advanced = true }
        ], values => api.Cluster.BulkGuestAsync(action, NonEmpty(values)), "DcBulk_Started");
    }
}
