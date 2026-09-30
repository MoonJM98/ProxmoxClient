using System.Globalization;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>
///     VM 디스크 편집기 — 하드 디스크(새로·편집·미사용 연결·가져오기), CD/DVD, EFI, TPM, CloudInit 드라이브.
///     값 형식은 웹 UI(HDEdit·CDEdit·HDEfi·HDTPM·CIDriveEdit)와 같다.
/// </summary>
internal static class DiskEditors
{
    private const string DefaultSizeGib = "32";
    private static readonly string[] DiskBuses = ["ide", "sata", "scsi", "virtio"];
    private static readonly string[] CdBuses = ["ide", "sata", "scsi"];

    private static string V(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
    }

    private static IReadOnlyList<(string, string)> StorageChoices(IReadOnlyList<StorageInfo> storages)
    {
        return storages.Select(s => (s.Id, $"{s.Id} ({s.Type})")).ToList();
    }

    private static FormField SlotField(HardwareContext ctx, IEnumerable<string> buses, string preferred)
    {
        var slots = DiskOptions.FreeSlots(ctx, buses);
        var initial = slots.Any(s => s.Value == preferred) ? preferred
            : slots.FirstOrDefault(s => s.Value.StartsWith(DiskOptions.BusOf(preferred), StringComparison.Ordinal))
                .Value ?? slots.FirstOrDefault().Value ?? string.Empty;
        return new FormField
        {
            Key = "slot", LabelKey = "HwEd_BusDevice", Kind = FormFieldKind.Choice, Choices = slots, Initial = initial,
            Required = true, Hint = Loc.T("HwEd_ScsiControllerIs", HardwareRender.ScsiHw(ctx.Get("scsihw")))
        };
    }

    private static FormField FormatField()
    {
        return new FormField
        {
            Key = "format", LabelKey = "HwEd_Format", Kind = FormFieldKind.Choice, Advanced = true,
            Choices = [("", "HwEd_FormatAuto"), ("raw", "raw"), ("qcow2", "qcow2"), ("vmdk", "vmdk")],
            Hint = Loc.T("HwEd_FormatHint")
        };
    }

    /// <summary>형식을 고르지 않으면 웹 UI 처럼 qcow2 를 지원하는 저장소에서 qcow2 를 쓴다(그 밖은 저장소 기본).</summary>
    private static string ResolveFormat(IReadOnlyList<StorageInfo> storages, string storage, string chosen)
    {
        if (chosen.Length > 0) return chosen;
        return storages.FirstOrDefault(s => s.Id == storage) is { SupportsQcow2: true } ? "qcow2" : string.Empty;
    }

    // ------------------------------------------------------------ 하드 디스크

    /// <summary>새 하드 디스크 — 저장소:크기(GiB), 예: scsi1=local-lvm:32,iothread=on</summary>
    public static async Task<HardwareEdit> CreateDiskAsync(HardwareContext ctx)
    {
        var storages = await ctx.StoragesAsync("images");
        var preferredBus = DiskOptions.PreferredBus(ctx);
        return new HardwareEdit
        {
            Title = Loc.T("HwAdd_HardDisk"),
            Background = true,
            Fields =
            [
                SlotField(ctx, DiskBuses, $"{preferredBus}0"),
                new FormField { Key = "storage", LabelKey = "Table_Storage", Kind = FormFieldKind.Choice,
                    Choices = StorageChoices(storages), Required = true },
                new FormField
                {
                    Key = "size", LabelKey = "HwEd_DiskSizeGib", Initial = DefaultSizeGib, Required = true
                },
                FormatField(),
                .. DiskOptions.Fields(PropertyString.Empty, preferredBus, isCreate: true, ctx.Get("scsihw"))
            ],
            Validate = values => ValidSize(V(values, "size"))
                ? DiskOptions.Validate(values)
                : Loc.T("HwEd_DiskSizeRange"),
            Build = values =>
            {
                var slot = V(values, "slot");
                var storage = V(values, "storage");
                var drive = PropertyString.Parse($"{storage}:{V(values, "size")}", "file")
                    .With("format", ResolveFormat(storages, storage, V(values, "format")));
                return One(slot, DiskOptions.Apply(drive, values, DiskOptions.BusOf(slot)).Format("file"));
            }
        };
    }

    private static bool ValidSize(string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gib)
               && gib is >= 0.001 and <= 131072;
    }

    /// <summary>기존 디스크 편집 — 볼륨·크기 등 원래 키는 그대로 두고 옵션만 바꾼다.</summary>
    public static HardwareEdit EditDisk(HardwareContext ctx, string key)
    {
        var drive = PropertyString.Parse(ctx.Get(key), "file");
        var bus = DiskOptions.BusOf(key);
        return new HardwareEdit
        {
            Title = Loc.T("HwEd_EditDisk", key, drive.Get("file")),
            Background = true,
            Fields = DiskOptions.Fields(drive, bus, isCreate: false, ctx.Get("scsihw")),
            Validate = DiskOptions.Validate,
            Build = values => One(key, DiskOptions.Apply(drive, values, bus).Format("file"))
        };
    }

    /// <summary>
    ///     미사용 디스크 연결 — 새 자리에 그 볼륨을 붙인다. 서버가 unusedN 항목을 알아서 정리하므로
    ///     unusedN 을 지우지 않는다(지우면 디스크 이미지가 삭제된다).
    /// </summary>
    public static HardwareEdit AttachUnused(HardwareContext ctx, string unusedKey)
    {
        var volume = ctx.Get(unusedKey);
        var preferredBus = DiskOptions.PreferredBus(ctx);
        return new HardwareEdit
        {
            Title = Loc.T("HwEd_AttachUnused", volume),
            Background = true,
            Fields =
            [
                SlotField(ctx, DiskBuses, $"{preferredBus}0"),
                .. DiskOptions.Fields(PropertyString.Empty, preferredBus, isCreate: true, ctx.Get("scsihw"))
            ],
            Validate = DiskOptions.Validate,
            Build = values =>
            {
                var slot = V(values, "slot");
                var drive = PropertyString.Parse(volume, "file");
                return One(slot, DiskOptions.Apply(drive, values, DiskOptions.BusOf(slot)).Format("file"));
            }
        };
    }

    /// <summary>디스크 가져오기(PVE 9) — 가져오기 저장소의 이미지를 대상 저장소로 복사해 붙인다. 예: sata1=local-lvm:0,import-from=…</summary>
    public static async Task<HardwareEdit> ImportDiskAsync(HardwareContext ctx)
    {
        var targets = await ctx.StoragesAsync("images");
        var images = new List<(string, string)>();
        foreach (var storage in await ctx.StoragesAsync("import"))
            images.AddRange((await ctx.Api.GetStorageContentAsync(ctx.Guest.Node, storage.Id, "import"))
                .Where(f => f.Format is "qcow2" or "vmdk" or "raw").Select(f => (f.Volid, f.Volid)));
        var preferredBus = DiskOptions.PreferredBus(ctx);
        return new HardwareEdit
        {
            Title = Loc.T("HwAdd_ImportDisk"),
            Background = true,
            Fields =
            [
                SlotField(ctx, DiskBuses, $"{preferredBus}0"),
                new FormField { Key = "source", LabelKey = "HwEd_ImportImage", Kind = FormFieldKind.Choice,
                    Choices = images, Required = true, Hint = images.Count == 0 ? Loc.T("HwEd_NoImportImages") : null },
                new FormField { Key = "storage", LabelKey = "HwEd_TargetStorage", Kind = FormFieldKind.Choice,
                    Choices = StorageChoices(targets), Required = true },
                FormatField(),
                .. DiskOptions.Fields(PropertyString.Empty, preferredBus, isCreate: true, ctx.Get("scsihw"))
            ],
            Validate = DiskOptions.Validate,
            Build = values =>
            {
                var slot = V(values, "slot");
                var storage = V(values, "storage");
                var drive = PropertyString.Parse($"{storage}:0", "file")
                    .With("import-from", V(values, "source"))
                    .With("format", ResolveFormat(targets, storage, V(values, "format")));
                return One(slot, DiskOptions.Apply(drive, values, DiskOptions.BusOf(slot)).Format("file"));
            }
        };
    }

    // ------------------------------------------------------------ CD/DVD

    /// <summary>
    ///     CD/DVD — ISO 이미지 / 실제 드라이브 / 미디어 없음. 새로 만들면 ide2(비었으면)에.
    ///     편집할 때 media 외의 원래 키(size 등)는 뒤에 그대로 둔다. 예: ide2=local:iso/debian.iso,media=cdrom
    /// </summary>
    public static async Task<HardwareEdit> CdromAsync(HardwareContext ctx, string? key)
    {
        var isos = await ctx.IsoChoicesAsync();
        var drive = key is null ? PropertyString.Empty : PropertyString.Parse(ctx.Get(key), "file");
        var file = drive.Get("file");
        var media = file switch { "cdrom" => "cdrom", "none" or "" when key is not null => "none", _ => "iso" };
        var fields = new List<FormField>();
        if (key is null) fields.Add(SlotField(ctx, CdBuses, "ide2"));
        fields.Add(new FormField
        {
            Key = "media", LabelKey = "HwEd_CdMedia", Kind = FormFieldKind.Choice, Initial = media,
            Choices = [("iso", "HwEd_CdIso"), ("cdrom", "HwEd_CdPhysical"), ("none", "HwEd_CdNone")]
        });
        fields.Add(new FormField
        {
            Key = "iso", LabelKey = "DeviceTable_Iso", Kind = FormFieldKind.Choice,
            Choices = [.. isos, .. media == "iso" && file.Length > 0 && isos.All(i => i.Value != file) ? [(file, file)]
                : Array.Empty<(string, string)>()],
            Initial = media == "iso" ? file : string.Empty
        });
        return new HardwareEdit
        {
            Title = key is null ? Loc.T("HwAdd_Cdrom") : Loc.T("Hw_Cdrom", key),
            Background = true,
            Fields = fields,
            Validate = values => V(values, "media") == "iso" && V(values, "iso").Length == 0
                ? Loc.T("HwEd_CdPickIso")
                : null,
            Build = values =>
            {
                var target = key ?? V(values, "slot");
                var source = V(values, "media") switch { "cdrom" => "cdrom", "none" => "none", _ => V(values, "iso") };
                var rest = drive.Items.Where(kv => kv.Key is not ("file" or "media"));
                var value = PropertyString.Parse($"{source},media=cdrom", "file")
                    .With(rest.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));
                return One(target, value.Format("file"));
            }
        };
    }

    // ------------------------------------------------------------ EFI · TPM · CloudInit

    /// <summary>EFI 디스크 — efidisk0=저장소:1,efitype=4m,pre-enrolled-keys=1[,format=…] (크기 1 은 서버가 정한다).</summary>
    public static async Task<HardwareEdit> EfiAsync(HardwareContext ctx)
    {
        var storages = await ctx.StoragesAsync("images");
        return new HardwareEdit
        {
            Title = Loc.T("HwAdd_Efi"),
            Background = true,
            Fields =
            [
                new FormField { Key = "storage", LabelKey = "HwEd_EfiStorage", Kind = FormFieldKind.Choice,
                    Choices = StorageChoices(storages), Required = true,
                    Hint = ctx.Get("bios") == "ovmf" ? null : Loc.T("HwEd_EfiNeedsOvmf") },
                FormatField(),
                new FormField { Key = "keys", LabelKey = "HwEd_PreEnroll", Kind = FormFieldKind.Bool, Initial = "1",
                    Hint = Loc.T("HwEd_PreEnrollHint") }
            ],
            Build = values =>
            {
                var storage = V(values, "storage");
                var drive = PropertyString.Parse($"{storage}:1", "file").With("efitype", "4m")
                    .With("pre-enrolled-keys", V(values, "keys") == "1" ? "1" : null)
                    .With("format", ResolveFormat(storages, storage, V(values, "format")));
                return One("efidisk0", drive.Format("file"));
            }
        };
    }

    /// <summary>TPM 상태 — tpmstate0=저장소:1,version=v2.0 (버전은 늘 보낸다).</summary>
    public static async Task<HardwareEdit> TpmAsync(HardwareContext ctx)
    {
        var storages = await ctx.StoragesAsync("images");
        return new HardwareEdit
        {
            Title = Loc.T("HwAdd_Tpm"),
            Fields =
            [
                new FormField { Key = "storage", LabelKey = "HwEd_TpmStorage", Kind = FormFieldKind.Choice,
                    Choices = StorageChoices(storages), Required = true },
                new FormField { Key = "version", LabelKey = "HwEd_TpmVersion", Kind = FormFieldKind.Choice,
                    Choices = [("v2.0", "v2.0"), ("v1.2", "v1.2")], Initial = "v2.0" }
            ],
            Build = values => One("tpmstate0", $"{V(values, "storage")}:1,version={V(values, "version")}")
        };
    }

    /// <summary>CloudInit 드라이브 — ide2(비었으면)=저장소:cloudinit[,format=qcow2].</summary>
    public static async Task<HardwareEdit> CloudInitAsync(HardwareContext ctx)
    {
        var storages = await ctx.StoragesAsync("images");
        return new HardwareEdit
        {
            Title = Loc.T("HwAdd_CloudInit"),
            Fields =
            [
                SlotField(ctx, CdBuses, "ide2"),
                new FormField { Key = "storage", LabelKey = "Table_Storage", Kind = FormFieldKind.Choice,
                    Choices = StorageChoices(storages), Required = true },
                FormatField()
            ],
            Build = values =>
            {
                var storage = V(values, "storage");
                var drive = PropertyString.Parse($"{storage}:cloudinit", "file")
                    .With("format", ResolveFormat(storages, storage, V(values, "format")));
                return One(V(values, "slot"), drive.Format("file"));
            }
        };
    }

    private static IReadOnlyDictionary<string, string> One(string key, string value)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };
    }
}
