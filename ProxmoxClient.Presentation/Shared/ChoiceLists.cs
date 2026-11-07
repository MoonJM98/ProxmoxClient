namespace ProxmoxClient.Presentation.Shared;

/// <summary>
///     Proxmox 고정 선택지 목록(값, 라벨). 라벨에는 리소스 키를 넣고 채울 때 번역한다 — 제품명처럼 키가 없는 라벨은 원문 그대로.
/// </summary>
public static class ChoiceLists
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
}
