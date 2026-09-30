using System.Text.RegularExpressions;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>부팅 순서 편집의 한 줄 — 장치 이름, 설정 요약, 부팅에 쓸지 여부.</summary>
public sealed record BootDevice(string Name, string Description, bool Enabled);

/// <summary>
///     VM 부팅 순서(boot) 해석·조립 — 웹 UI 의 부팅 순서 편집과 같은 규칙.
///     새 형식은 "order=scsi0;ide2;net0", 옛 형식은 "cdn"(c=bootdisk, d=CD-ROM, n=네트워크) 문자열이다.
/// </summary>
internal static partial class BootOrder
{
    /// <summary>boot 가 비었을 때 서버가 쓰는 옛 형식 기본값(디스크 → CD → 네트워크).</summary>
    private const string LegacyDefault = "cdn";

    [GeneratedRegex(@"^(ide|sata|scsi|virtio)\d+$")]
    private static partial Regex DiskKey();

    [GeneratedRegex(@"^(net|usb|hostpci)\d+$")]
    private static partial Regex OtherBootKey();

    [GeneratedRegex(@"vm-.*-cloudinit")]
    private static partial Regex CloudInitVolume();

    /// <summary>옛 형식은 앞 네 글자만 본다(웹 UI 와 같다).</summary>
    private const int MaxLegacyLetters = 4;

    /// <summary>부팅할 수 있는 장치인가 — 디스크·CD(CloudInit 제외), 네트워크, PCI 통과, USB(SPICE 제외).</summary>
    public static bool IsBootDevice(string key, string value)
    {
        if (DiskKey().IsMatch(key)) return !CloudInitVolume().IsMatch(value);
        if (key.StartsWith("usb", StringComparison.Ordinal)) return !value.Contains("spice", StringComparison.Ordinal);
        return OtherBootKey().IsMatch(key);
    }

    /// <summary>
    ///     편집 목록 — 부팅에 쓰는 장치를 순서대로 먼저, 나머지 부팅 가능 장치를 이름순으로 뒤에(꺼진 채로) 놓는다.
    /// </summary>
    public static IReadOnlyList<BootDevice> Parse(string? boot, IReadOnlyDictionary<string, string> config)
    {
        // 이제 없는 장치가 order 에 남아 있으면 서버가 저장을 거부하므로 목록에서 뺀다
        var enabled = EnabledDevices(boot, config).Where(config.ContainsKey).ToList();
        var rest = config.Where(kv => IsBootDevice(kv.Key, kv.Value)).Select(kv => kv.Key).Except(enabled)
            .OrderBy(SortKey, StringComparer.Ordinal);
        return
        [
            .. enabled.Select(name => new BootDevice(name, Summary(config, name), true)),
            .. rest.Select(name => new BootDevice(name, Summary(config, name), false))
        ];
    }

    /// <summary>서버에 보낼 값 — 켜진 장치만 순서대로.</summary>
    public static string Format(IEnumerable<BootDevice> devices)
    {
        return "order=" + string.Join(';', devices.Where(d => d.Enabled).Select(d => d.Name));
    }

    /// <summary>목록에 보일 문구 — 예: "scsi0, ide2, net0".</summary>
    public static string Describe(string? boot, IReadOnlyDictionary<string, string> config)
    {
        return string.Join(", ", EnabledDevices(boot, config));
    }

    /// <summary>부팅에 쓰는 장치를 순서대로. 설정에 없는 장치(지운 디스크 등)도 서버 값 그대로 남긴다.</summary>
    private static IReadOnlyList<string> EnabledDevices(string? boot, IReadOnlyDictionary<string, string> config)
    {
        var value = PropertyString.Parse(string.IsNullOrWhiteSpace(boot) ? LegacyDefault : boot, "legacy");
        if (value["order"] is { } order)
            return order.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct().ToList();

        // 옛 형식: 글자마다 장치 종류
        return value.Get("legacy").Take(MaxLegacyLetters).SelectMany(letter => LegacyDevices(letter, config))
            .Distinct().ToList();
    }

    private static IEnumerable<string> LegacyDevices(char letter, IReadOnlyDictionary<string, string> config)
    {
        var disks = config.Where(kv => DiskKey().IsMatch(kv.Key))
            .OrderBy(kv => SortKey(kv.Key), StringComparer.Ordinal);
        return letter switch
        {
            'c' when config.TryGetValue("bootdisk", out var disk) && config.ContainsKey(disk) => [disk],
            'd' => disks.Where(kv => IsCdrom(kv.Value)).Select(kv => kv.Key),
            'n' => config.Keys.Where(k => k.StartsWith("net", StringComparison.Ordinal) && OtherBootKey().IsMatch(k))
                .OrderBy(SortKey, StringComparer.Ordinal),
            _ => []
        };
    }

    private static bool IsCdrom(string value)
    {
        return value.Split(',').Any(p => p.Trim() == "media=cdrom");
    }

    /// <summary>설정 요약 — 첫 항목(저장소:볼륨 또는 모델=MAC)만.</summary>
    private static string Summary(IReadOnlyDictionary<string, string> config, string name)
    {
        return config.TryGetValue(name, out var value) ? value.Split(',')[0] : string.Empty;
    }

    /// <summary>scsi2 가 scsi10 보다 앞에 오게 숫자 부분을 채워 정렬한다.</summary>
    private static string SortKey(string name)
    {
        var digits = name.Length - name.Reverse().TakeWhile(char.IsDigit).Count();
        return name[..digits] + name[digits..].PadLeft(4, '0');
    }
}
