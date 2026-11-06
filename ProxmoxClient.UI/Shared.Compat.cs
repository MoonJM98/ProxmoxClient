using Avalonia.Controls;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI;

// WPF 앱(ProxmoxClient.App 네임스페이스)의 작은 공용 도우미를 같은 이름으로 옮겼다 — 옮겨 오는 코드가 그대로 쓴다.

public enum GraphValueUnit
{
    Number,
    Percent,
    BytesPerSecond
}

public enum GraphAxis
{
    Left,
    Right
}

public sealed record GraphSeries(
    string Name,
    IReadOnlyList<double?> Values,
    Color Color,
    GraphAxis Axis,
    GraphValueUnit Unit)
{
    public bool HasData => Values.Any(value => value is not null);
}

/// <summary>콤보 상자 선택지 — WPF ComboChoices 와 같은 목록·같은 사용법(Fill · Select · Selected).</summary>
public static class ComboChoices
{
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
        foreach (var (value, label) in items) box.Items.Add(new ComboBoxItem { Content = Loc.T(label), Tag = value });
    }

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
