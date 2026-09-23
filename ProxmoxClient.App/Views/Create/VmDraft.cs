using System.Globalization;

namespace ProxmoxClient.App.Views.Create;

/// <summary>
///     OS 에 따른 VM 기본값(웹 UI qemu/OSDefaults.js). 표에 없는 OS 는 generic.
/// </summary>
internal sealed record OsDefaults(string Bus, string NetworkCard, string ScsiHw, string Machine, string Bios,
    bool Tpm, int MemoryMiB)
{
    private static readonly OsDefaults Generic = new("ide", "e1000", "virtio-scsi-single", "", "", false, 2048);

    public static OsDefaults For(string ostype)
    {
        return ostype switch
        {
            "l26" => Generic with { Bus = "scsi", NetworkCard = "virtio" },
            "w2k" or "wxp" => Generic with { NetworkCard = "rtl8139", ScsiHw = "" },
            "win11" => Generic with { Machine = "q35", Bios = "ovmf", Tpm = true, MemoryMiB = 4096 },
            _ => Generic
        };
    }
}

/// <summary>마법사의 디스크 하나(웹 UI HDInputPanel). 새 디스크는 저장소:크기(GiB).</summary>
internal sealed class DiskDraft
{
    public string Slot { get; set; } = "scsi0";
    public string Storage { get; set; } = string.Empty;
    public string SizeGib { get; set; } = "32";
    public string Format { get; set; } = string.Empty;
    public string Cache { get; set; } = string.Empty;
    public bool Discard { get; set; }
    public bool IoThread { get; set; } = true;
    public bool Ssd { get; set; }
    public bool Backup { get; set; } = true;
}

/// <summary>
///     VM 만들기 마법사 입력(웹 UI qemu/CreateWizard.js) — 단계 화면이 고치고, <see cref="BuildParams" /> 가
///     POST nodes/{node}/qemu 로 보낼 값을 만든다. 기본값이면 보내지 않는 규칙도 웹 UI 와 같다.
/// </summary>
internal sealed class VmDraft
{
    public const string DefaultCpuType = "x86-64-v2-AES";

    // 일반
    public string Node { get; set; } = string.Empty;
    public string VmId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Pool { get; set; } = string.Empty;
    public bool OnBoot { get; set; }
    public string StartupOrder { get; set; } = string.Empty;
    public string StartupUp { get; set; } = string.Empty;
    public string StartupDown { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;

    // OS
    public string CdMode { get; set; } = "iso";
    public string Iso { get; set; } = string.Empty;
    public string OsType { get; private set; } = "l26";
    public bool VirtioDrivers { get; private set; }
    public string VirtioIso { get; set; } = string.Empty;

    // 시스템
    public string Vga { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public string Bios { get; set; } = string.Empty;
    public bool AddEfi { get; set; } = true;
    public string EfiStorage { get; set; } = string.Empty;
    public bool PreEnrollKeys { get; set; } = true;
    public string ScsiHw { get; set; } = "virtio-scsi-single";
    public bool Agent { get; set; }
    public bool AddTpm { get; set; }
    public string TpmStorage { get; set; } = string.Empty;
    public string TpmVersion { get; set; } = "v2.0";

    // 디스크·CPU·메모리·네트워크
    public List<DiskDraft> Disks { get; } = [new DiskDraft()];
    public string Sockets { get; set; } = "1";
    public string Cores { get; set; } = "1";
    public string CpuType { get; set; } = string.Empty;
    public bool Numa { get; set; }
    public string Memory { get; set; } = "2048";
    public string Balloon { get; set; } = string.Empty;
    public bool Ballooning { get; set; } = true;
    public bool NoNetwork { get; set; }
    public string Bridge { get; set; } = string.Empty;
    public string Vlan { get; set; } = string.Empty;
    public bool Firewall { get; set; } = true;
    public string NicModel { get; set; } = "virtio";
    public string Mac { get; set; } = string.Empty;
    public bool Start { get; set; }

    /// <summary>시스템·메모리 단계를 처음 열었는가 — 웹 UI 는 처음 열 때의 OS 로 그 단계 기본값을 정한다.</summary>
    public bool SystemShown { get; private set; }

    public bool MemoryShown { get; private set; }

    public OsDefaults Defaults => OsDefaults.For(OsType);

    public bool IsWindows => OsType is "w2k" or "wxp" or "w2k8" or "win7" or "win8" or "win10" or "win11";

    /// <summary>
    ///     OS 를 바꾼다(웹 UI onOSTypeChange) — 디스크가 하나면 버스, NIC 모델, CPU 종류(기본), SCSI 컨트롤러를
    ///     새 OS 의 기본값으로. Windows 가 아니면 VirtIO 드라이버 CD 는 끈다.
    /// </summary>
    public void SetOsType(string ostype)
    {
        OsType = ostype;
        if (!IsWindows) VirtioDrivers = false;
        var defaults = Defaults;
        ScsiHw = defaults.ScsiHw; // 디스크 버스보다 먼저 — IO 스레드 기본값이 컨트롤러에 달려 있다
        if (Disks.Count == 1) SetDiskBus(Disks[0], VirtioDrivers ? "scsi" : defaults.Bus);
        NicModel = VirtioDrivers ? "virtio" : defaults.NetworkCard;
        CpuType = string.Empty;
    }

    /// <summary>
    ///     VirtIO 드라이버 CD(ide0) — 켜면 디스크는 SCSI, NIC 는 VirtIO 로. 끄면 디스크 버스만 OS 기본으로 돌린다.
    /// </summary>
    public void SetVirtioDrivers(bool on)
    {
        VirtioDrivers = on && IsWindows;
        if (Disks.Count == 1) SetDiskBus(Disks[0], VirtioDrivers ? "scsi" : Defaults.Bus);
        // 디스크가 여럿이면 CD 자리(ide0)를 쓰던 디스크만 같은 버스의 빈 자리로 옮긴다
        foreach (var disk in Disks.Where(d => CdSlots().Contains(d.Slot)).ToList())
            SetDiskBus(disk, new string(disk.Slot.TakeWhile(char.IsLetter).ToArray()));
        if (VirtioDrivers) NicModel = "virtio";
    }

    /// <summary>시스템 단계를 처음 열 때 — 머신·BIOS·TPM 을 지금 OS 의 기본값으로.</summary>
    public void OnSystemShown()
    {
        if (SystemShown) return;
        SystemShown = true;
        var defaults = Defaults;
        Machine = defaults.Machine;
        Bios = defaults.Bios;
        AddTpm = defaults.Tpm;
    }

    /// <summary>메모리 단계를 처음 열 때 — Windows 11 이면 4096 MiB.</summary>
    public void OnMemoryShown()
    {
        if (MemoryShown) return;
        MemoryShown = true;
        Memory = Defaults.MemoryMiB.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>디스크 버스를 바꾸고 그 버스의 빈 번호를 고른다. VirtIO·(VirtIO SCSI single 의) SCSI 면 IO 스레드 켬.</summary>
    public void SetDiskBus(DiskDraft disk, string bus)
    {
        var used = Disks.Where(d => d != disk).Select(d => d.Slot).Concat(CdSlots()).ToHashSet(StringComparer.Ordinal);
        var max = bus switch { "ide" => 4, "sata" => 6, "virtio" => 16, _ => 31 };
        disk.Slot = Enumerable.Range(0, max).Select(i => $"{bus}{i}").FirstOrDefault(s => !used.Contains(s))
                    ?? disk.Slot;
        disk.IoThread = bus == "virtio" || bus == "scsi" && ScsiHw == "virtio-scsi-single";
    }

    /// <summary>CD 가 쓰는 자리(ide2, VirtIO 드라이버 CD 면 ide0).</summary>
    public IEnumerable<string> CdSlots()
    {
        yield return "ide2";
        if (VirtioDrivers) yield return "ide0";
    }

    /// <summary>
    ///     서버에 보낼 값 — 기본값(삭제에 해당)은 보내지 않는다(웹 UI 마법사가 delete 를 버리는 것과 같다).
    ///     <paramref name="formatOf" /> 는 저장소에 맞는 형식(qcow2 지원이면 qcow2, 아니면 빈 값).
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildParams(Func<string, string> formatOf)
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vmid"] = VmId.Trim(), ["ostype"] = OsType,
            ["ide2"] = $"{CdMode switch { "cdrom" => "cdrom", "none" => "none", _ => Iso }},media=cdrom",
            ["sockets"] = Sockets.Trim(), ["cores"] = Cores.Trim(),
            ["cpu"] = CpuType.Length > 0 ? CpuType : DefaultCpuType,
            ["numa"] = Numa ? "1" : "0", ["memory"] = Memory.Trim()
        };
        void Put(string key, string value)
        {
            if (value.Length > 0) p[key] = value;
        }

        Put("name", Name.Trim());
        Put("pool", Pool);
        if (OnBoot) p["onboot"] = "1";
        Put("startup", string.Join(',', new[] { ("order", StartupOrder), ("up", StartupUp), ("down", StartupDown) }
            .Where(s => s.Item2.Trim().Length > 0).Select(s => $"{s.Item1}={s.Item2.Trim()}")));
        Put("tags", string.Join(';', Tags.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)));
        if (VirtioDrivers) p["ide0"] = $"{VirtioIso},media=cdrom";
        AddSystem(p, formatOf);
        foreach (var disk in Disks) p[disk.Slot] = DiskValue(disk, formatOf);
        AddMemory(p);
        if (!NoNetwork) p["net0"] = NetValue();
        if (Start) p["start"] = "1";
        if (VirtioDrivers) p["boot"] = BootOrder();
        return p;
    }

    private void AddSystem(Dictionary<string, string> p, Func<string, string> formatOf)
    {
        if (Vga.Length > 0) p["vga"] = Vga;
        if (Vga.StartsWith("serial", StringComparison.Ordinal)) p[Vga] = "socket";
        if (Machine.Length > 0) p["machine"] = Machine;
        if (Bios.Length > 0) p["bios"] = Bios;
        if (Bios == "ovmf" && AddEfi)
            p["efidisk0"] = $"{EfiStorage}:1,efitype=4m" + (PreEnrollKeys ? ",pre-enrolled-keys=1" : "")
                            + FormatSuffix(formatOf(EfiStorage));
        if (ScsiHw.Length > 0) p["scsihw"] = ScsiHw;
        if (Agent) p["agent"] = "1";
        if (AddTpm) p["tpmstate0"] = $"{TpmStorage}:1,version={TpmVersion}";
    }

    private static string FormatSuffix(string format)
    {
        return format.Length > 0 ? $",format={format}" : string.Empty;
    }

    /// <summary>예: scsi0=local-lvm:32,iothread=on / scsi0=local:32,format=qcow2,iothread=on</summary>
    internal static string DiskValue(DiskDraft d, Func<string, string> formatOf)
    {
        var bus = new string(d.Slot.TakeWhile(char.IsLetter).ToArray());
        var parts = new List<string> { $"{d.Storage}:{d.SizeGib.Trim()}" };
        var format = d.Format.Length > 0 ? d.Format : formatOf(d.Storage);
        if (format.Length > 0) parts.Add($"format={format}");
        if (!d.Backup) parts.Add("backup=0");
        if (d.Discard) parts.Add("discard=on");
        if (d.Ssd && bus != "virtio") parts.Add("ssd=on");
        if (d.IoThread && bus is "virtio" or "scsi") parts.Add("iothread=on");
        if (d.Cache.Length > 0) parts.Add($"cache={d.Cache}");
        return string.Join(',', parts);
    }

    /// <summary>기본(최소 메모리 = 메모리)이면 memory 만. 벌루닝 끔은 balloon=0, 더 작은 최소 메모리는 balloon=n.</summary>
    private void AddMemory(Dictionary<string, string> p)
    {
        if (!Ballooning) p["balloon"] = "0";
        else if (Balloon.Trim() is { Length: > 0 } b && b != Memory.Trim()) p["balloon"] = b;
    }

    private string NetValue()
    {
        var model = Mac.Trim().Length > 0 ? $"{NicModel}={Mac.Trim()}" : NicModel;
        var parts = new List<string> { model, $"bridge={Bridge}" };
        if (Vlan.Trim().Length > 0) parts.Add($"tag={Vlan.Trim()}");
        if (Firewall) parts.Add("firewall=1");
        return string.Join(',', parts);
    }

    /// <summary>VirtIO 드라이버 CD 가 있을 때만 — 첫 디스크(ide·scsi·virtio·sata 순으로 찾음);ide2;ide0[;net0].</summary>
    private string BootOrder()
    {
        var first = new[] { "ide", "scsi", "virtio", "sata" }
            .SelectMany(bus => Disks.Where(d => d.Slot.StartsWith(bus, StringComparison.Ordinal))
                .Select(d => d.Slot).OrderBy(s => int.Parse(s[bus.Length..], CultureInfo.InvariantCulture)))
            .FirstOrDefault();
        var order = new List<string>();
        if (first is not null) order.Add(first);
        order.AddRange(["ide2", "ide0"]);
        if (!NoNetwork) order.Add("net0");
        return "order=" + string.Join(';', order);
    }
}
