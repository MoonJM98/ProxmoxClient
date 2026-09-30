using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>
///     게스트 옵션 목록 — 웹 UI(qemu/Options.js, lxc/Options.js) 의 줄 순서·기본값·저장 규칙을 따른다.
///     CPU·메모리·네트워크처럼 하드웨어 화면이 다루는 항목은 넣지 않는다.
/// </summary>
public static class GuestOptions
{
    private static readonly (string, string)[] LocalTimeChoices =
        [("", "LocalTime_Default"), ("1", "GuestOptions_Yes"), ("0", "GuestOptions_No")];

    private static readonly (string, string)[] ConsoleModeChoices =
        [("", "GuestOptions_ConsoleTty"), ("tty", "/dev/tty[X]"), ("console", "/dev/console"), ("shell", "shell")];

    /// <summary>CT 의 DNS 탭(웹 UI lxc/DNS.js) — 도메인·서버는 한 창에서 함께 고친다.</summary>
    public static readonly IReadOnlyList<GuestOption> CtDns =
    [
        new() { Key = "hostname", LabelKey = "MainWindow_40", CtOnly = true, Editor = OptionEditors.Hostname() },
        new() { Key = "searchdomain", LabelKey = "GuestSettingsWindow_25", CtOnly = true,
            EmptyLabelKey = "GuestOptions_UseHost", Editor = OptionEditors.Dns() },
        new() { Key = "nameserver", LabelKey = "GuestSettingsWindow_23", CtOnly = true,
            EmptyLabelKey = "GuestOptions_UseHost", Editor = OptionEditors.Dns() }
    ];

    public static readonly IReadOnlyList<GuestOption> All =
    [
        new() { Key = "name", LabelKey = "MainWindow_40", VmOnly = true },
        // VM 은 끄면 delete=onboot, CT 는 onboot=0 을 보낸다(웹 UI 와 같이)
        new() { Key = "onboot", LabelKey = "GuestSettingsWindow_03", Kind = OptionKind.Bool, VmOnly = true,
            DefaultValue = "0", DeleteDefault = true },
        new() { Key = "onboot", LabelKey = "GuestSettingsWindow_03", Kind = OptionKind.Bool, CtOnly = true },
        new() { Key = "startup", LabelKey = "GuestSettingsWindow_06", EmptyLabelKey = "Startup_Any",
            Editor = OptionEditors.Startup("startup") },
        new() { Key = "ostype", LabelKey = "GuestSettingsWindow_08", Kind = OptionKind.OsType, VmOnly = true },
        new() { Key = "ostype", LabelKey = "GuestSettingsWindow_08", CtOnly = true, ReadOnly = true,
            EmptyLabelKey = "Common_Unknown" },
        new() { Key = "arch", LabelKey = "GuestOptions_Arch", CtOnly = true, ReadOnly = true,
            EmptyLabelKey = "Common_Unknown" },
        new() { Key = "boot", LabelKey = "GuestSettingsWindow_09", Kind = OptionKind.BootOrder, VmOnly = true,
            EmptyLabelKey = "GuestOptions_NotSet", PendingKeys = ["boot", "bootdisk"] },

        // ---- VM 전용(웹 UI 옵션 화면 순서)
        DefaultOn("tablet", "GuestOptions_Tablet", vmOnly: true),
        new() { Key = "hotplug", LabelKey = "GuestOptions_Hotplug", VmOnly = true,
            EmptyLabelKey = "GuestOptions_HotplugDefault", Editor = OptionEditors.Hotplug("hotplug") },
        DefaultOn("acpi", "GuestOptions_Acpi", vmOnly: true),
        DefaultOn("kvm", "GuestOptions_Kvm", vmOnly: true),
        DefaultOff("freeze", "GuestOptions_Freeze", vmOnly: true),
        new() { Key = "localtime", LabelKey = "GuestOptions_LocalTime", Kind = OptionKind.Choice, VmOnly = true,
            EmptyLabelKey = "LocalTime_Default", Choices = LocalTimeChoices },
        new() { Key = "startdate", LabelKey = "GuestOptions_StartDate", VmOnly = true,
            EmptyLabelKey = "StartDate_Now", Editor = OptionEditors.StartDate("startdate") },
        new() { Key = "smbios1", LabelKey = "GuestOptions_Smbios", VmOnly = true,
            EmptyLabelKey = "GuestOptions_NotSet", Editor = OptionEditors.Smbios("smbios1") },
        new() { Key = "agent", LabelKey = "GuestOptions_Agent", VmOnly = true,
            EmptyLabelKey = "GuestOptions_DefaultOff", Editor = OptionEditors.Agent("agent") },

        // ---- CT 전용(웹 UI 옵션 화면 순서)
        DefaultOn("console", "GuestOptions_Console", vmOnly: false),
        new() { Key = "tty", LabelKey = "GuestOptions_Tty", CtOnly = true, EmptyLabelKey = "GuestOptions_TtyDefault" },
        new() { Key = "cmode", LabelKey = "GuestOptions_ConsoleMode", Kind = OptionKind.Choice, CtOnly = true,
            EmptyLabelKey = "GuestOptions_ConsoleTty", Choices = ConsoleModeChoices },

        new() { Key = "protection", LabelKey = "GuestSettingsWindow_04", Kind = OptionKind.Bool,
            DefaultValue = "0", DeleteDefault = true },

        new() { Key = "unprivileged", LabelKey = "GuestOptions_Unprivileged", Kind = OptionKind.Bool, CtOnly = true,
            ReadOnly = true },
        new() { Key = "features", LabelKey = "GuestOptions_Features", CtOnly = true, EmptyLabelKey = "Common_None",
            Editor = OptionEditors.Features("features") },
        new() { Key = "spice_enhancements", LabelKey = "GuestOptions_Spice", VmOnly = true,
            EmptyLabelKey = "Common_None", Editor = OptionEditors.Spice("spice_enhancements") },
        new() { Key = "vmstatestorage", LabelKey = "GuestOptions_VmState", VmOnly = true,
            EmptyLabelKey = "VmState_Auto" },

        // 웹 UI 에서도 고칠 수 없고, 설정돼 있을 때만 보인다
        new() { Key = "hookscript", LabelKey = "GuestOptions_Hookscript", ReadOnly = true,
            VisibleIf = GuestOption.WhenSet("hookscript") },
        new() { Key = "description", LabelKey = "NodeTab_Notes", EmptyLabelKey = "GuestOptions_NotSet",
            Editor = OptionEditors.Notes("description", "NodeTab_Notes") }
    ];

    /// <summary>기본 켜짐 체크 — 켜면 삭제(기본값), 끄면 0.</summary>
    private static GuestOption DefaultOn(string key, string labelKey, bool vmOnly)
    {
        return new GuestOption
        {
            Key = key, LabelKey = labelKey, Kind = OptionKind.Bool, VmOnly = vmOnly, CtOnly = !vmOnly,
            EmptyLabelKey = "GuestOptions_DefaultOn", DefaultValue = "1", DeleteDefault = true
        };
    }

    /// <summary>기본 꺼짐 체크 — 켜면 1, 끄면 삭제(기본값).</summary>
    private static GuestOption DefaultOff(string key, string labelKey, bool vmOnly)
    {
        return new GuestOption
        {
            Key = key, LabelKey = labelKey, Kind = OptionKind.Bool, VmOnly = vmOnly, CtOnly = !vmOnly,
            EmptyLabelKey = "GuestOptions_DefaultOff", DefaultValue = "0", DeleteDefault = true
        };
    }

    public static IReadOnlyList<GuestOption> For(ResourceKind kind)
    {
        return All.Where(option => option.AppliesTo(kind)).ToList();
    }
}

/// <summary>
///     Cloud-Init 항목(웹 UI qemu/CloudInit.js). 값은 VM 설정(config)에 들어가므로 옵션 탭과 같은 읽기·쓰기 경로를 쓴다.
/// </summary>
public static class CloudInitOptions
{
    /// <summary>VM 이 가질 수 있는 네트워크 장치 수(net0~net31) — 장치마다 IP 설정 줄이 하나씩 있다.</summary>
    private const int MaxNetDevices = 32;

    public static readonly IReadOnlyList<GuestOption> All =
    [
        new() { Key = "ciuser", LabelKey = "CloudInit_User", VmOnly = true, EmptyLabelKey = "CloudInit_ImageDefault" },
        new() { Key = "cipassword", LabelKey = "CloudInit_Password", VmOnly = true, EmptyLabelKey = "Common_None",
            Editor = OptionEditors.Password("cipassword", "CloudInit_Password") },
        new() { Key = "searchdomain", LabelKey = "GuestSettingsWindow_25", VmOnly = true,
            EmptyLabelKey = "GuestOptions_UseHost", Editor = OptionEditors.Dns() },
        new() { Key = "nameserver", LabelKey = "GuestSettingsWindow_23", VmOnly = true,
            EmptyLabelKey = "GuestOptions_UseHost", Editor = OptionEditors.Dns() },
        new() { Key = "sshkeys", LabelKey = "CloudInit_SshKeys", VmOnly = true, EmptyLabelKey = "Common_None",
            Editor = OptionEditors.SshKeys("sshkeys") },
        // 웹 UI 는 늘 ciupgrade=1/0 을 보낸다(삭제하지 않음). 설정이 없으면 서버 기본값은 켜짐
        new() { Key = "ciupgrade", LabelKey = "CloudInit_Upgrade", Kind = OptionKind.Bool, VmOnly = true,
            EmptyLabelKey = "GuestOptions_DefaultOn", DefaultValue = "1" },
        .. Enumerable.Range(0, MaxNetDevices).Select(IpConfig)
    ];

    private static GuestOption IpConfig(int index)
    {
        return new GuestOption
        {
            Key = $"ipconfig{index}", LabelKey = "CloudInit_IpConfigN", LabelArg = $"net{index}", VmOnly = true,
            EmptyLabelKey = "GuestOptions_NotSet", Editor = OptionEditors.IpConfig($"ipconfig{index}"),
            VisibleIf = GuestOption.WhenSet($"net{index}"), PendingKeys = [$"ipconfig{index}", $"net{index}"]
        };
    }
}
