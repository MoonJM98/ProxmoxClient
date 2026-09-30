using System.Globalization;
using System.Text;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>
///     VM 하드웨어 목록의 값 표시 — 웹 UI(qemu/HardwareView.js, Utils.js) 의 렌더러와 같은 규칙.
/// </summary>
internal static class HardwareRender
{
    private const long MiB = 1024 * 1024;

    /// <summary>웹 UI 의 기본 머신 버전(설정 없는 Windows VM 의 implicit 버전 계산 기준).</summary>
    private const string BaseMachineVersion = "5.1";

    private static readonly string[] WindowsTypes = ["w2k", "wxp", "w2k8", "win7", "win8", "win10", "win11"];

    public static readonly IReadOnlyList<(string Value, string Label)> ScsiControllers =
    [
        ("lsi", "LSI 53C895A"), ("lsi53c810", "LSI 53C810"), ("megasas", "MegaRAID SAS 8708EM2"),
        ("virtio-scsi-pci", "VirtIO SCSI"), ("virtio-scsi-single", "VirtIO SCSI single"), ("pvscsi", "VMware PVSCSI")
    ];

    public static readonly IReadOnlyList<(string Value, string Label)> VgaTypes =
    [
        ("std", "Standard VGA"), ("vmware", "VMware compatible"), ("qxl", "SPICE"), ("qxl2", "SPICE dual monitor"),
        ("qxl3", "SPICE three monitors"), ("qxl4", "SPICE four monitors"), ("serial0", "Serial terminal 0"),
        ("serial1", "Serial terminal 1"), ("serial2", "Serial terminal 2"), ("serial3", "Serial terminal 3"),
        ("virtio", "VirtIO-GPU"), ("virtio-gl", "VirGL GPU"), ("none", "none")
    ];

    /// <summary>웹 UI 의 format_size — IEC 단위, 소수 둘째 자리("32.00 GiB"). 바이트는 정수.</summary>
    public static string FormatSize(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString("0", CultureInfo.InvariantCulture)} B"
            : $"{bytes.ToString("0.00", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string Get(IReadOnlyDictionary<string, string> c, string key)
    {
        return c.TryGetValue(key, out var v) ? v : string.Empty;
    }

    private static long Long(IReadOnlyDictionary<string, string> c, string key, long fallback)
    {
        return long.TryParse(Get(c, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>메모리 — "2.00 GiB", 최소 메모리가 있으면 "1.00 GiB/2.00 GiB [shares=N]", 벌루닝 끔은 " [balloon=0]".</summary>
    public static string Memory(IReadOnlyDictionary<string, string> c)
    {
        var memory = Long(c, "memory", 512);
        var text = new StringBuilder(FormatSize(memory * MiB));
        if (c.ContainsKey("balloon"))
        {
            var balloon = Long(c, "balloon", 0);
            if (balloon > 0)
            {
                text.Insert(0, FormatSize(balloon * MiB) + "/");
                if (Get(c, "shares") is { Length: > 0 } shares) text.Append($" [shares={shares}]");
            }
            else
            {
                text.Append(" [balloon=0]");
            }
        }

        if (Get(c, "allow-ksm") is { Length: > 0 } ksm) text.Append($" [allow-ksm={ksm}]");
        return text.ToString();
    }

    /// <summary>프로세서 — "8 (1 sockets, 8 cores) [x86-64-v2-AES]" 에 설정된 numa·vcpus 등을 덧붙인다.</summary>
    public static string Processors(IReadOnlyDictionary<string, string> c)
    {
        var sockets = Long(c, "sockets", 1);
        var cores = Long(c, "cores", 1);
        var text = new StringBuilder(Loc.T("Hw_ProcessorsValue", sockets * cores, sockets, cores));
        if (Get(c, "cpu") is { Length: > 0 } cpu) text.Append($" [{cpu}]");
        foreach (var key in new[] { "numa", "vcpus", "cpulimit", "cpuunits" })
            if (Get(c, key) is { Length: > 0 } v)
                text.Append($" [{key}={v}]");
        if (Get(c, "affinity") is { Length: > 0 } affinity) text.Append($" [cpuaffinity={affinity}]");
        return text.ToString();
    }

    public static string Bios(string raw)
    {
        return raw switch
        {
            "" => Loc.T("Hw_BiosDefault"),
            "seabios" => "SeaBIOS",
            "ovmf" => "OVMF (UEFI)",
            _ => raw
        };
    }

    /// <summary>디스플레이 — 비었으면 "기본값", 아니면 "라벨 (원래 값)". 예: "VirGL GPU (virtio-gl,clipboard=vnc)".</summary>
    public static string Vga(string raw)
    {
        if (raw.Length == 0) return Loc.T("Hw_Default");
        var type = PropertyString.Parse(raw, "type").Get("type");
        var label = VgaTypes.FirstOrDefault(t => t.Value == type).Label ?? Loc.T("Hw_Default");
        return $"{label} ({raw})";
    }

    public static string ScsiHw(string raw)
    {
        return raw is "" or "__default__"
            ? Loc.T("Hw_ScsiDefault")
            : ScsiControllers.FirstOrDefault(s => s.Value == raw).Label ?? raw;
    }

    /// <summary>
    ///     머신 — Windows VM 에 버전 고정이 없으면 서버가 쓰는 implicit 버전("pc-i440fx-5.1 (implicit)")을 보인다.
    ///     나머지는 비었으면 "기본값 (i440fx)", 아니면 원래 값.
    /// </summary>
    public static string Machine(IReadOnlyDictionary<string, string> c)
    {
        var machine = PropertyString.Parse(Get(c, "machine"), "type");
        var type = machine.Get("type");
        var viommu = machine["viommu"] is { Length: > 0 } v ? $", viommu={v}" : string.Empty;
        if (WindowsTypes.Contains(Get(c, "ostype")) && type is "" or "pc" or "q35")
        {
            var prefix = type == "q35" ? "pc-q35-" : "pc-i440fx-";
            return $"{prefix}{ImplicitVersion(Get(c, "meta"))} (implicit){viommu}";
        }

        return type.Length == 0 && machine.Items.Count == 0 ? Loc.T("Hw_MachineDefault") : Get(c, "machine");
    }

    /// <summary>meta 의 creation-qemu 가 9.1 이상이면 그 버전(M.m), 아니면 5.1.</summary>
    internal static string ImplicitVersion(string meta)
    {
        var created = PropertyString.Parse(meta).Get("creation-qemu");
        var parts = created.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            && (major > 9 || major == 9 && minor >= 1))
            return $"{major}.{minor}";
        return BaseMachineVersion;
    }
}
