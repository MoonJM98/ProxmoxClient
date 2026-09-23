using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>장치 연결 편집기 — PCI 통과와 Virtiofs.</summary>
internal static partial class PassthroughEditors
{
    /// <summary>PCI 의 이름순 키(웹 UI printPropertyString) — host 는 키 없이 맨 앞.</summary>
    private static readonly string[] PciSortedKeys =
    [
        "device-id", "mapping", "mdev", "pcie", "rombar", "romfile", "sub-device-id", "sub-vendor-id",
        "vendor-id", "x-vga"
    ];

    /// <summary>
    ///     PCI 장치 — 매핑된 장치 또는 원시 장치(모든 기능), MDev·주 GPU, 고급: ROM-Bar(기본 켬)·ID 들·PCI-Express(q35 만).
    ///     예: hostpci0=0000:01:00,pcie=1,x-vga=1 / hostpci1=mapping=gpu1,mdev=nvidia-63,rombar=0
    /// </summary>
    public static async Task<HardwareEdit> PciAsync(HardwareContext ctx, string? key)
    {
        var target = key ?? ctx.FreeSlot("hostpci", MaxPci);
        var pci = key is null ? PropertyString.Empty : PropertyString.Parse(ctx.Get(key), "host");
        var host = pci.Get("host");
        if (host.Length > 0 && host.Count(c => c == ':') == 1) host = "0000:" + host; // 도메인 없는 옛 값
        var multifunction = host.Length > 0 && !host.Contains('.');
        var devices = (await TryTableAsync(ctx, $"{ctx.NodePath}/hardware/pci"))
            .Select(d => (ActionHelpers.Value(d, "id"),
                $"{ActionHelpers.Value(d, "id")} — {ActionHelpers.Value(d, "vendor_name")} "
                + ActionHelpers.Value(d, "device_name")))
            .Where(d => d.Item1.Length > 0).ToList();
        var hostChoice = multifunction ? host + ".0" : host;
        var isQ35 = ctx.Get("machine").Contains("q35", StringComparison.Ordinal);
        return new HardwareEdit
        {
            Title = key is null ? Loc.T("HwAdd_Pci") : Loc.T("Hw_Pci", key),
            Fields =
            [
                new FormField { Key = "mode", LabelKey = "HwEd_PciMode", Kind = FormFieldKind.Choice,
                    Initial = key is null || pci.Has("mapping") ? "mapped" : "raw",
                    Choices = [("mapped", "HwEd_PciMapped"), ("raw", "HwEd_PciRaw")] },
                new FormField { Key = "mapping", LabelKey = "HwEd_MappedDevice", Kind = FormFieldKind.Choice,
                    Choices = await MappingsAsync(ctx, "pci"), Initial = pci.Get("mapping") },
                new FormField { Key = "host", LabelKey = "HwEd_PciDevice", Kind = FormFieldKind.Choice,
                    Choices = [.. devices, .. hostChoice.Length > 0 && devices.All(d => d.Item1 != hostChoice)
                        ? [(hostChoice, hostChoice)] : Array.Empty<(string, string)>()],
                    Initial = hostChoice },
                Check("multifunction", "HwEd_AllFunctions", multifunction, Loc.T("HwEd_AllFunctionsHint")),
                new FormField { Key = "mdev", LabelKey = "HwEd_Mdev", Initial = pci.Get("mdev"),
                    Hint = Loc.T("HwEd_MdevHint") },
                Check("x-vga", "HwEd_PrimaryGpu", pci.IsOn("x-vga")),
                Check("rombar", "HwEd_RomBar", pci.Get("rombar") != "0", advanced: true),
                new FormField { Key = "vendor-id", LabelKey = "HwEd_VendorId", Advanced = true,
                    Initial = pci.Get("vendor-id"), Hint = Loc.T("HwEd_FromDevice") },
                new FormField { Key = "device-id", LabelKey = "HwEd_DeviceId", Advanced = true,
                    Initial = pci.Get("device-id"), Hint = Loc.T("HwEd_FromDevice") },
                Check("pcie", "HwEd_Pcie", pci.IsOn("pcie"), isQ35 ? null : Loc.T("HwEd_PcieQ35Only"), advanced: true),
                new FormField { Key = "sub-vendor-id", LabelKey = "HwEd_SubVendorId", Advanced = true,
                    Initial = pci.Get("sub-vendor-id"), Hint = Loc.T("HwEd_FromDevice") },
                new FormField { Key = "sub-device-id", LabelKey = "HwEd_SubDeviceId", Advanced = true,
                    Initial = pci.Get("sub-device-id"), Hint = Loc.T("HwEd_FromDevice") }
            ],
            Validate = values => target is null ? Loc.T("HwEd_NoFreeSlot", "hostpci") : ValidatePci(values, isQ35),
            Build = values => One(target!, FormatPci(values, pci.Get("romfile")))
        };
    }

    private static FormField Check(string key, string labelKey, bool on, string? hint = null, bool advanced = false)
    {
        return new FormField
        {
            Key = key, LabelKey = labelKey, Kind = FormFieldKind.Bool, Initial = on ? "1" : "0", Hint = hint,
            Advanced = advanced
        };
    }

    private static string? ValidatePci(IReadOnlyDictionary<string, string> values, bool isQ35)
    {
        var mapped = V(values, "mode") == "mapped";
        if (mapped ? V(values, "mapping").Length == 0 : V(values, "host").Length == 0) return Loc.T("HwEd_PickDevice");
        if (V(values, "pcie") == "1" && !isQ35) return Loc.T("HwEd_PcieQ35Only");
        var ids = new[] { "vendor-id", "device-id", "sub-vendor-id", "sub-device-id" };
        return ids.All(k => V(values, k).Length == 0 || PciId().IsMatch(V(values, k)))
            ? null
            : Loc.T("HwEd_PciIdInvalid");
    }

    /// <summary>host(또는 없음) 다음 나머지 키 이름순. 모든 기능이면 host 의 ".기능번호" 를 뗀다. ROM-Bar 는 끌 때만 0.</summary>
    internal static string FormatPci(IReadOnlyDictionary<string, string> values, string romfile)
    {
        var mapped = V(values, "mode") == "mapped";
        var host = V(values, "host");
        if (!mapped && V(values, "multifunction") == "1" && V(values, "mdev").Length == 0 && host.Contains('.'))
            host = host[..host.LastIndexOf('.')];
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mapping"] = mapped ? V(values, "mapping") : "",
            ["mdev"] = V(values, "mdev"),
            ["pcie"] = V(values, "pcie") == "1" ? "1" : "",
            ["rombar"] = V(values, "rombar") == "1" ? "" : "0",
            ["romfile"] = romfile,
            ["x-vga"] = V(values, "x-vga") == "1" ? "1" : "",
            ["vendor-id"] = V(values, "vendor-id"), ["device-id"] = V(values, "device-id"),
            ["sub-vendor-id"] = V(values, "sub-vendor-id"), ["sub-device-id"] = V(values, "sub-device-id")
        };
        var parts = new List<string>();
        if (!mapped) parts.Add(host);
        parts.AddRange(PciSortedKeys.Where(k => map[k].Length > 0).Select(k => $"{k}={map[k]}"));
        return string.Join(',', parts);
    }

    // ------------------------------------------------------------ Virtiofs

    /// <summary>
    ///     Virtiofs — 디렉터리 매핑 ID, 고급: 캐시·xattr·ACL(켜면 xattr 포함)·Direct IO.
    ///     예: virtiofs0=share,cache=always,direct-io=1,expose-acl=1
    /// </summary>
    public static async Task<HardwareEdit> VirtiofsAsync(HardwareContext ctx, string? key)
    {
        var target = key ?? ctx.FreeSlot("virtiofs", MaxVirtiofs);
        var fs = key is null ? PropertyString.Empty : PropertyString.Parse(ctx.Get(key), "dirid");
        var dirs = await MappingsAsync(ctx, "dir");
        return new HardwareEdit
        {
            Title = key is null ? Loc.T("HwAdd_Virtiofs") : Loc.T("Hw_Virtiofs", key),
            Fields =
            [
                new FormField { Key = "dirid", LabelKey = "HwEd_DirId", Kind = FormFieldKind.Choice, Required = true,
                    Choices = [.. dirs, .. fs.Get("dirid") is { Length: > 0 } d && dirs.All(x => x.Item1 != d)
                        ? [(d, d)] : Array.Empty<(string, string)>()],
                    Initial = fs.Get("dirid"), Hint = Loc.T("HwEd_DirIdHint") },
                new FormField { Key = "cache", LabelKey = "HwEd_Cache", Kind = FormFieldKind.Choice, Advanced = true,
                    Initial = fs.Get("cache"),
                    Choices = [("", "HwEd_VirtiofsCacheDefault"), ("auto", "auto"), ("always", "always"),
                        ("metadata", "metadata"), ("never", "never")] },
                Check("expose-xattr", "HwEd_Xattr", fs.IsOn("expose-xattr"), advanced: true),
                Check("expose-acl", "HwEd_Acl", fs.IsOn("expose-acl"), Loc.T("HwEd_AclHint"), advanced: true),
                Check("direct-io", "HwEd_DirectIo", fs.IsOn("direct-io"), advanced: true)
            ],
            Validate = _ => target is null ? Loc.T("HwEd_NoFreeSlot", "virtiofs") : null,
            Build = values =>
            {
                var parts = new List<string> { V(values, "dirid") };
                if (V(values, "cache").Length > 0) parts.Add($"cache={V(values, "cache")}");
                if (V(values, "direct-io") == "1") parts.Add("direct-io=1");
                if (V(values, "expose-acl") == "1") parts.Add("expose-acl=1");
                else if (V(values, "expose-xattr") == "1") parts.Add("expose-xattr=1");
                return One(target!, string.Join(',', parts));
            }
        };
    }
}
