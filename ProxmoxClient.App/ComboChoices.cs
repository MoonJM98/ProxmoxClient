using System.Windows.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App;

/// <summary>
///     값(Tag)과 표시 라벨을 분리한 ComboBox 선택지 도우미 + Proxmox 고정 선택지 목록.
///     라벨에는 리소스 키를 넣고 <see cref="Fill" /> 이 채울 때 번역한다(정적 목록이라 미리 번역하면 언어 전환이 반영되지 않는다).
///     제품명처럼 번역이 필요 없는 라벨은 키가 없으므로 원문 그대로 표시된다.
/// </summary>
internal static class ComboChoices
{
    public static readonly (string Value, string Label)[] QemuOsTypes =
    [
        ("", "ComboChoices_M01"),
        ("win11", "Windows 11 / 2022 / 2025"),
        ("win10", "Windows 10 / 2016 / 2019"),
        ("win8", "Windows 8.x / 2012 / 2012 R2"),
        ("win7", "Windows 7 / 2008 R2"),
        ("wvista", "Windows Vista"),
        ("w2k8", "Windows 2008"),
        ("w2k3", "Windows 2003"),
        ("wxp", "Windows XP"),
        ("w2k", "Windows 2000"),
        ("l26", "ComboChoices_M02"),
        ("l24", "ComboChoices_M03"),
        ("solaris", "Solaris / OpenSolaris / OpenIndiana"),
        ("other", "ComboChoices_M04")
    ];

    public static readonly (string Value, string Label)[] NicModels =
    [
        ("virtio", "Choice_NicVirtio"),
        ("e1000", "Intel E1000"),
        ("e1000e", "Intel E1000E"),
        ("rtl8139", "Realtek RTL8139"),
        ("vmxnet3", "VMware vmxnet3")
    ];

    public static readonly (string Value, string Label)[] FirewallActions =
    [
        ("ACCEPT", "Choice_FwAccept"),
        ("DROP", "Choice_FwDrop"),
        ("REJECT", "Choice_FwReject")
    ];

    public static readonly (string Value, string Label)[] FirewallDirections =
    [
        ("in", "Choice_FwIn"),
        ("out", "Choice_FwOut")
    ];

    public static readonly (string Value, string Label)[] FirewallProtocols =
    [
        ("", "Choice_FwAllProtocols"),
        ("tcp", "TCP"),
        ("udp", "UDP"),
        ("icmp", "ICMP"),
        ("ipv6-icmp", "ICMPv6"),
        ("sctp", "SCTP"),
        ("gre", "GRE"),
        ("esp", "ESP"),
        ("ah", "AH")
    ];

    public static void Fill(ComboBox box, IEnumerable<(string Value, string Label)> items)
    {
        box.Items.Clear();
        foreach (var (value, label) in items)
            // 키가 없는 라벨(제품명 등)은 Loc.T 가 원문을 그대로 돌려준다
            box.Items.Add(new ComboBoxItem { Content = Loc.T(label), Tag = value });
    }

    /// <summary>값으로 선택. 목록에 없는 값(서버에만 존재)은 원문 그대로 항목을 추가해 보존한다.</summary>
    public static void Select(ComboBox box, string value)
    {
        var match = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, value, StringComparison.OrdinalIgnoreCase));
        if (match is null && value.Length > 0)
        {
            match = new ComboBoxItem { Content = value, Tag = value };
            box.Items.Add(match);
        }

        box.SelectedItem = match ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    public static string Selected(ComboBox box)
    {
        return (box.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
    }
}