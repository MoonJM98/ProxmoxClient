using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Guest.Tabs;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Api.Versioning;
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
        var tabs = new NavTabList();
        // 웹 UI 데이터센터 메뉴 순서 — 저장소·풀·작업 목록은 서버가 권한에 맞게 걸러 주므로 늘 보인다
        AddGeneral(tabs, api, permissions, openResource);
        AddAccess(tabs, api, permissions);
        AddServices(tabs, api, permissions);

        return new NavWindow(
            Loc.T("DcWindow_Title"),
            Loc.T("DcWindow_Title"),
            "IconServer",
            tabs,
            initialTabId);
    }

    /// <summary>검색·요약·클러스터·Ceph·옵션·저장소·백업·복제.</summary>
    private static void AddGeneral(NavTabList tabs, ProxmoxApiClient api, PermissionsInfo permissions,
        Action<IReadOnlyDictionary<string, string>, Window?>? openResource)
    {
        var audit = permissions.CanSysAudit;
        tabs.Add("search", "DcTab_Search", "IconSearch", true, () => SearchTab.Create(api, openResource,
            ClusterBulkActions.Create(api, permissions.CanPowerMgmt, permissions.CanMigrate)));
        tabs.Add("summary", "GuestTab_Summary", "IconList", audit, () => DatacenterTables.Summary(api));
        tabs.Add("cluster", "DcTab_Cluster", "IconServer", audit,
            () => ClusterTabs.Cluster(api, permissions.CanSysModify));
        tabs.Add("ceph", "DcTab_Ceph", "IconDatabase", audit, () => ClusterTabs.CephStatus(api));
        tabs.Add("options", "GuestTab_Options", "IconSettings", audit, () => DatacenterOptions.Create(api));
        tabs.Add("storage", "DcTab_Storage", "IconDatabase", true,
            () => DatacenterTables.Storage(api, permissions.Has("Datastore.Allocate")));
        tabs.Add("backup", "GuestTab_Backup", "IconArchive", audit,
            () => DatacenterTables.BackupJobs(api, permissions.CanSysModify));
        tabs.Add("replication", "DcTab_Replication", "IconCopy", audit,
            () => DatacenterTables.Replication(api, permissions.CanSysModify));
    }

    /// <summary>사용자·TFA·그룹·풀·역할·인증 영역·권한.</summary>
    private static void AddAccess(NavTabList tabs, ProxmoxApiClient api, PermissionsInfo permissions)
    {
        var access = permissions.CanAuditPermissions;
        tabs.Add("users", "DcTab_Users", "IconLogIn", true,
            () => DatacenterTables.Users(api, permissions.Has("User.Modify")));
        tabs.Add("tfa", "DcTab_Tfa", "IconShield", true,
            () => DatacenterTables.Tfa(api, permissions.Has("User.Modify")), api.Tfa.Feature(nameof(TfaApi.ListAsync)));
        tabs.Add("groups", "DcTab_Groups", "IconFolder", access,
            () => DatacenterTables.Groups(api, permissions.Has("Group.Allocate")));
        tabs.Add("pools", "DcTab_Pools", "IconBox", true,
            () => DatacenterTables.Pools(api, permissions.Has("Pool.Allocate")));
        tabs.Add("roles", "DcTab_Roles", "IconKeyboard", access,
            () => DatacenterTables.Roles(api, permissions.Has("Sys.Modify")));
        tabs.Add("realms", "DcTab_Realms", "IconServer", access,
            () => RealmSyncJobs.View(api, permissions.Has("Realm.Allocate"),
                () => DatacenterTables.Realms(api, permissions.Has("Realm.Allocate"))));
        tabs.Add("acl", "DcTab_Permissions", "IconCheck", access,
            () => DatacenterTables.Acl(api, permissions.Has("Permissions.Modify")));
    }

    /// <summary>HA·SDN·ACME·방화벽·지표·매핑·CPU 모델·알림·작업·클러스터 로그.</summary>
    private static void AddServices(NavTabList tabs, ProxmoxApiClient api, PermissionsInfo permissions)
    {
        var audit = permissions.CanSysAudit;
        tabs.Add("ha", "DcTab_Ha", "IconRotate", audit, () => DatacenterTables.Ha(api, permissions.CanSysConsole));
        tabs.Add("sdn", "DcTab_Sdn", "IconSwitch", audit, () => ClusterTabs.Sdn(api, permissions.Has("SDN.Allocate")));
        tabs.Add("acme", "DcTab_Acme", "IconShield", audit, () => AcmeTabs.Create(api, permissions.CanSysModify));
        tabs.Add("firewall", "GuestTab_Firewall", "IconShield", audit,
            () => FirewallTabs.Create(api, permissions.CanSysModify));
        tabs.Add("metrics", "DcTab_Metrics", "IconList", audit,
            () => MonitoringTabs.Metrics(api, permissions.CanSysModify));
        tabs.Add("mappings", "DcTab_Mappings", "IconKeyboard", permissions.HasAny("Mapping.Audit", "Mapping.Modify"),
            () => ClusterTabs.Mappings(api, permissions.Has("Mapping.Modify")),
            api.Mappings.Feature(nameof(MappingsApi.ListAsync)));
        tabs.Add("cpumodels", "CpuModel_Tab", "IconSettings", audit,
            () => CpuModelsTab.Create(api, permissions.CanSysModify),
            api.Cluster.Feature(nameof(ClusterApi.CpuModelsAsync)));
        tabs.Add("notifications", "DcTab_Notifications", "IconExternal", audit,
            () => MonitoringTabs.Notifications(api, permissions.CanSysModify),
            api.Notifications.Feature(nameof(NotificationsApi.ListTargetsAsync)));
        tabs.Add("tasks", "GuestTab_Tasks", "IconArchive", true, () => new TasksTab(
            async () => await api.GetClusterTasksAsync(),
            "DcTasks_Hint", "NodeTasks_Empty", api));
        tabs.Add("clusterlog", "ClusterLog_Tab", "IconList", audit, () => ClusterLogTab.Create(api));
    }
}
