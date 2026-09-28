using System.Globalization;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Guest.Hardware;
using ProxmoxClient.App.Views.Guest.Tabs;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Create;

/// <summary>
///     VM 만들기 마법사(웹 UI qemu/CreateWizard.js) — 일반·OS·시스템·디스크·CPU·메모리·네트워크·확인.
///     값 조립은 <see cref="VmDraft" />, 이 클래스는 단계 화면과 검사만 맡는다.
/// </summary>
internal sealed class VmWizard
{
    private static readonly (string, string)[] Buses =
        [("ide", "IDE"), ("sata", "SATA"), ("scsi", "SCSI"), ("virtio", "VirtIO Block")];

    private readonly ProxmoxApiClient _api;
    private readonly VmDraft _d = new();
    private readonly IReadOnlyList<(string Value, string Label)> _nodes;
    private WizardData _data = new();

    private VmWizard(ProxmoxApiClient api, IReadOnlyList<string> nodes)
    {
        _api = api;
        _nodes = nodes.Select(n => (n, n)).ToList();
    }

    /// <summary>마법사를 연다 — 만들었으면 결과 문구, 취소면 null.</summary>
    public static async Task<string?> ShowAsync(Window owner, ProxmoxApiClient api, IReadOnlyList<string> nodes)
    {
        if (nodes.Count == 0) return null;
        var wizard = new VmWizard(api, nodes);
        await wizard.LoadNodeAsync(nodes[0]);
        var window = new WizardWindow(Loc.T("Wz_CreateVm"), wizard.Steps(), wizard.CreateAsync) { Owner = owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    /// <summary>노드 목록을 읽고, 노드에 따라 달라지는 기본값(다음 ID·첫 저장소·첫 브리지)을 채운다.</summary>
    private async Task LoadNodeAsync(string node)
    {
        _data = await WizardData.LoadAsync(_api, node);
        _d.Node = node;
        if (_d.VmId.Length == 0) _d.VmId = _data.NextId;
        _d.Bridge = _data.Bridges.FirstOrDefault().Value ?? string.Empty;
        var storage = _data.ImageStorages.FirstOrDefault()?.Id ?? string.Empty;
        foreach (var disk in _d.Disks) disk.Storage = storage;
        _d.EfiStorage = storage;
        _d.TpmStorage = storage;
        _d.Iso = string.Empty;
        _d.VirtioIso = string.Empty;
    }

    private async Task<string> CreateAsync()
    {
        return await WizardData.CreateAsync(_api, _d.Node, ResourceKind.Qemu,
            _d.BuildParams(_data.FormatOf), _d.VmId.Trim());
    }

    private IReadOnlyList<WizardStep> Steps()
    {
        return
        [
            new WizardStep { TitleKey = "Wz_General", Build = General, Validate = ValidateGeneral },
            new WizardStep { TitleKey = "Wz_Os", Build = Os, Validate = ValidateOs },
            new WizardStep { TitleKey = "Wz_System", Build = System, Validate = ValidateSystem,
                OnShown = _d.OnSystemShown },
            new WizardStep { TitleKey = "Wz_Disks", Build = Disks, Validate = ValidateDisks },
            new WizardStep { TitleKey = "Wz_Cpu", Build = Cpu, Validate = ValidateCpu },
            new WizardStep { TitleKey = "Wz_Memory", Build = Memory, Validate = ValidateMemory,
                OnShown = _d.OnMemoryShown },
            new WizardStep { TitleKey = "Wz_Network", Build = Network, Validate = ValidateNetwork },
            new WizardStep { TitleKey = "Wz_Confirm", Build = Confirm }
        ];
    }

    // ------------------------------------------------------------ 일반

    private void General(WizardPage page)
    {
        page.Choice("Wz_Node", _nodes, () => _d.Node, node => _ = ChangeNodeAsync(page.Window, node));
        page.Text("Wz_VmId", () => _d.VmId, v => _d.VmId = v);
        page.Text("Table_Name", () => _d.Name, v => _d.Name = v);
        page.Choice("Table_Pool", [("", "Common_None"), .. _data.Pools], () => _d.Pool, v => _d.Pool = v);
        page.Check("GuestSettingsWindow_03", () => _d.OnBoot, v => _d.OnBoot = v, advanced: true);
        page.Text("Startup_Order", () => _d.StartupOrder, v => _d.StartupOrder = v, Loc.T("Startup_OrderHint"), true);
        page.Text("Startup_Up", () => _d.StartupUp, v => _d.StartupUp = v, Loc.T("Startup_DefaultHint"), true);
        page.Text("Startup_Down", () => _d.StartupDown, v => _d.StartupDown = v, Loc.T("Startup_DefaultHint"), true);
        page.Text("Wz_Tags", () => _d.Tags, v => _d.Tags = v, Loc.T("Wz_TagsHint"), true);
    }

    /// <summary>노드를 바꾸면 그 노드의 목록을 다시 읽는다 — 읽는 동안 창을 잠가 다른 입력과 섞이지 않게.</summary>
    private async Task ChangeNodeAsync(WizardWindow window, string node)
    {
        window.SetBusy(true);
        window.SetStatus(Loc.T("Hw_Loading"));
        try
        {
            await LoadNodeAsync(node);
            window.SetStatus(string.Empty);
        }
        catch (Exception ex)
        {
            App.Log($"[만들기] 노드 목록 조회 실패: {ex.Message}");
            window.SetStatus(Loc.T("Wz_LoadFailed", ex.Message));
        }
        finally
        {
            window.SetBusy(false);
            window.Rebuild();
        }
    }

    private string? ValidateGeneral()
    {
        if (!int.TryParse(_d.VmId, out var id) || id < 100) return Loc.T("CreateGuest_VmidMin", 100);
        return new[] { _d.StartupOrder, _d.StartupUp, _d.StartupDown }
            .All(v => v.Trim().Length == 0 || int.TryParse(v, out var n) && n >= 0)
            ? null
            : Loc.T("Startup_NumbersOnly");
    }

    // ------------------------------------------------------------ OS

    private void Os(WizardPage page)
    {
        page.Choice("HwEd_CdMedia", [("iso", "HwEd_CdIso"), ("cdrom", "HwEd_CdPhysical"), ("none", "HwEd_CdNone")],
            () => _d.CdMode, v => _d.CdMode = v, rebuild: true);
        if (_d.CdMode == "iso")
            page.Choice("DeviceTable_Iso", _data.Isos, () => _d.Iso, v => _d.Iso = v,
                _data.Isos.Count == 0 ? Loc.T("Wz_NoIso") : null);

        page.Section("Wz_GuestOs");
        var family = OsTypes.FamilyOf(_d.OsType);
        page.Choice("OsType_Type", OsTypes.Families.Select(f => (f.LabelKey, f.LabelKey)).ToList(),
            () => family.LabelKey,
            key => _d.SetOsType(OsTypes.Families.First(f => f.LabelKey == key).Versions[0].Value), rebuild: true);
        if (OsTypes.HasVersionChoice(family))
            page.Choice("OsType_Version", family.Versions, () => _d.OsType, _d.SetOsType, rebuild: true);

        if (!_d.IsWindows) return;
        page.Check("Wz_VirtioDrivers", () => _d.VirtioDrivers, _d.SetVirtioDrivers, Loc.T("Wz_VirtioDriversHint"),
            rebuild: true);
        if (_d.VirtioDrivers)
            page.Choice("Wz_VirtioIso", _data.Isos, () => _d.VirtioIso, v => _d.VirtioIso = v);
    }

    private string? ValidateOs()
    {
        if (_d.CdMode == "iso" && _d.Iso.Length == 0) return Loc.T("HwEd_CdPickIso");
        return _d.VirtioDrivers && _d.VirtioIso.Length == 0 ? Loc.T("Wz_PickVirtioIso") : null;
    }

    // ------------------------------------------------------------ 시스템

    private void System(WizardPage page)
    {
        var storages = WizardData.StorageChoices(_data.ImageStorages);
        page.Choice("HwEd_GraphicCard", [("", "Hw_Default"), .. HardwareRender.VgaTypes], () => _d.Vga,
            v => _d.Vga = v);
        page.Choice("Hw_Machine", [("", "Hw_MachineDefault"), ("q35", "q35")], () => _d.Machine, v => _d.Machine = v);
        page.Section("Wz_Firmware");
        page.Choice("HwEd_Bios", [("", "Hw_BiosDefault"), ("seabios", "SeaBIOS"), ("ovmf", "OVMF (UEFI)")],
            () => _d.Bios, v => _d.Bios = v, rebuild: true);
        if (_d.Bios == "ovmf")
        {
            page.Check("Wz_AddEfi", () => _d.AddEfi, v => _d.AddEfi = v, rebuild: true);
            if (_d.AddEfi)
            {
                page.Choice("HwEd_EfiStorage", storages, () => _d.EfiStorage, v => _d.EfiStorage = v);
                page.Check("HwEd_PreEnroll", () => _d.PreEnrollKeys, v => _d.PreEnrollKeys = v,
                    Loc.T("HwEd_PreEnrollHint"));
            }
        }

        page.Section("Hw_ScsiHw");
        page.Choice("Hw_ScsiHw", [("", "Hw_ScsiDefault"), .. HardwareRender.ScsiControllers], () => _d.ScsiHw,
            v => _d.ScsiHw = v);
        page.Check("Agent_Enabled", () => _d.Agent, v => _d.Agent = v);
        page.Check("Wz_AddTpm", () => _d.AddTpm, v => _d.AddTpm = v, rebuild: true);
        if (!_d.AddTpm) return;
        page.Choice("HwEd_TpmStorage", storages, () => _d.TpmStorage, v => _d.TpmStorage = v);
        page.Choice("HwEd_TpmVersion", [("v2.0", "v2.0"), ("v1.2", "v1.2")], () => _d.TpmVersion,
            v => _d.TpmVersion = v);
    }

    private string? ValidateSystem()
    {
        if (_d.Bios == "ovmf" && _d.AddEfi && _d.EfiStorage.Length == 0) return Loc.T("Wz_PickEfiStorage");
        return _d.AddTpm && _d.TpmStorage.Length == 0 ? Loc.T("Wz_PickTpmStorage") : null;
    }

    // ------------------------------------------------------------ 디스크 · CPU · 메모리 · 네트워크 · 확인

    private void Disks(WizardPage page)
    {
        var storages = WizardData.StorageChoices(_data.ImageStorages);
        foreach (var disk in _d.Disks.ToList())
        {
            page.Section(Loc.T("Wz_DiskTitle", disk.Slot));
            page.Choice("DeviceTable_Bus", Buses, () => DiskOptions.BusOf(disk.Slot),
                bus => _d.SetDiskBus(disk, bus), Loc.T("Wz_SlotIs", disk.Slot), rebuild: true);
            page.Choice("Table_Storage", storages, () => disk.Storage, v => disk.Storage = v);
            page.Text("HwEd_DiskSizeGib", () => disk.SizeGib, v => disk.SizeGib = v);
            page.Choice("HwEd_Format", [("", "HwEd_FormatAuto"), ("raw", "raw"), ("qcow2", "qcow2"), ("vmdk", "vmdk")],
                () => disk.Format, v => disk.Format = v, advanced: true);
            page.Choice("HwEd_Cache", [("", "HwEd_CacheDefault"), ("directsync", "Direct sync"),
                ("writethrough", "Write through"), ("writeback", "Write back"), ("unsafe", "Write back (unsafe)"),
                ("none", "No cache")], () => disk.Cache, v => disk.Cache = v);
            page.Check("HwEd_Discard", () => disk.Discard, v => disk.Discard = v);
            page.Check("HwEd_IoThread", () => disk.IoThread, v => disk.IoThread = v, Loc.T("HwEd_IoThreadHint"));
            page.Check("HwEd_Ssd", () => disk.Ssd, v => disk.Ssd = v, advanced: true);
            page.Check("HwEd_Backup", () => disk.Backup, v => disk.Backup = v, advanced: true);
            if (_d.Disks.Count > 1)
                page.Button("Wz_RemoveDisk", () =>
                {
                    _d.Disks.Remove(disk);
                    page.Window.Rebuild();
                });
        }

        page.Button("Wz_AddDisk", () =>
        {
            var disk = new DiskDraft { Storage = _data.ImageStorages.FirstOrDefault()?.Id ?? "" };
            _d.Disks.Add(disk);
            _d.SetDiskBus(disk, DiskOptions.BusOf(_d.Disks[0].Slot));
            page.Window.Rebuild();
        });
    }

    private string? ValidateDisks()
    {
        var slots = _d.Disks.Select(d => d.Slot).Concat(_d.CdSlots()).ToList();
        if (slots.Distinct().Count() != slots.Count) return Loc.T("Wz_DuplicateSlot");
        foreach (var d in _d.Disks)
        {
            if (d.Storage.Length == 0) return Loc.T("Wz_PickStorage", d.Slot);
            if (!double.TryParse(d.SizeGib, NumberStyles.Float, CultureInfo.InvariantCulture, out var gib)
                || gib is < 0.001 or > 131072)
                return Loc.T("HwEd_DiskSizeRange");
        }

        return null;
    }

    private void Cpu(WizardPage page)
    {
        page.Text("HwEd_Sockets", () => _d.Sockets, v => _d.Sockets = v);
        page.Text("HwEd_Cores", () => _d.Cores, v => _d.Cores = v,
            _data.HostCpus > 0 ? Loc.T("HostLimit_Cores", _data.HostCpus, 1) : null);
        page.Choice("HwEd_CpuType", [("", Loc.T("HwEd_CpuDefault", VmDraft.DefaultCpuType)), .. _data.CpuModels],
            () => _d.CpuType, v => _d.CpuType = v);
        page.Check("HwEd_Numa", () => _d.Numa, v => _d.Numa = v, advanced: true);
    }

    private string? ValidateCpu()
    {
        return int.TryParse(_d.Sockets, out var s) && s >= 1
               && int.TryParse(_d.Cores, out var c) && c is >= 1 and <= 256
            ? null
            : Loc.T("GuestSettings_CoresMin");
    }

    private void Memory(WizardPage page)
    {
        page.Text("HwEd_MemoryMiB", () => _d.Memory, v => _d.Memory = v,
            _data.HostMemoryMiB > 0 ? Loc.T("Wz_HostMemory", _data.HostMemoryMiB) : null);
        page.Text("HwEd_MinMemory", () => _d.Balloon, v => _d.Balloon = v, Loc.T("Wz_MinMemoryHint"), true);
        page.Check("HwEd_Ballooning", () => _d.Ballooning, v => _d.Ballooning = v, advanced: true);
    }

    private string? ValidateMemory()
    {
        if (!int.TryParse(_d.Memory, out var m) || m < 16) return Loc.T("HwEd_MemoryMin", 16);
        return _d.Balloon.Trim().Length == 0 || int.TryParse(_d.Balloon, out var b) && b >= 1 && b <= m
            ? null
            : Loc.T("HwEd_BalloonRange");
    }

    private void Network(WizardPage page)
    {
        page.Check("Wz_NoNetwork", () => _d.NoNetwork, v => _d.NoNetwork = v, rebuild: true);
        if (_d.NoNetwork) return;
        page.Choice("GuestSettingsWindow_17", _data.Bridges, () => _d.Bridge, v => _d.Bridge = v);
        page.Text("GuestSettingsWindow_21", () => _d.Vlan, v => _d.Vlan = v, Loc.T("HwEd_NoVlan"));
        page.Check("FirewallWindow_01", () => _d.Firewall, v => _d.Firewall = v);
        page.Choice("GuestSettingsWindow_18", DeviceEditors.NicModels, () => _d.NicModel, v => _d.NicModel = v);
        page.Text("GuestSettingsWindow_19", () => _d.Mac, v => _d.Mac = v, Loc.T("HwEd_MacAuto"));
    }

    private string? ValidateNetwork()
    {
        return _d.NoNetwork ? null : WizardChecks.Network(_d.Bridge, _d.Vlan, _d.Mac);
    }

    private void Confirm(WizardPage page)
    {
        page.Summary(_d.BuildParams(_data.FormatOf));
        page.Check("Wz_StartAfter", () => _d.Start, v => _d.Start = v, rebuild: true);
    }
}
