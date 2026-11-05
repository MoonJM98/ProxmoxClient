using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Views.Main;

public enum GuestPowerAction
{
    None,
    Start,
    Shutdown,
    Stop,
    Reboot,
    Suspend,
    Resume,
    Hibernate,

    /// <summary>VM 강제 재설정(전원 리셋 버튼) — 게스트 OS 를 거치지 않는다.</summary>
    Reset
}

/// <summary>게스트 상태별로 의미 있는 전원 동작 판정 — WPF GuestPowerRules 와 같은 규칙.</summary>
public static class GuestPowerRules
{
    private const string StatusPaused = "paused";

    public static bool IsAvailable(GuestPowerAction action, PveResource? guest)
    {
        if (guest is null || guest.IsTemplate) return false; // 템플릿은 전원 동작 불가

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
            GuestPowerAction.Reset => running && isVm,
            _ => false
        };
    }

    public static string Label(GuestPowerAction action)
    {
        return action switch
        {
            GuestPowerAction.Start => L.T("GuestPower_Start"),
            GuestPowerAction.Shutdown => L.T("GuestPower_Shutdown"),
            GuestPowerAction.Stop => L.T("GuestPower_Stop"),
            GuestPowerAction.Reboot => L.T("GuestPower_Reboot"),
            GuestPowerAction.Suspend => L.T("GuestPower_Suspend"),
            GuestPowerAction.Resume => L.T("GuestPower_Resume"),
            GuestPowerAction.Hibernate => L.T("GuestPower_Hibernate"),
            GuestPowerAction.Reset => L.T("GuestPower_Reset"),
            _ => string.Empty
        };
    }
}
