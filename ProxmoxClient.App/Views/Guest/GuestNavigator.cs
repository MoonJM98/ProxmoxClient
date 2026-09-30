using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest;

/// <summary>
///     게스트(VM/CT) 탐색 창을 만든다 — 권한에 맞는 탭만 골라 공통 셸(<see cref="NavWindow" />)에 넘긴다.
/// </summary>
public static class GuestNavigator
{
    public static NavWindow Create(ProxmoxApiClient api, PveResource guest, PermissionsInfo permissions,
        GuestPowerRunner runPower, string? initialTabId = null)
    {
        var tabs = GuestTabs.VisibleFor(permissions, guest)
            .Select(tab => new NavTab
            {
                Id = tab.Id,
                LabelKey = guest.Kind == ResourceKind.Lxc && tab.CtLabelKey is { } ctLabel ? ctLabel : tab.LabelKey,
                IconKey = tab.IconKey,
                Create = () => CreateContent(tab.Id, api, guest, permissions, runPower)
            })
            .ToList();

        return new NavWindow(
            Loc.T("GuestWindow_Title", guest.Kind.Label(), guest.VmId, guest.Name),
            Loc.T("GuestWindow_Header", guest.Kind.Label(), guest.VmId, guest.Name, guest.Node),
            guest.Kind == ResourceKind.Lxc ? "IconTerminal" : "IconBox",
            tabs,
            initialTabId);
    }

    private static UIElement CreateContent(string tabId, ProxmoxApiClient api, PveResource guest,
        PermissionsInfo permissions, GuestPowerRunner runPower)
    {
        return tabId switch
        {
            "summary" => new Tabs.SummaryTab(api, guest, GuestLifecycleActions.Create(api, guest, permissions)),
            "console" => new Tabs.ConsoleTab(api, guest, runPower, permissions.CanPowerMgmt),
            "hardware" => new Shared.SubTabsView(
            [
                ("GuestHardware_Quick", () => new Tabs.HardwareTab(api, guest)),
                // 웹 UI 와 같은 하드웨어(VM)·리소스(CT) 화면
                ("GuestHardware_All", () => new Hardware.HardwareView(api, guest))
            ]),
            "cloudinit" => CloudInitTab(api, guest),
            "options" => CreateOptionsTab(api, guest, Tabs.GuestOptions.All),
            "network" => Hardware.CtNetwork.Create(api, guest),
            "dns" => CreateOptionsTab(api, guest, Tabs.GuestOptions.CtDns),
            "tasks" => new Tabs.TasksTab(
                async () => await api.GetNodeTasksAsync(guest.Node, vmid: guest.VmId),
                "TasksTab_Hint", "TasksTab_Empty", api),
            "backup" => new Shared.SubTabsView(
            [
                ("BackupList_Now", () => new Tabs.BackupTab(api, guest)),
                ("BackupList_Tab", () => Tabs.BackupList.Create(api, guest, permissions.CanAllocate))
            ]),
            "snapshots" => new Tabs.SnapshotsTab(api, guest),
            "firewall" => Datacenter.FirewallTabs.ForGuest(api, guest, permissions.CanConfigure),
            "monitor" => new Tabs.MonitorTab(api, guest),
            "replication" => Node.NodeSystemTabs.Replication(api, guest.Node, guest.VmId, canEdit: true),
            "permissions" => CreatePermissionsTab(api, guest, permissions),
            _ => throw new ArgumentOutOfRangeException(nameof(tabId), tabId, "알 수 없는 게스트 탭")
        };
    }

    /// <summary>이 게스트(/vms/{vmid})에 직접 걸린 권한만 보여 주고 고친다.</summary>
    private static Shared.TableTab CreatePermissionsTab(ProxmoxApiClient api, PveResource guest,
        PermissionsInfo permissions)
    {
        var path = $"/vms/{guest.VmId}";
        return new Shared.TableTab(
            async () => (await api.Access.ListAclAsync())
                .Where(entry => entry.TryGetValue("path", out var p) && p == path)
                .ToList(),
            Datacenter.DatacenterTables.AclColumns,
            "GuestPermissions_Hint",
            permissions.Has("Permissions.Modify") ? Datacenter.AccessActions.Acl(api, path) : null);
    }

    /// <summary>
    ///     게스트 설정을 읽고 쓰는 옵션 화면 — 이 게스트에 해당하는 항목만 넘긴다.
    ///     웹 UI 처럼 …/pending 으로 읽어 재시작 후 적용될 값을 따로 보여 주고, 되돌리기(revert)를 할 수 있다.
    /// </summary>
    private static Tabs.OptionsTab CreateOptionsTab(ProxmoxApiClient api, PveResource guest,
        IReadOnlyList<Tabs.GuestOption> options, IReadOnlyList<Shared.TableAction>? extraActions = null)
    {
        // 서버 버전이 모르는 설정(예: 8.0 전의 ciupgrade)은 옵션 목록이 저장 요청(target)을 보고 뺀다
        return new Tabs.OptionsTab(
            options.Where(o => o.AppliesTo(guest.Kind)).ToList(),
            () => api.GetGuestPendingAsync(guest.Node, guest.Kind, guest.VmId),
            async changes => await api.UpdateGuestConfigAsync(guest.Node, guest.Kind, guest.VmId,
                Shared.ActionHelpers.UpdateForm(changes)),
            async keys => await api.RevertGuestPendingAsync(guest.Node, guest.Kind, guest.VmId, keys),
            extraActions, api.Guests.Feature(nameof(Core.Api.Domains.GuestsApi.SetConfigAsync)));
    }

    /// <summary>
    ///     Cloud-Init 탭 — 웹 UI 처럼 Cloud-Init 드라이브(ide/sata/scsi N = …:cloudinit)가 있어야 고칠 수 있다.
    ///     없으면 목록을 흐리게 두고 하드웨어에서 드라이브를 추가하라고 알린다.
    /// </summary>
    private static Tabs.OptionsTab CloudInitTab(ProxmoxApiClient api, PveResource guest)
    {
        var tab = CreateOptionsTab(api, guest, Tabs.CloudInitOptions.All,
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
    private static Shared.TableAction RegenerateCloudInit(ProxmoxApiClient api, PveResource guest)
    {
        return new Shared.TableAction
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
