using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views.Main;

/// <summary>
///     게스트 상세 머리글·본문·도구 모음의 표시 규칙 — WPF MainWindow 의 DataTrigger·GuestPowerVisibility·
///     CanOpenConsole 등과 같은 조건. 조건은 모두 속성으로 두어 XAML 바인딩이 그대로 쓴다.
/// </summary>
public sealed partial class MainViewModel
{
    public bool IsSelectedCt => SelectedGuest is { Kind: ResourceKind.Lxc };

    public bool IsSelectedVm => SelectedGuest is { Kind: ResourceKind.Qemu };

    public string GuestPoolText => SelectedGuest?.Pool is { Length: > 0 } pool ? pool : "-";

    /// <summary>메모리 막대 — WPF 는 최대값을 그대로 쓰고 글자는 "사용 / 전체".</summary>
    public UsageBar GuestMemBar => SelectedGuest is { } g
        ? new UsageBar(g.MemBytes, g.MaxMemBytes,
            $"{ByteFormatter.Format(g.MemBytes)} / {ByteFormatter.Format(g.MaxMemBytes)}")
        : UsageBar.Empty;

    /// <summary>디스크 막대 — 메모리와 같은 형식.</summary>
    public UsageBar GuestDiskBar => SelectedGuest is { } g
        ? new UsageBar(g.DiskBytes, g.MaxDiskBytes,
            $"{ByteFormatter.Format(g.DiskBytes)} / {ByteFormatter.Format(g.MaxDiskBytes)}")
        : UsageBar.Empty;

    /// <summary>콘솔 버튼·메뉴 — 템플릿은 실행할 수 없으므로 콘솔이 없다(WPF CanOpenConsole 과 같다).</summary>
    public bool CanOpenConsole => Session.Permissions.CanConsole && SelectedGuest is { IsTemplate: false };

    /// <summary>VM 은 [콘솔 | ▾] — ▾ 에 콘솔 종류(지금은 VNC)가 있다. CT 는 ▾ 없이 콘솔 버튼만.</summary>
    public bool ShowConsoleMenuButton => CanOpenConsole && IsSelectedVm;

    public bool ShowConsolePlainButton => CanOpenConsole && !IsSelectedVm;

    /// <summary>스냅샷 버튼·메뉴 — 템플릿은 스냅샷을 만들 수 없다(WPF CanOpenSnapshots).</summary>
    public bool CanOpenSnapshots => Session.Permissions.CanSnapshot && SelectedGuest is { IsTemplate: false };

    /// <summary>전원 묶음은 전원 관리 권한이 있을 때만 보인다(WPF Permissions.CanPowerMgmt).</summary>
    public bool ShowPowerGroup => Session.Permissions.CanPowerMgmt;

    public bool ShowStartButton => ShowPowerGroup && IsPowerAvailable(GuestPowerAction.Start);

    /// <summary>[종료 | ▾] — 메인 버튼은 종료, ▾ 에 재부팅·일시 정지·재개·최대 절전·정지·리셋.</summary>
    public bool ShowStopSplit => ShowPowerGroup && IsPowerAvailable(GuestPowerAction.Stop);

    public bool ShowShutdownItem => ShowPowerGroup && IsPowerAvailable(GuestPowerAction.Shutdown);

    public bool ShowRebootItem => ShowPowerGroup && IsPowerAvailable(GuestPowerAction.Reboot);

    public bool ShowSuspendItem => ShowPowerGroup && IsPowerAvailable(GuestPowerAction.Suspend);

    public bool ShowResumeItem => ShowPowerGroup && IsPowerAvailable(GuestPowerAction.Resume);

    public bool ShowHibernateItem => ShowPowerGroup && IsPowerAvailable(GuestPowerAction.Hibernate);

    public bool ShowStopItem => ShowPowerGroup && IsPowerAvailable(GuestPowerAction.Stop);

    public bool ShowResetItem => ShowPowerGroup && IsPowerAvailable(GuestPowerAction.Reset);

    /// <summary>[설정 | ▾] — 설정(하드웨어)·복제·백업·방화벽. 설정 권한이 있을 때만 보인다.</summary>
    public bool ShowSettingsSplit => Session.Permissions.CanConfigure;

    public bool ShowCloneItem => Session.Permissions.CanClone;

    public bool ShowBackupItem => Session.Permissions.CanBackup;

    public bool ShowFirewallItem => Session.Permissions.CanConfigure;

    public bool ShowSettingsItem => Session.Permissions.CanConfigure;

    /// <summary>등록된 게스트 기능이 없으면 그 버튼은 눌리지 않는다(화면이 아직 옮겨지지 않은 탭).</summary>
    public bool CanOpenSettingsTab => HasGuestTab("hardware");

    public bool CanOpenCloneTab => HasGuestTab("clone");

    public bool CanOpenBackupTab => HasGuestTab("backup");

    public bool CanOpenFirewallTab => HasGuestTab("firewall");

    public bool CanOpenSnapshotTab => HasGuestTab("snapshots");

    private bool IsPowerAvailable(GuestPowerAction action) => GuestPowerRules.IsAvailable(action, SelectedGuest);

    private bool HasGuestTab(string id)
    {
        return SelectedGuest is { } guest && FindGuestFeature(id, guest) is not null;
    }

    private static GuestFeature? FindGuestFeature(string id, PveResource guest)
    {
        return FeatureRegistry.GuestFeatures.FirstOrDefault(f =>
            f.Id == id && (f.IsAvailable?.Invoke(guest) ?? true));
    }

    /// <summary>게스트 기능 탭을 새 화면으로 연다(WPF GuestNavigator 의 탭 선택 — 같은 이름의 등록 항목).</summary>
    private Task OpenGuestTabAsync(string id)
    {
        if (SelectedGuest is not { } guest || FindGuestFeature(id, guest) is not { } feature)
            return Task.CompletedTask;

        return AppServices.Navigator.PushViewAsync(
            $"{guest.VmId} · {guest.Name} · {L.T(feature.LabelKey)}", feature.Create(Session, guest));
    }

    /// <summary>VNC 콘솔 — 템플릿이면 안내만 한다(WPF 와 같다). 정지된 게스트도 연다(창에서 시작하면 붙는다).</summary>
    [RelayCommand]
    private async Task OpenConsoleAsync()
    {
        if (SelectedGuest is not { } guest) return;

        if (guest.IsTemplate)
        {
            await AppServices.Dialogs.AlertAsync(L.T("MainWindow_M08"), L.T("MainWindow_M07"));
            return;
        }

        await AppServices.Navigator.PushAsync(new ConsoleViewModel(Session, guest));
    }

    [RelayCommand] private Task OpenSnapshotsAsync() => OpenGuestTabAsync("snapshots");

    /// <summary>[설정] 버튼 — WPF 는 하드웨어 탭을 연다.</summary>
    [RelayCommand] private Task OpenSettingsTabAsync() => OpenGuestTabAsync("hardware");

    [RelayCommand] private Task OpenCloneTabAsync() => OpenGuestTabAsync("clone");

    [RelayCommand] private Task OpenBackupTabAsync() => OpenGuestTabAsync("backup");

    [RelayCommand] private Task OpenFirewallTabAsync() => OpenGuestTabAsync("firewall");
}
