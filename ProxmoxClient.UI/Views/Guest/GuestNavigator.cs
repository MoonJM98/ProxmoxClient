using Avalonia.Controls;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Datacenter;
using ProxmoxClient.UI.Views.Guest.Hardware;
using ProxmoxClient.UI.Views.Guest.Tabs;
using ProxmoxClient.UI.Views.Node;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Guest;

/// <summary>
///     게스트(VM/CT) 탐색 화면을 만든다 — 권한에 맞는 탭만 골라 공통 셸(<see cref="NavWindow" />)에 넘긴다.
///     WPF GuestNavigator 와 같은 탭·이름·아이콘·순서.
/// </summary>
public static class GuestNavigator
{
    public static NavWindow Create(ServerSession session, PveResource guest, string? initialTabId = null)
    {
        return new NavWindow(
            Loc.T("GuestWindow_Title", guest.Kind.Label(), guest.VmId, guest.Name),
            Loc.T("GuestWindow_Header", guest.Kind.Label(), guest.VmId, guest.Name, guest.Node),
            guest.Kind == ResourceKind.Lxc ? "IconTerminal" : "IconBox",
            Tabs(session, guest),
            initialTabId);
    }

    /// <summary>이 게스트에서 보이는 탭 목록(권한·게스트 종류에 맞는 것만). 탐색 화면과 모바일 "관리" 항목이 같이 쓴다.</summary>
    public static IReadOnlyList<NavTab> Tabs(ServerSession session, PveResource guest)
    {
        return GuestTabs.VisibleFor(session.Permissions, guest)
            .Select(tab => new NavTab
            {
                Id = tab.Id,
                LabelKey = guest.Kind == ResourceKind.Lxc && tab.CtLabelKey is { } ctLabel ? ctLabel : tab.LabelKey,
                IconKey = tab.IconKey,
                Create = () => CreateContent(tab.Id, session, guest)
            })
            .ToList();
    }

    /// <summary>탭 하나의 내용만 만든다(모바일 "관리" 목록에서 누르면 그 탭 화면이 바로 열린다). 볼 수 없는 탭이면 null.</summary>
    public static Control? Page(ServerSession session, PveResource guest, string tabId)
    {
        return Tabs(session, guest).FirstOrDefault(tab => tab.Id == tabId)?.Create();
    }

    private static Control CreateContent(string tabId, ServerSession session, PveResource guest)
    {
        var api = session.Api;
        var permissions = session.Permissions;
        return tabId switch
        {
            "summary" => new SummaryTab(api, guest, GuestLifecycleActions.Create(api, guest, permissions)),
            "console" => new ConsoleTab(session, guest),
            // 웹 UI 처럼 간편(HardwareTab) · 목록(HardwareView) 두 칸
            "hardware" => HardwareSubTabs.For(api, guest),
            "network" => CtNetwork.Create(api, guest),
            "dns" => GuestOptionsTab(api, guest, GuestOptions.CtDns),
            "cloudinit" => CloudInitTab(api, guest),
            "options" => GuestOptionsTab(api, guest, GuestOptions.All),
            "tasks" => new TasksTab(
                async () => await api.GetNodeTasksAsync(guest.Node, vmid: guest.VmId),
                "TasksTab_Hint", "TasksTab_Empty", api),
            "monitor" => new MonitorTab(api, guest),
            "backup" => BackupTabs(api, guest, permissions),
            "replication" => ViewSpecs.Build(NodeSystemTabs.Replication(api, guest.Node, guest.VmId, canEdit: true)),
            "snapshots" => new SnapshotsTab(api, guest),
            "firewall" => FirewallTabs.ForGuest(api, guest, permissions.CanConfigure),
            "permissions" => PermissionsTab(api, guest, permissions),
            _ => throw new ArgumentOutOfRangeException(nameof(tabId), tabId, "알 수 없는 게스트 탭")
        };
    }

    private static SubTabsView BackupTabs(ProxmoxApiClient api, PveResource guest, PermissionsInfo permissions)
    {
        IReadOnlyList<(string LabelKey, Func<Control> Create)> tabs =
        [
            ("BackupList_Now", () => new BackupTab(api, guest)),
            ("BackupList_Tab", () => BackupList.Create(api, guest, permissions.CanAllocate))
        ];
        return new SubTabsView(tabs);
    }

    /// <summary>이 게스트(/vms/{vmid})에 직접 걸린 권한만 보여 주고 고친다.</summary>
    private static TableTab PermissionsTab(ProxmoxApiClient api, PveResource guest, PermissionsInfo permissions)
    {
        var path = $"/vms/{guest.VmId}";
        return new TableTab(
            async () => (await api.Access.ListAclAsync())
                .Where(entry => entry.TryGetValue("path", out var p) && p == path)
                .ToList(),
            DatacenterTables.AclColumns,
            "GuestPermissions_Hint",
            permissions.Has("Permissions.Modify") ? AccessActions.Acl(api, path) : null);
    }

    /// <summary>
    ///     게스트 설정을 읽고 쓰는 옵션 화면 — 이 게스트에 해당하는 항목만 넘긴다.
    ///     웹 UI 처럼 …/pending 으로 읽어 재시작 후 적용될 값을 따로 보여 주고, 되돌리기(revert)를 할 수 있다.
    /// </summary>
    private static OptionsTab GuestOptionsTab(ProxmoxApiClient api, PveResource guest,
        IReadOnlyList<GuestOption> options, IReadOnlyList<TableAction>? extraActions = null)
    {
        // 서버 버전이 모르는 설정(예: 8.0 전의 ciupgrade)은 옵션 목록이 저장 요청(target)을 보고 뺀다
        var kind = guest.Kind;
        var node = guest.Node;
        var vmid = guest.VmId;
        return new OptionsTab(
            options.Where(o => o.AppliesTo(kind)).ToList(),
            () => api.GetGuestPendingAsync(node, kind, vmid),
            changes => api.UpdateGuestConfigAsync(node, kind, vmid, FormHelpers.UpdateForm(changes)),
            keys => api.RevertGuestPendingAsync(node, kind, vmid, keys),
            extraActions,
            api.Guests.Feature(nameof(GuestsApi.SetConfigAsync)));
    }

    /// <summary>
    ///     Cloud-Init 탭 — 웹 UI 처럼 Cloud-Init 드라이브(ide/sata/scsi N = …:cloudinit)가 있어야 고칠 수 있다.
    ///     없으면 목록을 흐리게 두고 하드웨어에서 드라이브를 추가하라고 알린다.
    /// </summary>
    private static OptionsTab CloudInitTab(ProxmoxApiClient api, PveResource guest)
    {
        var tab = GuestOptionsTab(api, guest, CloudInitOptions.All,
            [RegenerateCloudInit(api, guest), ..CloudInitViews.Actions(api, guest)]);
        tab.LockedReason = config => HasCloudInitDrive(config) ? null : Loc.T("CloudInit_NoDrive");
        return tab;
    }

    private static bool HasCloudInitDrive(IReadOnlyDictionary<string, string> config)
    {
        return config.Any(kv => kv.Key.TrimEnd("0123456789".ToCharArray()) is "ide" or "sata" or "scsi"
                                && kv.Value.Contains("cloudinit", StringComparison.Ordinal));
    }

    /// <summary>Cloud-Init 이미지 다시 만들기(PUT …/cloudinit) — 바꾼 설정을 드라이브에 바로 반영한다.</summary>
    private static TableAction RegenerateCloudInit(ProxmoxApiClient api, PveResource guest)
    {
        return new TableAction
        {
            LabelKey = "CloudInit_Regenerate", IconKey = "IconRotate",
            Run = async (_, _) =>
            {
                await api.Guests.RegenerateCloudInitAsync(guest.Node, guest.VmId);
                return Loc.T("CloudInit_Regenerated");
            }
        };
    }
}
