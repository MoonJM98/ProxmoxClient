using System.Globalization;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>
///     VM 디스크의 공통 옵션 칸(웹 UI HDEdit 의 디스크·대역폭 탭)과, 입력을 드라이브 문자열에 반영하는 규칙.
///     반영할 때 기존 키는 제자리를 지키고 새 키는 뒤에 붙는다(웹 UI printQemuDrive 와 같다).
/// </summary>
internal static class DiskOptions
{
    /// <summary>버스별 최대 장치 수(웹 UI diskControllerMaxIDs).</summary>
    public static readonly IReadOnlyDictionary<string, int> BusMax = new Dictionary<string, int>
    {
        ["ide"] = 4, ["sata"] = 6, ["scsi"] = 31, ["virtio"] = 16
    };

    private static readonly (string Value, string Label)[] CacheChoices =
    [
        ("", "HwEd_CacheDefault"), ("directsync", "Direct sync"), ("writethrough", "Write through"),
        ("writeback", "Write back"), ("unsafe", "Write back (unsafe)"), ("none", "No cache")
    ];

    private static readonly (string Value, string Label)[] AioChoices =
        [("", "HwEd_AioDefault"), ("io_uring", "io_uring"), ("native", "native"), ("threads", "threads")];

    /// <summary>대역폭 칸(키, 라벨, 정수만인지).</summary>
    private static readonly (string Key, string LabelKey, bool Integer)[] Bandwidth =
    [
        ("mbps_rd", "HwEd_ReadMbps", false), ("mbps_wr", "HwEd_WriteMbps", false),
        ("iops_rd", "HwEd_ReadIops", true), ("iops_wr", "HwEd_WriteIops", true),
        ("mbps_rd_max", "HwEd_ReadMbpsBurst", false), ("mbps_wr_max", "HwEd_WriteMbpsBurst", false),
        ("iops_rd_max", "HwEd_ReadIopsBurst", true), ("iops_wr_max", "HwEd_WriteIopsBurst", true)
    ];

    private static string V(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
    }

    private static bool On(PropertyString drive, string key)
    {
        return drive.IsOn(key);
    }

    /// <summary>
    ///     디스크 옵션 칸. <paramref name="bus" /> 가 VirtIO 면 SSD 를 뺀다(웹 UI 는 끔).
    ///     새 디스크의 IO 스레드는 VirtIO, 또는 VirtIO SCSI single 컨트롤러의 SCSI 에서 기본으로 켠다.
    /// </summary>
    public static IReadOnlyList<FormField> Fields(PropertyString drive, string bus, bool isCreate, string scsihw)
    {
        var iothreadDefault = isCreate && (bus == "virtio" || bus == "scsi" && scsihw == "virtio-scsi-single");
        var fields = new List<FormField>
        {
            new() { Key = "cache", LabelKey = "HwEd_Cache", Kind = FormFieldKind.Choice, Choices = CacheChoices,
                Initial = drive.Get("cache") == "off" ? "none" : drive.Get("cache") },
            Check("discard", "HwEd_Discard", On(drive, "discard")),
            Check("iothread", "HwEd_IoThread", isCreate ? iothreadDefault : On(drive, "iothread"),
                Loc.T("HwEd_IoThreadHint")),
            Check("ssd", "HwEd_Ssd", On(drive, "ssd"), Loc.T("HwEd_SsdHint"), advanced: true),
            Check("ro", "HwEd_ReadOnly", On(drive, "ro"), Loc.T("HwEd_ReadOnlyHint"), advanced: true),
            Check("backup", "HwEd_Backup", drive.Get("backup") != "0", advanced: true),
            Check("noreplicate", "HwEd_SkipReplication", drive.Get("replicate") is "0" or "no", advanced: true),
            new() { Key = "aio", LabelKey = "HwEd_Aio", Kind = FormFieldKind.Choice, Choices = AioChoices,
                Initial = drive.Get("aio"), Advanced = true },
            new() { Key = "__bandwidth", LabelKey = "HwEd_Bandwidth", Kind = FormFieldKind.Section }
        };
        fields.AddRange(Bandwidth.Select(b => new FormField
        {
            Key = b.Key, LabelKey = b.LabelKey, Initial = drive.Get(b.Key),
            Hint = Loc.T(b.Key.EndsWith("_max", StringComparison.Ordinal) ? "HwEd_BurstHint" : "HwEd_UnlimitedHint")
        }));
        return fields;
    }

    private static FormField Check(string key, string labelKey, bool on, string? hint = null, bool advanced = false)
    {
        return new FormField
        {
            Key = key, LabelKey = labelKey, Kind = FormFieldKind.Bool, Initial = on ? "1" : "0", Hint = hint,
            Advanced = advanced
        };
    }

    /// <summary>
    ///     입력을 드라이브에 반영한다 — 순서: backup, replicate, discard, ssd, iothread, ro, cache, aio, 대역폭.
    ///     VirtIO 는 SSD 를, IDE·SATA 는 IO 스레드·읽기 전용을 쓸 수 없어 뺀다.
    /// </summary>
    public static PropertyString Apply(PropertyString drive, IReadOnlyDictionary<string, string> values, string bus)
    {
        var threads = bus is "virtio" or "scsi";
        string? Flag(string key, bool allowed = true) => allowed && V(values, key) == "1" ? "on" : null;
        var result = drive
            .With("backup", V(values, "backup") == "1" ? null : "0")
            .With("replicate", V(values, "noreplicate") == "1" ? "no" : null)
            .With("discard", Flag("discard"))
            .With("ssd", Flag("ssd", bus != "virtio"))
            .With("iothread", Flag("iothread", threads))
            .With("ro", Flag("ro", threads))
            .With("cache", V(values, "cache"))
            .With("aio", V(values, "aio"));
        return Bandwidth.Aggregate(result, (d, b) => d.With(b.Key, V(values, b.Key)));
    }

    /// <summary>대역폭 칸 검사 — 비었거나(무제한) 양수. ops 는 정수 10 이상.</summary>
    public static string? Validate(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, labelKey, integer) in Bandwidth)
        {
            var text = V(values, key);
            if (text.Length == 0) continue;
            var ok = integer
                ? int.TryParse(text, out var i) && i >= 10
                : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 1;
            if (!ok) return Loc.T("HwEd_BandwidthInvalid", Loc.T(labelKey));
        }

        return null;
    }

    /// <summary>
    ///     비어 있는 자리 목록(ide0, sata1, scsi2…) — 웹 UI 의 버스·번호 두 칸을 겹치지 않는 한 칸으로 합친다.
    /// </summary>
    public static IReadOnlyList<(string Value, string Label)> FreeSlots(HardwareContext ctx, IEnumerable<string> buses)
    {
        return buses.SelectMany(bus => Enumerable.Range(0, BusMax[bus]).Select(i => $"{bus}{i}"))
            .Where(key => !ctx.Config.Current.ContainsKey(key) && !ctx.Effective.ContainsKey(key))
            .Select(key => (key, key)).ToList();
    }

    /// <summary>
    ///     새 디스크의 기본 버스 — 이미 쓰는(CD 제외) 디스크가 가장 많은 버스, 같으면 OS 에 맞는 순서
    ///     (Linux: scsi > virtio > sata > ide, 그 밖: ide > sata > scsi > virtio). 웹 UI ControllerSelector 와 같다.
    /// </summary>
    public static string PreferredBus(HardwareContext ctx)
    {
        string[] order = ctx.Get("ostype") == "l26"
            ? ["scsi", "virtio", "sata", "ide"]
            : ["ide", "sata", "scsi", "virtio"];
        var counts = order.ToDictionary(b => b, b => ctx.Effective
            .Count(kv => kv.Key.StartsWith(b, StringComparison.Ordinal) && kv.Key.Length > b.Length
                         && char.IsDigit(kv.Key[b.Length])
                         && !kv.Value.Contains("media=cdrom", StringComparison.Ordinal)));
        return order.OrderByDescending(b => counts[b]).ThenBy(b => Array.IndexOf(order, b)).First();
    }

    /// <summary>자리 키에서 버스 이름(scsi3 → scsi).</summary>
    public static string BusOf(string slot)
    {
        return new string(slot.TakeWhile(char.IsLetter).ToArray());
    }
}
