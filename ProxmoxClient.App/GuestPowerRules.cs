using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App;

public enum GuestPowerAction
{
    None,
    Start,
    Shutdown,
    Stop,
    Reboot,
    Suspend,
    Resume,
    Hibernate
}

/// <summary>콘솔 창 등에서 특정 게스트의 전원 동작을 실행하는 대리자(메인 뷰모델이 제공).</summary>
public delegate Task GuestPowerRunner(PveResource guest, GuestPowerAction action);

/// <summary>게스트 상태별로 의미 있는 전원 동작 판정 — 메인 화면·메뉴·콘솔 창이 같은 규칙을 쓴다.</summary>
public static class GuestPowerRules
{
    private const string StatusPaused = "paused";

    public static bool IsAvailable(GuestPowerAction action, PveResource guest)
    {
        if (guest.IsTemplate) return false; // 템플릿은 전원 동작 불가

        var running = guest.IsRunning;
        var paused = string.Equals(guest.Status, StatusPaused, StringComparison.OrdinalIgnoreCase);
        var isVm = guest.Kind == ResourceKind.Qemu;

        return action switch
        {
            GuestPowerAction.Start => !running && !paused,
            GuestPowerAction.Shutdown => running,
            GuestPowerAction.Stop => running || paused,
            GuestPowerAction.Reboot => running,
            GuestPowerAction.Suspend => running && isVm,
            GuestPowerAction.Resume => paused && isVm,
            GuestPowerAction.Hibernate => running && isVm,
            _ => false
        };
    }

    public static string Label(GuestPowerAction action)
    {
        return action switch
        {
            GuestPowerAction.Start => Loc.T("GuestPower_Start"),
            GuestPowerAction.Shutdown => Loc.T("GuestPower_Shutdown"),
            GuestPowerAction.Stop => Loc.T("GuestPower_Stop"),
            GuestPowerAction.Reboot => Loc.T("GuestPower_Reboot"),
            GuestPowerAction.Suspend => Loc.T("GuestPower_Suspend"),
            GuestPowerAction.Resume => Loc.T("GuestPower_Resume"),
            GuestPowerAction.Hibernate => Loc.T("GuestPower_Hibernate"),
            _ => string.Empty
        };
    }
}