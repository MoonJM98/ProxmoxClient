using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Guest.Tabs;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     데이터센터(클러스터 전체) 탐색 창을 만든다. 목록형 화면은 <see cref="DatacenterTables" />에 정의한다.
/// </summary>
public static class DatacenterNavigator
{
    /// <param name="openResource">검색 탭에서 행을 두 번 눌렀을 때 노드·게스트·저장소 창을 여는 동작.</param>
    public static NavWindow Create(ProxmoxApiClient api, PermissionsInfo permissions, string? initialTabId = null,
        Action<IReadOnlyDictionary<string, string>, Window?>? openResource = null)
    {
        var tabs = new List<NavTab>();

        void Add(string id, string labelKey, string iconKey, bool visible, Func<UIElement> create)
        {
            if (visible) tabs.Add(new NavTab { Id = id, LabelKey = labelKey, IconKey = iconKey, Create = create });
        }

        // 저장소·풀·작업 목록은 서버가 권한에 맞게 걸러 주므로 늘 보인다
        var audit = permissions.CanSysAudit;
        var access = permissions.CanAuditPermissions;

        // 웹 UI 데이터센터 메뉴 순서
        Add("search", "DcTab_Search", "IconSearch", true, () => SearchTab.Create(api, openResource));
        Add("summary", "GuestTab_Summary", "IconList", audit, () => DatacenterTables.Summary(api));
        Add("cluster", "DcTab_Cluster", "IconServer", audit,
            () => ClusterTabs.Cluster(api, permissions.CanSysModify));
        Add("ceph", "DcTab_Ceph", "IconDatabase", audit, () => ClusterTabs.CephStatus(api));
        Add("options", "GuestTab_Options", "IconSettings", audit, () => DatacenterOptions.Create(api));
        Add("storage", "DcTab_Storage", "IconDatabase", true,
            () => DatacenterTables.Storage(api, permissions.Has("Datastore.Allocate")));
        Add("backup", "GuestTab_Backup", "IconArchive", audit,
            () => DatacenterTables.BackupJobs(api, permissions.CanSysModify));
        Add("replication", "DcTab_Replication", "IconCopy", audit,
            () => DatacenterTables.Replication(api, permissions.CanSysModify));
        Add("users", "DcTab_Users", "IconLogIn", access,
            () => DatacenterTables.Users(api, permissions.Has("User.Modify")));
        Add("tfa", "DcTab_Tfa", "IconShield", access,
            () => DatacenterTables.Tfa(api, permissions.Has("User.Modify")));
        Add("groups", "DcTab_Groups", "IconFolder", access,
            () => DatacenterTables.Groups(api, permissions.Has("Group.Allocate")));
        Add("pools", "DcTab_Pools", "IconBox", true,
            () => DatacenterTables.Pools(api, permissions.Has("Pool.Allocate")));
        Add("roles", "DcTab_Roles", "IconKeyboard", access,
            () => DatacenterTables.Roles(api, permissions.Has("Sys.Modify")));
        Add("realms", "DcTab_Realms", "IconServer", access,
            () => DatacenterTables.Realms(api, permissions.Has("Realm.Allocate")));
        Add("acl", "DcTab_Permissions", "IconCheck", access,
            () => DatacenterTables.Acl(api, permissions.Has("Permissions.Modify")));
        Add("ha", "DcTab_Ha", "IconRotate", audit, () => DatacenterTables.Ha(api, permissions.CanSysConsole));
        Add("sdn", "DcTab_Sdn", "IconSwitch", audit, () => ClusterTabs.Sdn(api, permissions.Has("SDN.Allocate")));
        Add("acme", "DcTab_Acme", "IconShield", audit, () => AcmeTabs.Create(api, permissions.CanSysModify));
        Add("firewall", "GuestTab_Firewall", "IconShield", audit,
            () => FirewallTabs.Create(api, permissions.CanSysModify));
        Add("metrics", "DcTab_Metrics", "IconList", audit,
            () => MonitoringTabs.Metrics(api, permissions.CanSysModify));
        Add("mappings", "DcTab_Mappings", "IconKeyboard", permissions.HasAny("Mapping.Audit", "Mapping.Modify"),
            () => ClusterTabs.Mappings(api, permissions.Has("Mapping.Modify")));
        Add("notifications", "DcTab_Notifications", "IconExternal", audit,
            () => MonitoringTabs.Notifications(api, permissions.CanSysModify));
        Add("tasks", "GuestTab_Tasks", "IconArchive", true, () => new TasksTab(
            async () => await api.GetClusterTasksAsync(),
            "DcTasks_Hint", "NodeTasks_Empty", api));

        return new NavWindow(
            Loc.T("DcWindow_Title"),
            Loc.T("DcWindow_Title"),
            "IconServer",
            tabs,
            initialTabId);
    }
}
