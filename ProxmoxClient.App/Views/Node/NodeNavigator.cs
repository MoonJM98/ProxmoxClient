using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Guest.Tabs;
using ProxmoxClient.App.Views.Node.Tabs;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Node;

/// <summary>
///     노드 탐색 창을 만든다. Proxmox 웹 UI 의 노드 화면 순서를 따르며,
///     설정 탭(노트·DNS·시간)은 게스트 옵션과 같은 '한 줄씩 보고 고치기' 화면을 쓴다.
/// </summary>
public static class NodeNavigator
{
    /// <summary>노드 설정 묶음 — 서버 경로(nodes/{node}/{Section})와 그 안의 항목.</summary>
    private sealed record Section(string Path, IReadOnlyList<GuestOption> Options);

    private static readonly Section Notes = new("config",
    [
        new GuestOption
        {
            Key = "description", LabelKey = "NodeTab_Notes", Kind = OptionKind.Text,
            EmptyLabelKey = "GuestOptions_NotSet"
        }
    ]);

    private static readonly Section Dns = new("dns",
    [
        new GuestOption { Key = "search", LabelKey = "GuestSettingsWindow_25", Kind = OptionKind.Text },
        new GuestOption
        {
            Key = "dns1", LabelKey = "NodeDns_Server1", Kind = OptionKind.Text,
            EmptyLabelKey = "GuestOptions_NotSet"
        },
        new GuestOption
        {
            Key = "dns2", LabelKey = "NodeDns_Server2", Kind = OptionKind.Text,
            EmptyLabelKey = "GuestOptions_NotSet"
        },
        new GuestOption
        {
            Key = "dns3", LabelKey = "NodeDns_Server3", Kind = OptionKind.Text,
            EmptyLabelKey = "GuestOptions_NotSet"
        }
    ]);

    private static readonly Section Time = new("time",
    [
        new GuestOption { Key = "timezone", LabelKey = "NodeTime_Timezone", Kind = OptionKind.Text }
    ]);

    public static NavWindow Create(ProxmoxApiClient api, string node, PermissionsInfo permissions,
        string? initialTabId = null)
    {
        // 웹 UI 노드 메뉴 순서: 요약·메모·셸 → 시스템(네트워크…시스템 로그) → 업데이트·방화벽·디스크 → 복제·작업·구독
        var tabs = new NavTabList();
        AddSystem(tabs, api, node, permissions);
        AddServices(tabs, api, node, permissions);

        return new NavWindow(
            Loc.T("NodeWindow_Title", node),
            Loc.T("NodeWindow_Title", node),
            "IconServer",
            tabs,
            initialTabId);
    }

    /// <summary>요약·메모·셸·네트워크·인증서·DNS·hosts·옵션·시간·서비스·시스템 로그.</summary>
    private static void AddSystem(NavTabList tabs, ProxmoxApiClient api, string node, PermissionsInfo permissions)
    {
        // 조회는 Sys.Audit, 바꾸기는 서버가 Sys.Modify 로 다시 확인한다(업데이트 목록은 조회부터 Sys.Modify)
        var audit = permissions.CanSysAudit;
        tabs.Add("summary", "GuestTab_Summary", "IconList", audit,
            () => new NodeSummaryTab(api, node,
                [..NodePowerActions.Create(api, node, permissions), ..ReportAction(api, node, audit)]));
        tabs.Add("notes", "NodeTab_Notes", "IconPencil", audit, () => CreateSectionTab(api, node, Notes));
        tabs.Add("shell", "NodeTab_Shell", "IconTerminal", permissions.CanSysConsole,
            () => new NodeShellTab(api, node));
        tabs.Add("network", "NodeTab_Network", "IconSwitch", audit,
            () => NodeTables.Network(api, node, permissions.CanSysModify));
        tabs.Add("certificates", "NodeTab_Certificates", "IconShield", audit,
            () => NodeTables.Certificates(api, node, permissions.CanSysModify));
        tabs.Add("dns", "NodeTab_Dns", "IconServer", audit, () => CreateSectionTab(api, node, Dns));
        tabs.Add("hosts", "NodeTab_Hosts", "IconList", audit,
            () => NodeSystemTabs.Hosts(api, node, permissions.CanSysModify));
        tabs.Add("options", "GuestTab_Options", "IconSettings", audit, () => NodeSystemTabs.NodeOptions(api, node));
        tabs.Add("time", "NodeTab_Time", "IconRotate", audit, () => CreateSectionTab(api, node, Time));
        tabs.Add("services", "NodeTab_Services", "IconSettings", audit,
            () => NodeServices.Create(api, node, permissions.CanSysModify));
        tabs.Add("syslog", "NodeTab_Syslog", "IconList", audit, () => new SyslogTab(api, node));
    }

    /// <summary>업데이트·방화벽·디스크·Ceph·SDN·복제·작업·구독.</summary>
    private static void AddServices(NavTabList tabs, ProxmoxApiClient api, string node, PermissionsInfo permissions)
    {
        var audit = permissions.CanSysAudit;
        tabs.Add("updates", "NodeTab_Updates", "IconDownload", permissions.CanSysModify,
            () => UpdateTabs.Create(api, node, permissions.CanSysConsole));
        tabs.Add("firewall", "GuestTab_Firewall", "IconShield", audit,
            () => Datacenter.FirewallTabs.ForNode(api, node, permissions.CanSysModify));
        tabs.Add("disks", "NodeTab_Disks", "IconDatabase", audit,
            () => DiskTabs.Create(api, node, permissions.CanSysModify));
        tabs.Add("ceph", "DcTab_Ceph", "IconDatabase", audit,
            () => CephTabs.Create(api, node, permissions.CanSysModify, permissions.CanSysConsole));
        tabs.Add("sdn", "DcTab_Sdn", "IconSwitch", audit, () => NodeSdnTab.Create(api, node),
            api.Sdn.Feature(nameof(Core.Api.Domains.SdnApi.ZoneBridgesAsync)));
        tabs.Add("replication", "DcTab_Replication", "IconCopy", audit, () => NodeSystemTabs.Replication(api, node));
        tabs.Add("tasks", "GuestTab_Tasks", "IconArchive", true, () => new TasksTab(
            async () => await api.GetNodeTasksAsync(node),
            "NodeTasks_Hint", "NodeTasks_Empty", api));
        tabs.Add("subscription", "NodeTab_Subscription", "IconCheck", audit,
            () => NodeSystemTabs.Subscription(api, node, permissions.CanSysModify));
    }

    /// <summary>시스템 보고서(pvereport) — 지원 문의용 긴 글. 모으는 데 수십 초 걸릴 수 있다.</summary>
    private static IEnumerable<TableAction> ReportAction(ProxmoxApiClient api, string node, bool audit)
    {
        if (!audit) yield break;

        yield return new TableAction
        {
            LabelKey = "NodeReport_Action", IconKey = "IconList",
            Run = async (_, owner) => TextViewWindow.ShowModal(owner, Loc.T("NodeReport_Title", node),
                await api.Nodes.ReportAsync(node))
        };
    }

    private static OptionsTab CreateSectionTab(ProxmoxApiClient api, string node, Section section)
    {
        return new OptionsTab(
            section.Options,
            () => api.GetNodeSectionAsync(node, section.Path),
            async changes => await api.UpdateNodeSectionAsync(node, section.Path, ActionHelpers.UpdateForm(changes)));
    }
}
