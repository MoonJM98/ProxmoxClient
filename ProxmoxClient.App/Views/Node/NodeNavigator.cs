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
        var tabs = new List<NavTab>();

        void Add(string id, string labelKey, string iconKey, bool visible, Func<UIElement> create)
        {
            if (visible) tabs.Add(new NavTab { Id = id, LabelKey = labelKey, IconKey = iconKey, Create = create });
        }

        // 조회는 Sys.Audit, 바꾸기는 서버가 Sys.Modify 로 다시 확인한다(업데이트 목록은 조회부터 Sys.Modify)
        var audit = permissions.CanSysAudit;

        // 웹 UI 노드 메뉴 순서: 요약·메모·셸 → 시스템(네트워크…시스템 로그) → 업데이트·방화벽·디스크 → 복제·작업·구독
        Add("summary", "GuestTab_Summary", "IconList", audit,
            () => new NodeSummaryTab(api, node, NodePowerActions.Create(api, node, permissions)));
        Add("notes", "NodeTab_Notes", "IconPencil", audit, () => CreateSectionTab(api, node, Notes));
        Add("shell", "NodeTab_Shell", "IconTerminal", permissions.CanSysConsole, () => new NodeShellTab(api, node));
        Add("network", "NodeTab_Network", "IconSwitch", audit,
            () => NodeTables.Network(api, node, permissions.CanSysModify));
        Add("certificates", "NodeTab_Certificates", "IconShield", audit,
            () => NodeTables.Certificates(api, node, permissions.CanSysModify));
        Add("dns", "NodeTab_Dns", "IconServer", audit, () => CreateSectionTab(api, node, Dns));
        Add("hosts", "NodeTab_Hosts", "IconList", audit,
            () => NodeSystemTabs.Hosts(api, node, permissions.CanSysModify));
        Add("options", "GuestTab_Options", "IconSettings", audit, () => NodeSystemTabs.NodeOptions(api, node));
        Add("time", "NodeTab_Time", "IconRotate", audit, () => CreateSectionTab(api, node, Time));
        Add("syslog", "NodeTab_Syslog", "IconList", audit, () => new SyslogTab(api, node));
        Add("updates", "NodeTab_Updates", "IconDownload", permissions.CanSysModify,
            () => UpdateTabs.Create(api, node, permissions.CanSysConsole));
        Add("firewall", "GuestTab_Firewall", "IconShield", audit,
            () => Datacenter.FirewallTabs.ForNode(api, node));
        Add("disks", "NodeTab_Disks", "IconDatabase", audit,
            () => DiskTabs.Create(api, node, permissions.CanSysModify));
        Add("ceph", "DcTab_Ceph", "IconDatabase", audit,
            () => CephTabs.Create(api, node, permissions.CanSysModify, permissions.CanSysConsole));
        Add("replication", "DcTab_Replication", "IconCopy", audit, () => NodeSystemTabs.Replication(api, node));
        Add("tasks", "GuestTab_Tasks", "IconArchive", true, () => new TasksTab(
            async () => await api.GetNodeTasksAsync(node),
            "NodeTasks_Hint", "NodeTasks_Empty", api));
        Add("subscription", "NodeTab_Subscription", "IconCheck", audit,
            () => NodeSystemTabs.Subscription(api, node, permissions.CanSysModify));

        return new NavWindow(
            Loc.T("NodeWindow_Title", node),
            Loc.T("NodeWindow_Title", node),
            "IconServer",
            tabs,
            initialTabId);
    }

    private static OptionsTab CreateSectionTab(ProxmoxApiClient api, string node, Section section)
    {
        return new OptionsTab(
            section.Options,
            () => api.GetNodeSectionAsync(node, section.Path),
            async changes => await api.UpdateNodeSectionAsync(node, section.Path, ActionHelpers.UpdateForm(changes)));
    }
}
