using System.Text.RegularExpressions;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>하드웨어 목록 한 줄의 종류 — 편집기·디스크 작업·제거 규칙이 종류마다 다르다.</summary>
public enum HardwareItem
{
    Arch, Memory, Processors, Bios, Display, Machine, ScsiHw, Disk, Cdrom, CloudInit, Network, Efi, Tpm, Usb, Pci,
    Serial, Audio, Rng, Virtiofs, Unused, VmState,

    // CT 리소스
    Swap, Cores, RootFs, MountPoint, Device
}

/// <summary>
///     하드웨어 목록 한 줄. <see cref="Display" /> 는 지금 값, <see cref="PendingText" /> 는 재시작 후 적용될 값.
/// </summary>
public sealed record HardwareRow(
    string Key, HardwareItem Item, string Header, string Display, string PendingText, bool HasPending,
    bool IsDeleted, IReadOnlyList<string> Keys)
{
    /// <summary>쓰고 있는 디스크·볼륨 — 제거 버튼이 "분리"가 되고 크기 조정을 할 수 있다.</summary>
    public bool IsUsedDisk => Item is HardwareItem.Disk or HardwareItem.RootFs or HardwareItem.MountPoint;

    public bool IsDisk => IsUsedDisk || Item == HardwareItem.Unused;

    /// <summary>편집 창이 있는 줄(웹 UI 도 EFI·TPM·시리얼·CloudInit·VM 상태·아키텍처는 편집 창이 없다).</summary>
    public bool HasEditor => !IsDeleted && Item is not (HardwareItem.Efi or HardwareItem.Tpm or HardwareItem.Serial
        or HardwareItem.CloudInit or HardwareItem.VmState or HardwareItem.Arch);

    /// <summary>늘 있는 항목(메모리·프로세서 등)은 지울 수 없다.</summary>
    public bool CanRemove => !IsDeleted && Item is not (HardwareItem.Arch or HardwareItem.Memory
        or HardwareItem.Processors or HardwareItem.Bios or HardwareItem.Display or HardwareItem.Machine
        or HardwareItem.ScsiHw or HardwareItem.Swap or HardwareItem.Cores or HardwareItem.RootFs);
}

/// <summary>VM 설정(대기 중 변경 포함)에서 웹 UI 와 같은 순서·표시의 하드웨어 목록을 만든다.</summary>
internal static partial class HardwareRows
{
    /// <summary>한 줄로 묶이는 키(바뀌면 그 줄이 대기 중으로 보인다).</summary>
    private static readonly string[] MemoryKeys = ["memory", "balloon", "shares", "allow-ksm"];

    private static readonly string[] CpuKeys =
        ["sockets", "cpu", "cores", "numa", "vcpus", "cpulimit", "cpuunits", "affinity"];

    [GeneratedRegex(@"^(ide|sata|scsi|virtio|net|usb|hostpci|serial|audio|rng|virtiofs|unused)(\d+)$")]
    private static partial Regex DeviceKey();

    [GeneratedRegex(@"vm-.*-cloudinit")]
    private static partial Regex CloudInitVolume();

    public static IReadOnlyList<HardwareRow> Build(GuestPendingConfig config)
    {
        var rows = new List<(int Group, int Index, HardwareRow Row)>();

        void Add(int group, int index, string key, HardwareItem item, string header, IReadOnlyList<string> keys,
            Func<IReadOnlyDictionary<string, string>, string> render)
        {
            rows.Add((group, index, MakeRow(config, key, item, header, keys, render)));
        }

        var all = config.Current.Keys.Concat(config.Effective.Keys).Distinct().ToList();
        if (all.Contains("arch"))
            Add(0, 0, "arch", HardwareItem.Arch, Loc.T("Hw_Arch"), ["arch"], c => ArchLabel(Get(c, "arch")));
        Add(2, 0, "memory", HardwareItem.Memory, Loc.T("Hw_Memory"), MemoryKeys, HardwareRender.Memory);
        Add(3, 0, "sockets", HardwareItem.Processors, Loc.T("Hw_Processors"), CpuKeys, HardwareRender.Processors);
        Add(4, 0, "bios", HardwareItem.Bios, "BIOS", ["bios"], c => HardwareRender.Bios(Get(c, "bios")));
        Add(5, 0, "vga", HardwareItem.Display, Loc.T("Hw_Display"), ["vga"], c => HardwareRender.Vga(Get(c, "vga")));
        Add(6, 0, "machine", HardwareItem.Machine, Loc.T("Hw_Machine"), ["machine"], HardwareRender.Machine);
        Add(7, 0, "scsihw", HardwareItem.ScsiHw, Loc.T("Hw_ScsiHw"), ["scsihw"],
            c => HardwareRender.ScsiHw(Get(c, "scsihw")));

        foreach (var key in all)
            if (Describe(key, config) is { } d)
                Add(d.Group, d.Index, key, d.Item, d.Header, [key], c => Get(c, key));

        return rows.OrderBy(r => r.Group).ThenBy(r => r.Index).ThenBy(r => r.Row.Key, StringComparer.Ordinal)
            .Select(r => r.Row).ToList();
    }

    /// <summary>장치 키의 종류·머리글·정렬 자리. 장치가 아니면 null.</summary>
    private static (int Group, int Index, HardwareItem Item, string Header)? Describe(string key,
        GuestPendingConfig config)
    {
        if (key == "efidisk0") return (20, 0, HardwareItem.Efi, Loc.T("Hw_Efi"));
        if (key == "tpmstate0") return (22, 0, HardwareItem.Tpm, Loc.T("Hw_Tpm"));
        if (key == "vmstate") return (100, 0, HardwareItem.VmState, Loc.T("Hw_VmState"));

        var match = DeviceKey().Match(key);
        if (!match.Success) return null;

        var bus = match.Groups[1].Value;
        var index = int.Parse(match.Groups[2].Value);
        var value = config.Effective.GetValueOrDefault(key) ?? config.Current.GetValueOrDefault(key, "");
        return bus switch
        {
            "ide" or "sata" or "scsi" or "virtio" when CloudInitVolume().IsMatch(value) =>
                (10, index, HardwareItem.CloudInit, Loc.T("Hw_CloudInit", key)),
            "ide" or "sata" or "scsi" or "virtio" when value.Contains("media=cdrom", StringComparison.Ordinal) =>
                (10, index, HardwareItem.Cdrom, Loc.T("Hw_Cdrom", key)),
            "ide" or "sata" or "scsi" or "virtio" => (10, index, HardwareItem.Disk, Loc.T("Hw_Disk", key)),
            "net" => (15, index, HardwareItem.Network, Loc.T("Hw_Net", key)),
            "usb" => (25, index, HardwareItem.Usb, Loc.T("Hw_Usb", key)),
            "hostpci" => (30, index, HardwareItem.Pci, Loc.T("Hw_Pci", key)),
            "serial" => (35, index, HardwareItem.Serial, Loc.T("Hw_Serial", key)),
            "audio" => (40, index, HardwareItem.Audio, Loc.T("Hw_Audio")),
            "rng" => (45, index, HardwareItem.Rng, "VirtIO RNG"),
            "virtiofs" => (50, index, HardwareItem.Virtiofs, Loc.T("Hw_Virtiofs", key)),
            "unused" => (99, index, HardwareItem.Unused, Loc.T("Hw_Unused", index)),
            _ => null
        };
    }

    private static HardwareRow MakeRow(GuestPendingConfig config, string key, HardwareItem item, string header,
        IReadOnlyList<string> keys, Func<IReadOnlyDictionary<string, string>, string> render)
    {
        var hasPending = config.HasPending(keys);
        var deleted = keys.All(k => config.DeletedKeys.Contains(k) || !config.Current.ContainsKey(k))
                      && keys.Any(config.DeletedKeys.Contains);
        var now = render(config.Current);
        var after = render(config.Effective);
        var pending = !hasPending ? string.Empty
            : deleted ? Loc.T("OptionsTab_PendingDelete", now)
            : after == now ? string.Empty
            : after;
        return new HardwareRow(key, item, header, now, pending, hasPending, deleted, keys);
    }

    [GeneratedRegex(@"^(mp|unused|dev)(\d+)$")]
    private static partial Regex CtDeviceKey();

    /// <summary>
    ///     CT 리소스 목록(웹 UI lxc/Resources.js) — 메모리·스왑·코어·루트 디스크·마운트 포인트·미사용 디스크·장치.
    /// </summary>
    public static IReadOnlyList<HardwareRow> BuildCt(GuestPendingConfig config)
    {
        var rows = new List<(int Group, int Index, HardwareRow Row)>
        {
            (1, 0, MakeRow(config, "memory", HardwareItem.Memory, Loc.T("Hw_Memory"), ["memory"],
                c => HardwareRender.FormatSize(MiBOf(c, "memory") * 1024.0 * 1024))),
            (2, 0, MakeRow(config, "swap", HardwareItem.Swap, Loc.T("Ct_Swap"), ["swap"],
                c => HardwareRender.FormatSize(MiBOf(c, "swap") * 1024.0 * 1024))),
            (3, 0, MakeRow(config, "cores", HardwareItem.Cores, Loc.T("Ct_Cores"), ["cores", "cpulimit", "cpuunits"],
                CtCores)),
            (4, 0, MakeRow(config, "rootfs", HardwareItem.RootFs, Loc.T("Ct_RootDisk"), ["rootfs"],
                c => Get(c, "rootfs") is { Length: > 0 } r ? r : Loc.T("Common_None")))
        };
        foreach (var key in config.Current.Keys.Concat(config.Effective.Keys).Distinct())
        {
            var match = CtDeviceKey().Match(key);
            if (!match.Success) continue;

            var index = int.Parse(match.Groups[2].Value);
            var (group, item, header) = match.Groups[1].Value switch
            {
                "mp" => (5, HardwareItem.MountPoint, Loc.T("Ct_MountPoint", key)),
                "unused" => (6, HardwareItem.Unused, Loc.T("Hw_Unused", index)),
                _ => (7, HardwareItem.Device, Loc.T("Ct_Device", key))
            };
            rows.Add((group, index, MakeRow(config, key, item, header, [key], c => Get(c, key))));
        }

        return rows.OrderBy(r => r.Group).ThenBy(r => r.Index).Select(r => r.Row).ToList();
    }

    /// <summary>설정 값(MiB), 없으면 서버 기본 512.</summary>
    private static long MiBOf(IReadOnlyDictionary<string, string> c, string key)
    {
        return long.TryParse(Get(c, key), out var v) ? v : 512;
    }

    /// <summary>코어 — 값 또는 "제한 없음", 뒤에 [cpulimit=…] [cpuunits=…].</summary>
    private static string CtCores(IReadOnlyDictionary<string, string> c)
    {
        var text = Get(c, "cores") is { Length: > 0 } cores ? cores : Loc.T("Ct_Unlimited");
        if (Get(c, "cpulimit") is { Length: > 0 } limit) text += $" [cpulimit={limit}]";
        if (Get(c, "cpuunits") is { Length: > 0 } units) text += $" [cpuunits={units}]";
        return text;
    }

    private static string Get(IReadOnlyDictionary<string, string> c, string key)
    {
        return c.TryGetValue(key, out var v) ? v : string.Empty;
    }

    private static string ArchLabel(string arch)
    {
        return arch switch
        {
            "" or "x86_64" => "x86 (64-bit)",
            "aarch64" => "ARM (64-bit)",
            _ => Loc.T("Common_Unknown")
        };
    }
}
