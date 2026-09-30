using System.Text;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>
///     옵션 탭의 여러 칸 편집기 — 웹 UI(pve-manager) 편집 창과 같은 칸·값 형식·표시를 쓴다.
///     반환하는 변경 목록에서 빈 값은 "삭제(서버 기본값으로)" 를 뜻한다.
/// </summary>
internal static partial class OptionEditors
{
    private static IReadOnlyDictionary<string, string> One(string key, string value)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };
    }

    private static string V(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
    }

    private static bool IsNonNegativeIntOrEmpty(string text)
    {
        return text.Length == 0 || int.TryParse(text, out var v) && v >= 0;
    }

    // ------------------------------------------------------------ 시작/종료 순서 (startup)

    /// <summary>시작/종료 순서 — order·up·down 세 칸, 모두 비우면 삭제. 예: order=1,up=30,down=60</summary>
    public static OptionEditor Startup(string key)
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            {
                var p = PropertyString.Parse(raw, "order");
                return
                [
                    new FormField { Key = "order", LabelKey = "Startup_Order", Initial = p.Get("order"),
                        Hint = Loc.T("Startup_OrderHint") },
                    new FormField { Key = "up", LabelKey = "Startup_Up", Initial = p.Get("up"),
                        Hint = Loc.T("Startup_DefaultHint") },
                    new FormField { Key = "down", LabelKey = "Startup_Down", Initial = p.Get("down"),
                        Hint = Loc.T("Startup_DefaultHint") }
                ];
            },
            Validate = values => new[] { "order", "up", "down" }.All(k => IsNonNegativeIntOrEmpty(V(values, k)))
                ? null
                : Loc.T("Startup_NumbersOnly"),
            Build = (values, _) => One(key, FormatStartup(V(values, "order"), V(values, "up"), V(values, "down"))),
            Display = DescribeStartup
        };
    }

    internal static string FormatStartup(string order, string up, string down)
    {
        var parts = new[] { ("order", order), ("up", up), ("down", down) }
            .Where(p => p.Item2.Length > 0).Select(p => $"{p.Item1}={p.Item2}");
        return string.Join(',', parts);
    }

    /// <summary>웹 UI 표시: "order=any" 또는 "order=N", 뒤에 ",up=N" ",down=N".</summary>
    internal static string DescribeStartup(string raw)
    {
        var p = PropertyString.Parse(raw, "order");
        var text = "order=" + (p["order"] is { Length: > 0 } o ? o : "any");
        if (p["up"] is { Length: > 0 } up) text += $",up={up}";
        if (p["down"] is { Length: > 0 } down) text += $",down={down}";
        return text;
    }

    // ------------------------------------------------------------ 핫플러그 (hotplug)

    private static readonly (string Value, string Label)[] HotplugFeatures =
    [
        ("disk", "Hotplug_Disk"), ("network", "Hotplug_Network"), ("usb", "Hotplug_Usb"),
        ("memory", "Hotplug_Memory"), ("cpu", "Hotplug_Cpu")
    ];

    /// <summary>핫플러그 — 체크 목록. 아무것도 안 고르면 hotplug=0(끔).</summary>
    public static OptionEditor Hotplug(string key)
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            [
                new FormField
                {
                    Key = "features", LabelKey = "GuestOptions_Hotplug", Kind = FormFieldKind.MultiChoice,
                    Choices = HotplugFeatures, Initial = string.Join(',', HotplugSet(raw))
                }
            ],
            Build = (values, _) =>
            {
                var chosen = V(values, "features").Split(',', StringSplitOptions.RemoveEmptyEntries);
                var ordered = HotplugFeatures.Select(f => f.Value).Where(chosen.Contains).ToList();
                return One(key, ordered.Count == 0 ? "0" : string.Join(',', ordered));
            },
            Display = DescribeHotplug
        };
    }

    /// <summary>켜진 기능 — 비었거나 1 이면 기본(disk·network·usb), 0 이면 없음.</summary>
    private static IReadOnlyList<string> HotplugSet(string raw)
    {
        return raw switch
        {
            "" or "1" => ["disk", "network", "usb"],
            "0" => [],
            _ => raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };
    }

    internal static string DescribeHotplug(string raw)
    {
        var set = HotplugSet(raw);
        if (set.Count == 0) return Loc.T("Common_Disabled");
        return string.Join(", ", HotplugFeatures.Where(f => set.Contains(f.Value)).Select(f => Loc.T(f.Label)));
    }

    // ------------------------------------------------------------ QEMU 게스트 에이전트 (agent)

    /// <summary>게스트 에이전트 — 사용·trim·파일시스템 동결(기본 켬)·종류(고급).</summary>
    public static OptionEditor Agent(string key)
    {
        return new OptionEditor
        {
            Fields = (raw, _) =>
            {
                var p = PropertyString.Parse(raw, "enabled");
                var freeze = p["freeze-fs"] ?? p["freeze-fs-on-backup"]; // 옛 키도 같은 뜻
                return
                [
                    new FormField { Key = "enabled", LabelKey = "Agent_Enabled", Kind = FormFieldKind.Bool,
                        Initial = p.IsOn("enabled") ? "1" : "0" },
                    new FormField { Key = "fstrim", LabelKey = "Agent_Fstrim", Kind = FormFieldKind.Bool,
                        Initial = p.IsOn("fstrim_cloned_disks") ? "1" : "0" },
                    new FormField { Key = "freeze", LabelKey = "Agent_FreezeFs", Kind = FormFieldKind.Bool,
                        Initial = freeze is "0" ? "0" : "1", Hint = Loc.T("Agent_FreezeFsHint") },
                    new FormField
                    {
                        Key = "type", LabelKey = "Agent_Type", Kind = FormFieldKind.Choice, Advanced = true,
                        Choices = [("", "Agent_TypeDefault"), ("virtio", "VirtIO"), ("isa", "ISA")],
                        Initial = p.Get("type")
                    }
                ];
            },
            Build = (values, _) => One(key, FormatAgent(V(values, "enabled") == "1", V(values, "fstrim") == "1",
                V(values, "freeze") == "1", V(values, "type"))),
            Display = DescribeAgent
        };
    }

    /// <summary>enabled 를 맨 앞에, 나머지는 이름순 — 예: 1,freeze-fs=0,fstrim_cloned_disks=1,type=isa</summary>
    internal static string FormatAgent(bool enabled, bool fstrim, bool freezeFs, string type)
    {
        var parts = new List<string> { enabled ? "1" : "0" };
        if (enabled && !freezeFs) parts.Add("freeze-fs=0");
        if (enabled && fstrim) parts.Add("fstrim_cloned_disks=1");
        if (type.Length > 0) parts.Add($"type={type}");
        return string.Join(',', parts);
    }

    internal static string DescribeAgent(string raw)
    {
        var p = PropertyString.Parse(raw, "enabled");
        if (!p.IsOn("enabled")) return Loc.T("Common_Disabled");

        var text = new StringBuilder(Loc.T("Common_Enabled"));
        foreach (var (k, v) in p.Items.Where(kv => kv.Key != "enabled"))
        {
            // 파일시스템 동결은 켜진 게 기본이라 꺼졌을 때만 보인다
            if (k is "freeze-fs" or "freeze-fs-on-backup" && v != "0") continue;
            var value = k == "type" ? v == "isa" ? "ISA" : "VirtIO"
                : Loc.T(v is "1" or "on" ? "Common_Enabled" : "Common_Disabled");
            text.Append($", {k}: {value}");
        }

        return text.ToString();
    }

    // ------------------------------------------------------------ SPICE 향상 (spice_enhancements)

    public static OptionEditor Spice(string key)
    {
        return new OptionEditor
        {
            Fields = (raw, config) =>
            {
                var p = PropertyString.Parse(raw);
                var vga = PropertyString.Parse(config.TryGetValue("vga", out var v) ? v : string.Empty, "type");
                var isSpice = vga.Get("type") is "qxl" or "qxl2" or "qxl3" or "qxl4";
                return
                [
                    new FormField { Key = "foldersharing", LabelKey = "Spice_FolderSharing",
                        Kind = FormFieldKind.Bool, Initial = p.IsOn("foldersharing") ? "1" : "0",
                        Hint = isSpice ? null : Loc.T("Spice_NeedsSpiceDisplay") },
                    new FormField
                    {
                        Key = "videostreaming", LabelKey = "Spice_VideoStreaming", Kind = FormFieldKind.Choice,
                        Choices = [("off", "off"), ("all", "all"), ("filter", "filter")],
                        Initial = p.Get("videostreaming", "off")
                    }
                ];
            },
            Build = (values, _) =>
            {
                var parts = new List<string>();
                if (V(values, "foldersharing") == "1") parts.Add("foldersharing=1");
                var mode = V(values, "videostreaming");
                if (mode is "all" or "filter") parts.Add($"videostreaming={mode}");
                return One(key, string.Join(',', parts));
            },
            Display = raw =>
            {
                var p = PropertyString.Parse(raw);
                var parts = new List<string>();
                if (p.IsOn("foldersharing")) parts.Add(Loc.T("Spice_FolderSharingOn"));
                if (p["videostreaming"] is { Length: > 0 } mode and not "off")
                    parts.Add(Loc.T("Spice_VideoStreamingIs", mode));
                return parts.Count == 0 ? Loc.T("Common_None") : string.Join(", ", parts);
            }
        };
    }
}
