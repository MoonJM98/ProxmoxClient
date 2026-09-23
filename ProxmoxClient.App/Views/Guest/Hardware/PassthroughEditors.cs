using System.Text.RegularExpressions;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>
///     장치 연결 편집기 — USB·PCI·Virtiofs(웹 UI USBEdit·PCIEdit·VirtiofsEdit 와 같은 값 형식).
///     장치 목록은 노드 하드웨어(…/hardware/usb|pci)와 데이터센터 리소스 매핑(cluster/mapping/…)에서 읽는다.
/// </summary>
internal static partial class PassthroughEditors
{
    private const int MaxUsbOld = 5;
    private const int MaxUsbNew = 14;
    private const int MaxPci = 16;
    private const int MaxVirtiofs = 10;

    [GeneratedRegex(@"-(\d+)\.(\d+)")]
    private static partial Regex MachineVersion();

    [GeneratedRegex(@"^(0x)?([a-fA-F0-9]{4}):(0x)?([a-fA-F0-9]{4})$")]
    private static partial Regex UsbId();

    [GeneratedRegex(@"^\d+-\d+(\.\d+)*$")]
    private static partial Regex UsbPort();

    [GeneratedRegex("^0x[0-9a-fA-F]{4}$")]
    private static partial Regex PciId();

    private static string V(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
    }

    private static IReadOnlyDictionary<string, string> One(string key, string value)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };
    }

    /// <summary>표 조회 실패(권한·버전)는 빈 목록으로 — 편집 창은 열리고 직접 입력은 할 수 있다.</summary>
    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> TryTableAsync(HardwareContext ctx,
        string path)
    {
        try
        {
            return await ctx.Api.GetTableAsync(path);
        }
        catch (ProxmoxApiException ex)
        {
            App.Log($"[하드웨어] {path} 조회 실패: {ex.Message}");
            return [];
        }
    }

    private static async Task<IReadOnlyList<(string, string)>> MappingsAsync(HardwareContext ctx, string kind)
    {
        var rows = await TryTableAsync(ctx, $"cluster/mapping/{kind}?check-node={ActionHelpers.Seg(ctx.Guest.Node)}");
        return rows.Select(r => ActionHelpers.Value(r, "id")).Where(id => id.Length > 0)
            .Select(id => (id, id)).ToList();
    }

    // ------------------------------------------------------------ USB

    /// <summary>
    ///     USB 최대 수 — 머신 버전이 없거나 7.1 이상이고, OS 가 Linux 또는 Windows 8 이상이면 14(모두 xHCI), 아니면 5.
    /// </summary>
    internal static int MaxUsb(string ostype, string machine)
    {
        var version = MachineVersion().Match(machine);
        var newMachine = !version.Success
                         || int.Parse(version.Groups[1].Value) * 100 + int.Parse(version.Groups[2].Value) >= 701;
        var winNumber = ostype.StartsWith("win", StringComparison.Ordinal)
                        && int.TryParse(ostype[3..], out var n) ? n : 0;
        return newMachine && (ostype == "l26" || winNumber > 7) ? MaxUsbNew : MaxUsbOld;
    }

    /// <summary>USB — SPICE 포트 / 매핑된 장치 / 제조사:제품 ID / 포트. USB3 는 포트가 5개인 구형 조합에서만 고른다.</summary>
    public static async Task<HardwareEdit> UsbAsync(HardwareContext ctx, string? key)
    {
        var max = MaxUsb(ctx.Get("ostype"), ctx.Get("machine"));
        var target = key ?? ctx.FreeSlot("usb", max);
        var usb = key is null ? PropertyString.Empty : PropertyString.Parse(ctx.Get(key), "host");
        var host = usb.Get("host");
        var mode = key is null || host == "spice" ? "spice"
            : usb.Has("mapping") ? "mapped"
            : UsbPort().IsMatch(host) ? "port" : "device";
        var devices = (await TryTableAsync(ctx, $"{ctx.NodePath}/hardware/usb"))
            .Where(d => ActionHelpers.Value(d, "usbpath").Length > 0 && ActionHelpers.Value(d, "prodid").Length > 0
                        && ActionHelpers.Value(d, "class") != "9")
            .Select(UsbChoice).ToList();
        var fields = new List<FormField>
        {
            new() { Key = "mode", LabelKey = "HwEd_UsbMode", Kind = FormFieldKind.Choice, Initial = mode,
                Choices = [("spice", "HwEd_UsbSpice"), ("mapped", "HwEd_UsbMapped"), ("device", "HwEd_UsbDevice"),
                    ("port", "HwEd_UsbPort")] },
            new() { Key = "mapping", LabelKey = "HwEd_MappedDevice", Kind = FormFieldKind.Choice,
                Choices = await MappingsAsync(ctx, "usb"), Initial = usb.Get("mapping") },
            new() { Key = "device", LabelKey = "HwEd_UsbDeviceId", Kind = FormFieldKind.Choice,
                Choices = [.. devices, .. mode == "device" && devices.All(d => d.Item1 != NormalizeUsbId(host))
                    ? [(NormalizeUsbId(host), NormalizeUsbId(host))]
                    : Array.Empty<(string, string)>()],
                Initial = mode == "device" ? NormalizeUsbId(host) : "" },
            new() { Key = "port", LabelKey = "HwEd_UsbPortPath", Initial = mode == "port" ? host : "",
                Hint = Loc.T("HwEd_UsbPortHint") }
        };
        if (max == MaxUsbOld)
            fields.Add(new FormField { Key = "usb3", LabelKey = "HwEd_Usb3", Kind = FormFieldKind.Bool,
                Initial = key is null || usb.IsOn("usb3") ? "1" : "0" });
        return new HardwareEdit
        {
            Title = key is null ? Loc.T("HwAdd_Usb") : Loc.T("Hw_Usb", key),
            Fields = fields,
            Validate = values => target is null ? Loc.T("HwEd_NoFreeSlot", "usb") : ValidateUsb(values),
            Build = values => One(target!, FormatUsb(values, max == MaxUsbOld))
        };
    }

    private static (string, string) UsbChoice(IReadOnlyDictionary<string, string> d)
    {
        var id = NormalizeUsbId($"{ActionHelpers.Value(d, "vendid")}:{ActionHelpers.Value(d, "prodid")}");
        var name = $"{ActionHelpers.Value(d, "manufacturer")} {ActionHelpers.Value(d, "product")}".Trim();
        return (id, name.Length > 0 ? $"{id} — {name}" : id);
    }

    private static string NormalizeUsbId(string value)
    {
        var m = UsbId().Match(value);
        return m.Success ? $"{m.Groups[2].Value}:{m.Groups[4].Value}".ToLowerInvariant() : value;
    }

    private static string? ValidateUsb(IReadOnlyDictionary<string, string> values)
    {
        return V(values, "mode") switch
        {
            "mapped" when V(values, "mapping").Length == 0 => Loc.T("HwEd_PickDevice"),
            "device" when !UsbId().IsMatch(V(values, "device")) => Loc.T("HwEd_UsbIdInvalid"),
            "port" when !UsbPort().IsMatch(V(values, "port")) => Loc.T("HwEd_UsbPortInvalid"),
            _ => null
        };
    }

    internal static string FormatUsb(IReadOnlyDictionary<string, string> values, bool allowUsb3)
    {
        var value = V(values, "mode") switch
        {
            "mapped" => $"mapping={V(values, "mapping")}",
            "device" => $"host={NormalizeUsbId(V(values, "device"))}",
            "port" => $"host={V(values, "port")}",
            _ => "spice"
        };
        return allowUsb3 && V(values, "usb3") == "1" ? value + ",usb3=1" : value;
    }
}
