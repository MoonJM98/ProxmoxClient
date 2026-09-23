using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Node;

/// <summary>
///     노드 요약 화면의 전원·일괄 작업 — 노드 재부팅/종료와 게스트 일괄 시작/정지/이전.
///     일괄 작업은 서버가 한 작업으로 묶어 처리하므로 시작만 확인하고, 진행은 작업 탭에서 본다.
/// </summary>
internal static class NodePowerActions
{
    public static IReadOnlyList<TableAction> Create(ProxmoxApiClient api, string node, PermissionsInfo permissions)
    {
        var basePath = $"nodes/{Seg(node)}";
        var actions = new List<TableAction>();

        if (permissions.CanPowerMgmt)
        {
            actions.Add(new TableAction
            {
                LabelKey = "NodePower_StartAll", IconKey = "IconPlay",
                Run = (_, owner) => BulkAsync(api, node, owner, "NodePower_StartAll", g => !g.IsRunning,
                [
                    new FormField
                    {
                        Key = "force", LabelKey = "NodePower_IgnoreOnboot", Kind = FormFieldKind.Bool, Initial = "1"
                    }
                ],
                    form => api.PostActionAsync($"{basePath}/startall", form))
            });
            actions.Add(new TableAction
            {
                LabelKey = "NodePower_StopAll", IconKey = "IconSquare",
                Run = (_, owner) => BulkAsync(api, node, owner, "NodePower_StopAll", g => g.IsRunning,
                [
                    new FormField { Key = "timeout", LabelKey = "NodePower_Timeout", Initial = "180" },
                    new FormField
                    {
                        Key = "force-stop", LabelKey = "NodePower_ForceStop", Kind = FormFieldKind.Bool, Initial = "1"
                    }
                ], form => api.PostActionAsync($"{basePath}/stopall", form))
            });
        }

        if (permissions.CanMigrate)
            actions.Add(new TableAction
            {
                LabelKey = "NodePower_MigrateAll", IconKey = "IconSwitch",
                Run = async (_, owner) =>
                {
                    var targets = (await api.GetNodesAsync())
                        .Where(n => n.Node != node
                                    && string.Equals(n.Status, "online", StringComparison.OrdinalIgnoreCase))
                        .Select(n => (n.Node, n.Node))
                        .ToList();
                    return await BulkAsync(api, node, owner, "NodePower_MigrateAll", _ => true,
                    [
                        new FormField
                        {
                            Key = "target", LabelKey = "Table_Target", Kind = FormFieldKind.Choice, Choices = targets,
                            Required = true
                        },
                        new FormField { Key = "max-workers", LabelKey = "NodePower_MaxWorkers", Initial = "1" },
                        new FormField
                        {
                            Key = "with-local-disks", LabelKey = "NodePower_LocalDisks", Kind = FormFieldKind.Bool
                        }
                    ], form => api.PostActionAsync($"{basePath}/migrateall", form));
                }
            });

        if (permissions.CanSysPowerMgmt)
        {
            actions.Add(NodeCommand(api, basePath, node, "reboot", "NodePower_Reboot", "IconRotate"));
            actions.Add(NodeCommand(api, basePath, node, "shutdown", "NodePower_Shutdown", "IconPower"));
        }

        return actions;
    }

    /// <summary>노드 재부팅/종료 — 노드 이름을 직접 입력해야 실행된다(이 노드의 게스트가 모두 멈춘다).</summary>
    private static TableAction NodeCommand(ProxmoxApiClient api, string basePath, string node, string command,
        string labelKey, string iconKey)
    {
        return new TableAction
        {
            LabelKey = labelKey, IconKey = iconKey,
            Run = (_, owner) => SubmitAsync(owner, Loc.T("NodePower_CommandTitle", Loc.T(labelKey), node),
                [TypeToConfirmField()],
                _ => api.PostActionAsync($"{basePath}/status",
                    new Dictionary<string, string> { ["command"] = command }),
                "NodePower_CommandSent", titleIsKey: false, validate: TypedMatches(node))
        };
    }

    /// <summary>이 노드의 게스트 중 조건에 맞는 것을 골라(기본 전부 선택) 일괄 작업을 시작한다.</summary>
    private static async Task<string?> BulkAsync(ProxmoxApiClient api, string node, Window? owner, string titleKey,
        Func<PveResource, bool> eligible, IReadOnlyList<FormField> extra,
        Func<IReadOnlyDictionary<string, string>, Task<string>> start)
    {
        var guests = (await api.GetClusterResourcesAsync())
            .Where(r => r.Kind is ResourceKind.Qemu or ResourceKind.Lxc && r.Node == node && !r.IsTemplate)
            .Where(eligible)
            .OrderBy(r => r.VmId)
            .ToList();
        var choices = guests.Select(g => (g.VmId.ToString(), $"{g.VmId}  {g.Name}")).ToList();

        return await SubmitAsync(owner, titleKey,
        [
            new FormField
            {
                Key = "vms", LabelKey = "NodePower_Guests", Kind = FormFieldKind.MultiChoice, Choices = choices,
                Initial = string.Join(",", choices.Select(c => c.Item1)), Required = true
            },
            ..extra
        ], values => start(NonEmpty(values)), "NodePower_BulkStarted");
    }
}
