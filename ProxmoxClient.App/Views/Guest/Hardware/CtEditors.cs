using System.Globalization;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>
///     CT 리소스 편집기 — 메모리·스왑, CPU, 마운트 포인트(새로·편집·미사용 연결), 장치 연결.
///     값 형식은 웹 UI(lxc/ResourceEdit·MPEdit·DeviceEdit)와 같다. 네트워크는 <c>CtNetworkEditor</c>.
/// </summary>
internal static partial class CtEditors
{
    private const long MiB = 1024 * 1024;
    private const int MaxMountPoints = 256;
    private const string DefaultMpSizeGib = "8";

    private static readonly (string Value, string Label)[] MountOptions =
    [
        ("discard", "discard"), ("lazytime", "lazytime"), ("noatime", "noatime"), ("nodev", "nodev"),
        ("noexec", "noexec"), ("nosuid", "nosuid")
    ];

    private static string V(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
    }

    private static bool IsIntOrEmpty(string text, int min, int max)
    {
        return text.Length == 0 || int.TryParse(text, out var v) && v >= min && v <= max;
    }

    // ------------------------------------------------------------ 메모리 · CPU

    /// <summary>메모리·스왑 — 둘 다 늘 보낸다(memory=…&amp;swap=…).</summary>
    public static async Task<HardwareEdit> MemoryAsync(HardwareContext ctx)
    {
        var host = await ctx.HostAsync();
        return new HardwareEdit
        {
            Title = Loc.T("HwEd_Memory"),
            Fields =
            [
                new FormField { Key = "memory", LabelKey = "HwEd_MemoryMiB", Required = true,
                    Initial = ctx.Get("memory") is { Length: > 0 } m ? m : "512",
                    Hint = host is null ? null : Loc.T("HostLimit_Memory", host.MemTotalBytes / MiB,
                        Math.Max(host.MemTotalBytes - host.MemUsedBytes, 0) / MiB) },
                new FormField { Key = "swap", LabelKey = "Ct_SwapMiB", Required = true,
                    Initial = ctx.Get("swap") is { Length: > 0 } s ? s : "512" }
            ],
            Validate = values => int.TryParse(V(values, "memory"), out var m) && m >= 16
                                 && int.TryParse(V(values, "swap"), out var s) && s >= 0
                ? null
                : Loc.T("Ct_MemoryInvalid"),
            Build = values => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["memory"] = V(values, "memory"), ["swap"] = V(values, "swap")
            }
        };
    }

    /// <summary>CPU — 코어(비우면 제한 없음=삭제), 고급: CPU 제한(0·빈칸=삭제)·CPU 단위(빈칸=삭제).</summary>
    public static async Task<HardwareEdit> CpuAsync(HardwareContext ctx)
    {
        var host = await ctx.HostAsync();
        return new HardwareEdit
        {
            Title = Loc.T("Ct_Cores"),
            Fields =
            [
                new FormField { Key = "cores", LabelKey = "Ct_Cores", Initial = ctx.Get("cores"),
                    Hint = host is null ? Loc.T("Ct_CoresHint")
                        : Loc.T("Ct_CoresHint") + " "
                          + Loc.T("HostLimit_Cores", host.CpuCores, Math.Max(host.CpuSockets, 1)) },
                new FormField { Key = "cpulimit", LabelKey = "HwEd_CpuLimit", Advanced = true,
                    Initial = ctx.Get("cpulimit"), Hint = Loc.T("HwEd_CpuLimitHint") },
                new FormField { Key = "cpuunits", LabelKey = "HwEd_CpuUnits", Advanced = true,
                    Initial = ctx.Get("cpuunits"), Hint = Loc.T("HwEd_CpuUnitsHint") }
            ],
            Validate = values =>
            {
                if (!IsIntOrEmpty(V(values, "cores"), 1, 8192)) return Loc.T("Ct_CoresRange");
                if (V(values, "cpulimit") is { Length: > 0 } limit
                    && !(double.TryParse(limit, NumberStyles.Float, CultureInfo.InvariantCulture, out var l) && l >= 0))
                    return Loc.T("HwEd_CpuLimitRange");
                return IsIntOrEmpty(V(values, "cpuunits"), 8, 500000) ? null : Loc.T("HwEd_CpuUnitsRange");
            },
            Build = values => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cores"] = V(values, "cores"),
                ["cpulimit"] = V(values, "cpulimit") == "0" ? string.Empty : V(values, "cpulimit"),
                ["cpuunits"] = V(values, "cpuunits")
            }
        };
    }

    // ------------------------------------------------------------ 장치 연결

    /// <summary>
    ///     장치 연결(root@pam 만) — /dev/… 경로, 고급: UID·GID·권한·읽기 전용(deny-write=on).
    ///     저장: 경로가 맨 앞, 나머지 이름순. 예: dev0=/dev/ttyUSB0,deny-write=on,gid=20,mode=0660
    /// </summary>
    public static HardwareEdit Device(HardwareContext ctx, string? key)
    {
        var target = key ?? ctx.FreeSlot("dev", MaxMountPoints);
        var dev = key is null ? PropertyString.Empty : PropertyString.Parse(ctx.Get(key), "path");
        return new HardwareEdit
        {
            Title = key is null ? Loc.T("CtAdd_Device") : Loc.T("Ct_Device", key),
            Fields =
            [
                new FormField { Key = "path", LabelKey = "Ct_DevicePath", Initial = dev.Get("path"), Required = true,
                    Hint = Loc.T("Ct_DevicePathHint") },
                new FormField { Key = "uid", LabelKey = "Ct_DeviceUid", Advanced = true, Initial = dev.Get("uid"),
                    Hint = Loc.T("Ct_DefaultZero") },
                new FormField { Key = "gid", LabelKey = "Ct_DeviceGid", Advanced = true, Initial = dev.Get("gid"),
                    Hint = Loc.T("Ct_DefaultZero") },
                new FormField { Key = "mode", LabelKey = "Ct_DeviceMode", Advanced = true, Initial = dev.Get("mode"),
                    Hint = Loc.T("Ct_DeviceModeHint") },
                new FormField { Key = "deny-write", LabelKey = "HwEd_ReadOnly", Kind = FormFieldKind.Bool,
                    Advanced = true, Initial = dev.IsOn("deny-write") ? "1" : "0" }
            ],
            Validate = values => target is null ? Loc.T("HwEd_NoFreeSlot", "dev") : ValidateDevice(values),
            Build = values =>
            {
                var parts = new List<string> { V(values, "path") };
                if (V(values, "deny-write") == "1") parts.Add("deny-write=on");
                if (V(values, "gid").Length > 0) parts.Add($"gid={V(values, "gid")}");
                if (V(values, "mode").Length > 0) parts.Add($"mode={V(values, "mode")}");
                if (V(values, "uid").Length > 0) parts.Add($"uid={V(values, "uid")}");
                return new Dictionary<string, string>(StringComparer.Ordinal) { [target!] = string.Join(',', parts) };
            }
        };
    }

    private static string? ValidateDevice(IReadOnlyDictionary<string, string> values)
    {
        if (!V(values, "path").StartsWith("/dev/", StringComparison.Ordinal)) return Loc.T("Ct_DevicePathHint");
        if (!IsIntOrEmpty(V(values, "uid"), 0, int.MaxValue) || !IsIntOrEmpty(V(values, "gid"), 0, int.MaxValue))
            return Loc.T("Ct_DeviceIdInvalid");
        var mode = V(values, "mode");
        return mode.Length == 0 || mode.Length == 4 && mode[0] == '0' && mode.Skip(1).All(c => c is >= '0' and <= '7')
            ? null
            : Loc.T("Ct_DeviceModeHint");
    }
}
