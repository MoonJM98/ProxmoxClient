using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Guest;

/// <summary>
///     게스트 요약 화면의 이전·템플릿 변환·삭제. 오래 걸리는 이전은 시작만 확인하고 진행은 작업 탭에서 본다.
/// </summary>
internal static class GuestLifecycleActions
{
    private static readonly IReadOnlyList<TableColumn> AgentColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 180 },
        new() { Key = "value", HeaderKey = "NodeSubscription_Value", Width = 0 }
    ];

    /// <summary>에이전트 항목 이름 — 인터페이스 이름(eth0 등)은 그대로 둔다.</summary>
    private static string AgentLabel(string name)
    {
        return name switch
        {
            "host-name" => Loc.T("GuestLife_AgentHostName"),
            "os" => Loc.T("GuestLife_AgentOs"),
            "kernel" => Loc.T("GuestLife_AgentKernel"),
            _ => name
        };
    }

    public static IReadOnlyList<TableAction> Create(ProxmoxApiClient api, PveResource guest,
        PermissionsInfo permissions)
    {
        var path = $"nodes/{Seg(guest.Node)}/{guest.Kind.ApiSegment()}/{guest.VmId}";
        var vmid = guest.VmId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var actions = new List<TableAction>();

        // 게스트 안의 IP·OS — QEMU 게스트 에이전트가 켜진 실행 중 VM 에서만 답이 온다
        if (guest.Kind == ResourceKind.Qemu)
            actions.Add(new TableAction
            {
                LabelKey = "GuestLife_AgentInfo", IconKey = "IconMonitor",
                Run = (_, owner) => Task.FromResult(TableWindow.ShowModal(owner,
                    Loc.T("GuestLife_AgentInfoTitle", vmid), new TableTab(async () =>
                        (await api.GetAgentSummaryAsync(guest.Node, guest.VmId))
                        .Select(row => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                        {
                            ["name"] = AgentLabel(row["name"]), ["value"] = row["value"]
                        })
                        .ToList(), AgentColumns, "GuestLife_AgentInfoHint")))
            });

        if (permissions.CanMigrate)
            actions.Add(new TableAction
            {
                LabelKey = "GuestLife_Migrate", IconKey = "IconSwitch",
                Run = async (_, owner) =>
                {
                    var targets = (await api.GetNodesAsync())
                        .Where(n => n.Node != guest.Node
                                    && string.Equals(n.Status, "online", StringComparison.OrdinalIgnoreCase))
                        .Select(n => (n.Node, n.Node))
                        .ToList();
                    var isVm = guest.Kind == ResourceKind.Qemu;

                    return await SubmitAsync(owner, Loc.T("GuestLife_MigrateTitle", vmid),
                    [
                        new FormField
                        {
                            Key = "target", LabelKey = "Table_Target", Kind = FormFieldKind.Choice, Choices = targets,
                            Required = true
                        },
                        new FormField
                        {
                            Key = isVm ? "online" : "restart", Kind = FormFieldKind.Bool,
                            LabelKey = isVm ? "GuestLife_Online" : "GuestLife_Restart",
                            Initial = guest.IsRunning ? "1" : "0"
                        },
                        ..(isVm
                            ?
                            new[]
                            {
                                new FormField
                                {
                                    Key = "with-local-disks", LabelKey = "NodePower_LocalDisks",
                                    Kind = FormFieldKind.Bool
                                }
                            }
                            : [])
                    ], values => api.PostActionAsync($"{path}/migrate", values), "GuestLife_MigrateStarted",
                        titleIsKey: false);
                }
            });

        if (permissions.CanAllocate)
        {
            actions.Add(new TableAction
            {
                LabelKey = "GuestLife_Template", IconKey = "IconCopy",
                Confirm = _ => Loc.T("GuestLife_TemplateConfirm", vmid),
                Run = async (_, _) =>
                {
                    // 실행 중인 게스트는 템플릿으로 바꿀 수 없다 — 서버 오류 대신 먼저 알려 준다
                    if (guest.IsRunning) return Loc.T("GuestLife_StopFirst");

                    await api.PostActionAsync($"{path}/template");
                    return Loc.T("GuestLife_Templated");
                }
            });
            actions.Add(new TableAction
            {
                LabelKey = "GuestLife_Delete", IconKey = "IconTrash",
                Run = (_, owner) => guest.IsRunning
                    ? Task.FromResult<string?>(Loc.T("GuestLife_StopFirst"))
                    : SubmitTaskAsync(api, owner, Loc.T("GuestLife_DeleteTitle", vmid, guest.Name),
                    [
                        new FormField
                        {
                            Key = "purge", LabelKey = "GuestLife_Purge", Kind = FormFieldKind.Bool, Initial = "1"
                        },
                        new FormField
                        {
                            Key = "destroy-unreferenced-disks", LabelKey = "GuestLife_DestroyUnreferenced",
                            Kind = FormFieldKind.Bool, Initial = "1"
                        },
                        TypeToConfirmField()
                    ], values => api.DeleteActionAsync(
                        $"{path}?purge={values["purge"]}"
                        + $"&destroy-unreferenced-disks={values["destroy-unreferenced-disks"]}"),
                        "GuestLife_Deleted", TypedMatches(vmid))
            });
        }

        return actions;
    }
}
