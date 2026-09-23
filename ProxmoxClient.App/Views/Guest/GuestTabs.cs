using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest;

/// <summary>게스트 창 왼쪽 목록에 들어가는 탭.</summary>
internal sealed class GuestTab
{
    public required string Id { get; init; }

    /// <summary>탭 이름 리소스 키 — 언어를 바꾸면 다시 조회한다.</summary>
    public required string LabelKey { get; init; }

    /// <summary>CT 에서 쓸 다른 이름(예: 하드웨어 → 리소스). 없으면 <see cref="LabelKey" />.</summary>
    public string? CtLabelKey { get; init; }

    /// <summary>Icons.xaml 의 Geometry 키.</summary>
    public required string IconKey { get; init; }

    /// <summary>이 탭을 볼 수 있는지 — 권한이 없으면 목록에서 숨긴다(웹 UI 와 같은 방식).</summary>
    public required Func<PermissionsInfo, PveResource, bool> IsVisible { get; init; }
}

/// <summary>
/// 게스트 창의 탭 구성. Proxmox 웹 UI 의 VM 화면 순서를 따른다.
/// </summary>
internal static class GuestTabs
{
    public static readonly IReadOnlyList<GuestTab> All =
    [
        new()
        {
            Id = "summary",
            LabelKey = "GuestTab_Summary",
            IconKey = "IconList",
            // 게스트를 볼 수 있으면 요약도 볼 수 있다
            IsVisible = (_, _) => true
        },
        new()
        {
            Id = "console",
            LabelKey = "GuestTab_Console",
            IconKey = "IconMonitor",
            // 템플릿은 실행할 수 없어 콘솔을 열 수 없다
            IsVisible = (p, guest) => p.CanConsole && !guest.IsTemplate
        },
        new()
        {
            Id = "hardware",
            LabelKey = "GuestTab_Hardware",
            CtLabelKey = "GuestTab_Resources",
            IconKey = "IconBox",
            IsVisible = (p, _) => p.CanConfigure
        },
        // CT 는 웹 UI 처럼 리소스 다음에 네트워크·DNS 탭이 따로 있다
        new()
        {
            Id = "network",
            LabelKey = "GuestTab_Network",
            IconKey = "IconSwitch",
            IsVisible = (p, guest) => p.CanConfigure && guest.Kind == ResourceKind.Lxc
        },
        new()
        {
            Id = "dns",
            LabelKey = "GuestTab_Dns",
            IconKey = "IconServer",
            IsVisible = (p, guest) => p.CanConfigure && guest.Kind == ResourceKind.Lxc
        },
        new()
        {
            Id = "cloudinit",
            LabelKey = "GuestTab_CloudInit",
            IconKey = "IconDownload",
            // Cloud-Init 설정은 VM 설정에 들어가므로 VM 에서만 뜻이 있다
            IsVisible = (p, guest) => p.CanConfigure && guest.Kind == ResourceKind.Qemu
        },
        new()
        {
            Id = "options",
            LabelKey = "GuestTab_Options",
            IconKey = "IconSettings",
            IsVisible = (p, _) => p.CanConfigure
        },
        new()
        {
            Id = "tasks",
            LabelKey = "GuestTab_Tasks",
            IconKey = "IconArchive",
            IsVisible = (_, _) => true
        },
        new()
        {
            Id = "monitor",
            LabelKey = "GuestTab_Monitor",
            IconKey = "IconTerminal",
            // QEMU 모니터는 VM 에만 있다. 조회 명령은 Sys.Audit, 바꾸는 명령은 서버가 Sys.Modify 로 막는다
            IsVisible = (p, guest) => guest.Kind == ResourceKind.Qemu && !guest.IsTemplate
                                      && (p.CanSysAudit || p.CanSysModify)
        },
        new()
        {
            Id = "backup",
            LabelKey = "GuestTab_Backup",
            IconKey = "IconDatabase",
            IsVisible = (p, _) => p.CanBackup
        },
        new()
        {
            Id = "replication",
            LabelKey = "DcTab_Replication",
            IconKey = "IconCopy",
            IsVisible = (p, _) => p.CanConfigure
        },
        new()
        {
            Id = "snapshots",
            LabelKey = "GuestTab_Snapshots",
            IconKey = "IconCamera",
            IsVisible = (p, _) => p.CanSnapshot
        },
        new()
        {
            Id = "firewall",
            LabelKey = "GuestTab_Firewall",
            IconKey = "IconShield",
            IsVisible = (p, _) => p.CanConfigure
        },
        new()
        {
            Id = "permissions",
            LabelKey = "DcTab_Permissions",
            IconKey = "IconCheck",
            IsVisible = (p, _) => p.CanAuditPermissions
        }
    ];

    /// <summary>현재 사용자가 이 게스트에서 볼 수 있는 탭만 추린다.</summary>
    public static IReadOnlyList<GuestTab> VisibleFor(PermissionsInfo permissions, PveResource guest)
    {
        return All.Where(tab => tab.IsVisible(permissions, guest)).ToList();
    }
}
