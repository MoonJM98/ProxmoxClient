using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>VM 시스템 편집기 — BIOS·디스플레이·머신·SCSI 컨트롤러.</summary>
internal static partial class SystemEditors
{
    private static IReadOnlyDictionary<string, string> One(string key, string value)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };
    }

    /// <summary>BIOS — 기본값(SeaBIOS)은 삭제. OVMF 인데 EFI 디스크가 없으면 안내한다.</summary>
    public static HardwareEdit Bios(HardwareContext ctx)
    {
        var needsEfi = !ctx.Effective.ContainsKey("efidisk0");
        return new HardwareEdit
        {
            Title = "BIOS",
            Fields =
            [
                new FormField
                {
                    Key = "bios", LabelKey = "HwEd_Bios", Kind = FormFieldKind.Choice, Initial = ctx.Get("bios"),
                    Choices = [("", "Hw_BiosDefault"), ("seabios", "SeaBIOS"), ("ovmf", "OVMF (UEFI)")],
                    Hint = needsEfi ? Loc.T("HwEd_OvmfNeedsEfi") : null
                }
            ],
            Build = values => One("bios", V(values, "bios"))
        };
    }

    /// <summary>디스플레이 — 종류·메모리·(고급)클립보드. 모두 기본이면 삭제. 예: qxl,clipboard=vnc,memory=32</summary>
    public static HardwareEdit Display(HardwareContext ctx)
    {
        var vga = PropertyString.Parse(ctx.Get("vga"), "type");
        return new HardwareEdit
        {
            Title = Loc.T("Hw_Display"),
            Fields =
            [
                new FormField
                {
                    Key = "type", LabelKey = "HwEd_GraphicCard", Kind = FormFieldKind.Choice, Initial = vga.Get("type"),
                    Choices = [("", "Hw_Default"), .. HardwareRender.VgaTypes]
                },
                new FormField { Key = "memory", LabelKey = "HwEd_VgaMemory", Initial = vga.Get("memory"),
                    Hint = Loc.T("HwEd_VgaMemoryHint") },
                new FormField
                {
                    Key = "clipboard", LabelKey = "HwEd_Clipboard", Kind = FormFieldKind.Choice, Advanced = true,
                    Initial = vga.Get("clipboard"), Choices = [("", "Hw_Default"), ("vnc", "VNC")],
                    Hint = Loc.T("HwEd_ClipboardHint")
                }
            ],
            Validate = values => ValidateDisplay(values, ctx),
            Build = values => One("vga", FormatVga(V(values, "type"), V(values, "memory"), V(values, "clipboard")))
        };
    }

    private static string? ValidateDisplay(IReadOnlyDictionary<string, string> values, HardwareContext ctx)
    {
        var type = V(values, "type");
        if (type.StartsWith("serial", StringComparison.Ordinal) && ctx.Get(type) != "socket")
            return Loc.T("HwEd_SerialDisplayNeedsPort", type);
        return IsIntOrEmpty(V(values, "memory"), 4, 512) ? null : Loc.T("HwEd_VgaMemoryRange");
    }

    /// <summary>type 이 맨 앞(키 없이), 나머지는 이름순 — 웹 UI printPropertyString 과 같다.</summary>
    internal static string FormatVga(string type, string memory, string clipboard)
    {
        var noExtras = type is "none" || type.StartsWith("serial", StringComparison.Ordinal);
        var parts = new List<string>();
        if (type.Length > 0) parts.Add(type);
        if (!noExtras && clipboard.Length > 0) parts.Add($"clipboard={clipboard}");
        if (!noExtras && memory.Length > 0) parts.Add($"memory={memory}");
        return string.Join(',', parts);
    }

    /// <summary>
    ///     머신 — 종류(기본 i440fx / q35), 고급: 버전 고정·vIOMMU.
    ///     저장: 기본+최신+vIOMMU 없음 → 삭제 / 기본+vIOMMU → pc,viommu=… / q35[,viommu=…] / 고정 버전[,viommu=…].
    /// </summary>
    public static async Task<HardwareEdit> MachineAsync(HardwareContext ctx)
    {
        var machine = PropertyString.Parse(ctx.Get("machine"), "type");
        var raw = machine.Get("type");
        var pinned = raw.Contains('-') ? raw : string.Empty; // pc-q35-9.2+pve1 처럼 버전이 붙은 값
        var type = raw.Contains("q35", StringComparison.Ordinal) ? "q35" : string.Empty;
        var versions = await MachineVersionsAsync(ctx);
        return new HardwareEdit
        {
            Title = Loc.T("Hw_Machine"),
            Fields =
            [
                new FormField
                {
                    Key = "type", LabelKey = "Hw_Machine", Kind = FormFieldKind.Choice, Initial = type,
                    Choices = [("", "Hw_MachineDefault"), ("q35", "q35")]
                },
                new FormField
                {
                    Key = "version", LabelKey = "HwEd_MachineVersion", Kind = FormFieldKind.Choice,
                    Advanced = true, Initial = pinned,
                    Choices = [("", ctx.IsWindows ? "HwEd_MachineImplicit" : "HwEd_MachineLatest"), .. versions,
                        .. Keep(versions, pinned)],
                    Hint = Loc.T("HwEd_MachineVersionHint")
                },
                new FormField
                {
                    Key = "viommu", LabelKey = "HwEd_Viommu", Kind = FormFieldKind.Choice, Advanced = true,
                    Initial = machine.Get("viommu"),
                    Choices = [("", "HwEd_ViommuNone"), ("intel", "Intel (AMD Compatible)"), ("virtio", "VirtIO")]
                }
            ],
            Validate = ValidateMachine,
            Build = values => One("machine",
                FormatMachine(V(values, "type"), V(values, "version"), V(values, "viommu")))
        };
    }

    private static string? ValidateMachine(IReadOnlyDictionary<string, string> values)
    {
        var q35 = V(values, "type") == "q35";
        var version = V(values, "version");
        if (version.Length > 0 && version.Contains("q35", StringComparison.Ordinal) != q35)
            return Loc.T("HwEd_MachineVersionMismatch");
        return !q35 && V(values, "viommu") == "intel" ? Loc.T("HwEd_IntelViommuQ35") : null;
    }

    internal static string FormatMachine(string type, string version, string viommu)
    {
        var suffix = viommu.Length > 0 ? $",viommu={viommu}" : string.Empty;
        if (version.Length > 0) return version + suffix;
        if (type == "q35") return "q35" + suffix;
        return viommu.Length > 0 ? "pc" + suffix : string.Empty;
    }

    /// <summary>노드의 QEMU 머신 버전(GET …/capabilities/qemu/machines) — 최신이 위로.</summary>
    private static async Task<IReadOnlyList<(string Value, string Label)>> MachineVersionsAsync(HardwareContext ctx)
    {
        try
        {
            var rows = await ctx.Api.Guests.MachinesAsync(ctx.Guest.Node, ctx.Get("arch"));
            var isArm = ctx.Get("arch") == "aarch64";
            return rows.Select(r => ActionHelpers.Value(r, "id"))
                .Where(id => id.Length > 0 && id.StartsWith(isArm ? "virt" : "pc", StringComparison.Ordinal))
                .OrderByDescending(id => id, StringComparer.Ordinal).Select(id => (id, id)).ToList();
        }
        catch (ProxmoxClient.Core.Api.ProxmoxApiException ex)
        {
            App.Log($"[하드웨어] 머신 버전 목록 조회 실패: {ex.Message}");
            return [];
        }
    }

    /// <summary>SCSI 컨트롤러 — 기본값(LSI 53C895A)은 삭제.</summary>
    public static HardwareEdit ScsiHw(HardwareContext ctx)
    {
        return new HardwareEdit
        {
            Title = Loc.T("Hw_ScsiHw"),
            Fields =
            [
                new FormField
                {
                    Key = "scsihw", LabelKey = "Hw_ScsiHw", Kind = FormFieldKind.Choice, Initial = ctx.Get("scsihw"),
                    Choices = [("", "Hw_ScsiDefault"), .. HardwareRender.ScsiControllers]
                }
            ],
            Build = values => One("scsihw", V(values, "scsihw"))
        };
    }
}
