using System.Globalization;
using System.Text.RegularExpressions;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>
///     VM 장치 편집기 — 네트워크·시리얼·오디오·VirtIO RNG. 값 형식은 웹 UI(NetworkEdit·SerialEdit·AudioEdit·RNGEdit)와 같다.
///     USB·PCI·Virtiofs 는 <c>PassthroughEditors</c>.
/// </summary>
internal static partial class DeviceEditors
{
    private const int MaxNet = 32;
    private const int MaxSerial = 4;

    public static readonly IReadOnlyList<(string Value, string Label)> NicModels =
    [
        ("e1000", "Intel E1000"), ("e1000e", "Intel E1000E"), ("virtio", "VirtIO (paravirtualized)"),
        ("rtl8139", "Realtek RTL8139"), ("vmxnet3", "VMware vmxnet3")
    ];

    [GeneratedRegex("^([a-fA-F0-9]{2}:){5}[a-fA-F0-9]{2}$")]
    private static partial Regex MacPattern();

    private static string V(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
    }

    private static IReadOnlyDictionary<string, string> One(string key, string value)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };
    }

    // ------------------------------------------------------------ 네트워크

    /// <summary>
    ///     네트워크 장치 — 브리지·VLAN·방화벽·모델·MAC, 고급: 연결 끊기·MTU·속도 제한·멀티큐.
    ///     저장 순서(printQemuNetwork): 모델[=MAC], bridge, tag, firewall, rate, queues, link_down, trunks, mtu.
    ///     새로 만들면 방화벽은 켜고, 모델은 OS 에 맞춘다(Linux virtio, Win2000·XP rtl8139, 그 밖 e1000).
    /// </summary>
    public static async Task<HardwareEdit> NetworkAsync(HardwareContext ctx, string? key)
    {
        var bridges = await ctx.BridgeChoicesAsync();
        var net = key is null ? PropertyString.Empty : PropertyString.Parse(ctx.Get(key));
        var model = key is null ? DefaultModel(ctx.Get("ostype")) : net.Items.FirstOrDefault().Key ?? "virtio";
        var target = key ?? ctx.FreeSlot("net", MaxNet);
        return new HardwareEdit
        {
            Title = key is null ? Loc.T("HwAdd_Network") : Loc.T("Hw_Net", key),
            Fields =
            [
                new FormField { Key = "bridge", LabelKey = "GuestSettingsWindow_17", Kind = FormFieldKind.Choice,
                    Choices = [.. bridges, .. Missing(bridges, net.Get("bridge"))], Required = true,
                    Initial = key is null ? bridges.FirstOrDefault().Value ?? "" : net.Get("bridge") },
                new FormField { Key = "tag", LabelKey = "GuestSettingsWindow_21", Initial = net.Get("tag"),
                    Hint = Loc.T("HwEd_NoVlan") },
                new FormField { Key = "firewall", LabelKey = "FirewallWindow_01", Kind = FormFieldKind.Bool,
                    Initial = key is null || net.IsOn("firewall") ? "1" : "0" },
                new FormField { Key = "model", LabelKey = "GuestSettingsWindow_18", Kind = FormFieldKind.Choice,
                    Choices = [.. NicModels, .. Missing(NicModels, model)], Initial = model },
                new FormField { Key = "mac", LabelKey = "GuestSettingsWindow_19", Initial = net.Get(model),
                    Hint = Loc.T("HwEd_MacAuto") },
                new FormField { Key = "link_down", LabelKey = "HwEd_Disconnect", Kind = FormFieldKind.Bool,
                    Advanced = true, Initial = net.IsOn("link_down") ? "1" : "0" },
                new FormField { Key = "mtu", LabelKey = "HwEd_Mtu", Advanced = true, Initial = net.Get("mtu"),
                    Hint = Loc.T("HwEd_MtuHint") },
                new FormField { Key = "rate", LabelKey = "HwEd_RateLimit", Advanced = true, Initial = net.Get("rate"),
                    Hint = Loc.T("HwEd_UnlimitedHint") },
                new FormField { Key = "queues", LabelKey = "HwEd_Multiqueue", Advanced = true,
                    Initial = net.Get("queues") }
            ],
            Validate = values => target is null ? Loc.T("HwEd_NoFreeSlot", "net") : ValidateNetwork(values),
            Build = values => One(target!, FormatNetwork(values, net.Get("trunks")))
        };
    }

    private static IEnumerable<(string, string)> Missing(IReadOnlyList<(string Value, string Label)> list, string value)
    {
        return value.Length == 0 || list.Any(i => i.Value == value) ? [] : [(value, value)];
    }

    private static string DefaultModel(string ostype)
    {
        return ostype switch { "l26" => "virtio", "w2k" or "wxp" => "rtl8139", _ => "e1000" };
    }

    private static string? ValidateNetwork(IReadOnlyDictionary<string, string> values)
    {
        if (V(values, "mac") is { Length: > 0 } mac && !MacPattern().IsMatch(mac)) return Loc.T("HwEd_MacInvalid");
        if (V(values, "tag") is { Length: > 0 } tag && !(int.TryParse(tag, out var t) && t is >= 1 and <= 4094))
            return Loc.T("GuestSettings_VlanRange");
        if (V(values, "mtu") is { Length: > 0 } mtu
            && !(int.TryParse(mtu, out var m) && (m == 1 || m is >= 576 and <= 65520)))
            return Loc.T("HwEd_MtuRange");
        if (V(values, "mtu").Length > 0 && V(values, "model") != "virtio") return Loc.T("HwEd_MtuVirtioOnly");
        if (V(values, "queues") is { Length: > 0 } q && !(int.TryParse(q, out var n) && n is >= 1 and <= 64))
            return Loc.T("HwEd_QueuesRange");
        return V(values, "rate") is { Length: > 0 } rate
               && !(double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
                    && r is >= 0 and <= 10240)
            ? Loc.T("HwEd_RateRange")
            : null;
    }

    /// <summary>웹 UI printQemuNetwork 순서. trunks 는 화면에 없어도 원래 값을 남긴다.</summary>
    internal static string FormatNetwork(IReadOnlyDictionary<string, string> values, string trunks)
    {
        var mac = V(values, "mac");
        var parts = new List<string> { mac.Length > 0 ? $"{V(values, "model")}={mac}" : V(values, "model") };
        var bridge = V(values, "bridge");
        if (bridge.Length > 0)
        {
            parts.Add($"bridge={bridge}");
            if (V(values, "tag") is { Length: > 0 } tag and not "0") parts.Add($"tag={tag}");
            if (V(values, "firewall") == "1") parts.Add("firewall=1");
        }

        if (V(values, "rate").Length > 0) parts.Add($"rate={V(values, "rate")}");
        if (V(values, "queues").Length > 0) parts.Add($"queues={V(values, "queues")}");
        if (V(values, "link_down") == "1") parts.Add("link_down=1");
        if (trunks.Length > 0) parts.Add($"trunks={trunks}");
        if (V(values, "mtu").Length > 0) parts.Add($"mtu={V(values, "mtu")}");
        return string.Join(',', parts);
    }

    // ------------------------------------------------------------ 시리얼 · 오디오 · RNG

    /// <summary>시리얼 포트 — 번호만 고른다(serialN=socket). 기존 포트는 편집 창이 없다.</summary>
    public static HardwareEdit Serial(HardwareContext ctx)
    {
        var free = Enumerable.Range(0, MaxSerial).Select(i => $"serial{i}").Where(k => !ctx.Effective.ContainsKey(k)
            && !ctx.Config.Current.ContainsKey(k)).Select(k => (k, k)).ToList();
        return new HardwareEdit
        {
            Title = Loc.T("HwAdd_Serial"),
            Fields =
            [
                new FormField { Key = "port", LabelKey = "HwEd_SerialPort", Kind = FormFieldKind.Choice, Choices = free,
                    Initial = free.FirstOrDefault().Item1 ?? "", Required = true }
            ],
            Build = values => One(V(values, "port"), "socket")
        };
    }

    /// <summary>오디오 — audio0=device=…,driver=… (키 이름순). 기본 ich9-intel-hda + spice.</summary>
    public static HardwareEdit Audio(HardwareContext ctx)
    {
        var audio = PropertyString.Parse(ctx.Get("audio0"));
        return new HardwareEdit
        {
            Title = Loc.T("Hw_Audio"),
            Fields =
            [
                new FormField { Key = "device", LabelKey = "HwEd_AudioDevice", Kind = FormFieldKind.Choice,
                    Choices = [("ich9-intel-hda", "ich9-intel-hda"), ("intel-hda", "intel-hda"), ("AC97", "AC97")],
                    Initial = audio.Get("device", "ich9-intel-hda") },
                new FormField { Key = "driver", LabelKey = "HwEd_AudioDriver", Kind = FormFieldKind.Choice,
                    Choices = [("spice", "SPICE"), ("none", "HwEd_AudioNone")], Initial = audio.Get("driver", "spice") }
            ],
            Build = values => One("audio0", $"device={V(values, "device")},driver={V(values, "driver")}")
        };
    }

    /// <summary>
    ///     VirtIO RNG — 엔트로피 원본·제한(바이트/주기)·주기(ms). 제한 칸이 비면 제한 없음(max_bytes=0),
    ///     1024 이고 주기가 비면 기본이라 뺀다. 키 이름순.
    /// </summary>
    public static HardwareEdit Rng(HardwareContext ctx)
    {
        var rng = PropertyString.Parse(ctx.Get("rng0"));
        var maxBytes = rng.Get("max_bytes", "1024");
        return new HardwareEdit
        {
            Title = "VirtIO RNG",
            Fields =
            [
                new FormField { Key = "source", LabelKey = "HwEd_RngSource", Kind = FormFieldKind.Choice,
                    Choices =
                    [
                        ("/dev/urandom", "/dev/urandom"), ("/dev/random", "/dev/random"),
                        ("/dev/hwrng", "/dev/hwrng")
                    ],
                    Initial = rng.Get("source", "/dev/urandom") },
                new FormField { Key = "max_bytes", LabelKey = "HwEd_RngLimit",
                    Initial = maxBytes == "0" ? "" : maxBytes,
                    Hint = Loc.T("HwEd_RngLimitHint") },
                new FormField { Key = "period", LabelKey = "HwEd_RngPeriod", Initial = rng.Get("period"),
                    Hint = Loc.T("HwEd_RngPeriodHint") }
            ],
            Validate = values => IsNumberOrEmpty(V(values, "max_bytes"), 0) && IsNumberOrEmpty(V(values, "period"), 1)
                ? null
                : Loc.T("HwEd_RngInvalid"),
            Build = values => One("rng0", FormatRng(V(values, "source"), V(values, "max_bytes"), V(values, "period")))
        };
    }

    private static bool IsNumberOrEmpty(string text, int min)
    {
        return text.Length == 0 || long.TryParse(text, out var v) && v >= min;
    }

    internal static string FormatRng(string source, string maxBytes, string period)
    {
        var parts = new List<string>();
        var bytes = maxBytes.Length == 0 ? "0" : maxBytes;
        if (!(bytes == "1024" && period.Length == 0)) parts.Add($"max_bytes={bytes}");
        if (period.Length > 0) parts.Add($"period={period}");
        parts.Add($"source={source}");
        return string.Join(',', parts);
    }
}
