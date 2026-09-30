using System.Globalization;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>
///     VM 시스템 편집기 — 메모리·프로세서·BIOS·디스플레이·머신·SCSI 컨트롤러(웹 UI qemu/*Edit.js 와 같은 칸·값 형식).
/// </summary>
internal static partial class SystemEditors
{
    private const long MiB = 1024 * 1024;
    private const string DefaultCpuType = "x86-64-v2-AES";

    private static string V(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
    }

    private static bool IsIntOrEmpty(string text, int min, int max)
    {
        return text.Length == 0 || int.TryParse(text, out var v) && v >= min && v <= max;
    }

    // ------------------------------------------------------------ 메모리

    /// <summary>
    ///     메모리 — 최소 메모리(벌루닝)·공유·KSM 은 고급. 저장 규칙(MemoryEdit.onGetValues):
    ///     벌루닝 끔 → balloon=0·shares 삭제 / 최소 = 메모리 → balloon·shares 삭제 / 그 밖 → balloon=값.
    /// </summary>
    public static async Task<HardwareEdit> MemoryAsync(HardwareContext ctx)
    {
        var host = await ctx.HostAsync();
        var memory = ctx.Get("memory") is { Length: > 0 } m ? m : "512";
        var balloon = ctx.Get("balloon");
        var hotplugMemory = ctx.Get("hotplug").Split(',').Contains("memory");
        // 최소 메모리가 없거나 메모리와 같았으면, 고급 칸을 건드리지 않은 채 메모리만 바꿔도 함께 따라간다(웹 UI 와 같이)
        var follows = balloon.Length == 0 || balloon == memory ? memory : null;
        return new HardwareEdit
        {
            Title = Loc.T("HwEd_Memory"),
            Fields =
            [
                new FormField
                {
                    Key = "memory", LabelKey = "HwEd_MemoryMiB", Initial = memory, Required = true,
                    Hint = host is null ? null
                        : Loc.T("HostLimit_Memory", host.MemTotalBytes / MiB,
                            Math.Max(host.MemTotalBytes - host.MemUsedBytes, 0) / MiB)
                },
                new FormField
                {
                    Key = "balloon", LabelKey = "HwEd_MinMemory", Advanced = true,
                    Initial = balloon is { Length: > 0 } b && b != "0" ? b : memory,
                    Hint = Loc.T("HwEd_MinMemoryHint")
                },
                new FormField
                {
                    Key = "shares", LabelKey = "HwEd_Shares", Advanced = true, Initial = ctx.Get("shares"),
                    Hint = Loc.T("HwEd_SharesHint")
                },
                new FormField
                {
                    Key = "ballooning", LabelKey = "HwEd_Ballooning", Kind = FormFieldKind.Bool, Advanced = true,
                    Initial = balloon == "0" ? "0" : "1"
                },
                // KSM 끄기(allow-ksm)는 9.0+ — 낮은 서버엔 편집 창이 설정 저장 요청을 보고 이 칸을 뺀다
                new FormField
                {
                    Key = "allow-ksm", LabelKey = "HwEd_AllowKsm", Kind = FormFieldKind.Bool, Advanced = true,
                    Initial = ctx.Get("allow-ksm") == "0" ? "0" : "1"
                }
            ],
            Validate = values => ValidateMemory(Follow(values, follows), hotplugMemory),
            Build = values => BuildMemory(Follow(values, follows))
        };
    }

    private static string? ValidateMemory(IReadOnlyDictionary<string, string> values, bool hotplugMemory)
    {
        var min = hotplugMemory ? 1024 : 16;
        if (!int.TryParse(V(values, "memory"), out var memory) || memory < min)
            return Loc.T("HwEd_MemoryMin", min);
        if (V(values, "ballooning") == "1"
            && (!int.TryParse(V(values, "balloon"), out var balloon) || balloon < 1 || balloon > memory))
            return Loc.T("HwEd_BalloonRange");
        return IsIntOrEmpty(V(values, "shares"), 0, 50000) ? null : Loc.T("HwEd_SharesRange");
    }

    /// <summary>최소 메모리 칸이 처음 값(=옛 메모리) 그대로면 새 메모리로 바꿔 본다.</summary>
    internal static IReadOnlyDictionary<string, string> Follow(IReadOnlyDictionary<string, string> values,
        string? initialMemory)
    {
        if (initialMemory is null || V(values, "balloon") != initialMemory) return values;
        return new Dictionary<string, string>(values, StringComparer.Ordinal) { ["balloon"] = V(values, "memory") };
    }

    internal static IReadOnlyDictionary<string, string> BuildMemory(IReadOnlyDictionary<string, string> values)
    {
        var memory = V(values, "memory");
        var changes = new Dictionary<string, string>(StringComparer.Ordinal) { ["memory"] = memory };
        if (V(values, "ballooning") != "1")
        {
            changes["balloon"] = "0";
            changes["shares"] = string.Empty;
        }
        else if (V(values, "balloon") is var balloon && (balloon.Length == 0 || balloon == memory))
        {
            changes["balloon"] = string.Empty;
            changes["shares"] = string.Empty;
        }
        else
        {
            changes["balloon"] = balloon;
            changes["shares"] = V(values, "shares");
        }

        // KSM 은 켜진 게 기본 — 끌 때만 0, 켜면 삭제(칸이 없는 낮은 서버는 건드리지 않는다)
        if (values.ContainsKey("allow-ksm"))
            changes["allow-ksm"] = V(values, "allow-ksm") == "1" ? string.Empty : "0";
        return changes;
    }

    // ------------------------------------------------------------ 프로세서

    /// <summary>
    ///     프로세서 — 소켓·코어·종류, 고급: vCPU·CPU 제한·affinity·CPU 단위·NUMA·CPU 플래그.
    ///     종류를 비우면(x86) 웹 UI 처럼 cpu=x86-64-v2-AES 를 명시해서 보낸다. cpu 의 다른 옵션은 그대로 둔다.
    /// </summary>
    public static async Task<HardwareEdit> ProcessorAsync(HardwareContext ctx)
    {
        var host = await ctx.HostAsync();
        var models = await CpuModelsAsync(ctx);
        var cpu = PropertyString.Parse(ctx.Get("cpu"), "cputype");
        var isArm = ctx.Get("arch") == "aarch64";
        // cpu 가 없으면 서버 기본은 kvm64(ARM 은 host), x86-64-v2-AES 는 "기본값" 항목으로 보인다
        var type = !ctx.Effective.ContainsKey("cpu") ? isArm ? "host" : "kvm64"
            : cpu.Get("cputype") == DefaultCpuType && !isArm ? "" : cpu.Get("cputype");
        return new HardwareEdit
        {
            Title = Loc.T("HwEd_Processors"),
            Fields =
            [
                new FormField { Key = "sockets", LabelKey = "HwEd_Sockets", Initial = Or(ctx.Get("sockets"), "1") },
                new FormField
                {
                    Key = "cores", LabelKey = "HwEd_Cores", Initial = Or(ctx.Get("cores"), "1"),
                    Hint = host is null ? null : Loc.T("HostLimit_Cores", host.CpuCores, Math.Max(host.CpuSockets, 1))
                },
                new FormField
                {
                    Key = "cputype", LabelKey = "HwEd_CpuType", Kind = FormFieldKind.Choice, Initial = type,
                    Choices = [("", isArm ? "Hw_Default" : Loc.T("HwEd_CpuDefault", DefaultCpuType)), .. models,
                        .. Keep(models, type)]
                },
                new FormField { Key = "vcpus", LabelKey = "HwEd_Vcpus", Advanced = true, Initial = ctx.Get("vcpus"),
                    Hint = Loc.T("HwEd_VcpusHint") },
                new FormField { Key = "cpulimit", LabelKey = "HwEd_CpuLimit", Advanced = true,
                    Initial = ctx.Get("cpulimit"), Hint = Loc.T("HwEd_CpuLimitHint") },
                new FormField { Key = "affinity", LabelKey = "HwEd_Affinity", Advanced = true,
                    Initial = ctx.Get("affinity"), Hint = Loc.T("HwEd_AffinityHint") },
                new FormField { Key = "cpuunits", LabelKey = "HwEd_CpuUnits", Advanced = true,
                    Initial = ctx.Get("cpuunits"), Hint = Loc.T("HwEd_CpuUnitsHint") },
                new FormField { Key = "numa", LabelKey = "HwEd_Numa", Kind = FormFieldKind.Bool, Advanced = true,
                    Initial = ctx.Get("numa") == "1" ? "1" : "0" },
                new FormField { Key = "flags", LabelKey = "HwEd_CpuFlags", Advanced = true, Initial = cpu.Get("flags"),
                    Hint = Loc.T("HwEd_CpuFlagsHint") }
            ],
            Validate = ValidateProcessor,
            Build = values => BuildProcessor(values, cpu, isArm)
        };
    }

    private static string Or(string value, string fallback)
    {
        return value.Length > 0 ? value : fallback;
    }

    /// <summary>목록에 없는 현재 값(사용자 지정 CPU 등)은 그대로 고를 수 있게 넣는다.</summary>
    private static IEnumerable<(string, string)> Keep(IReadOnlyList<(string Value, string Label)> list, string value)
    {
        return value.Length == 0 || list.Any(i => i.Value == value) ? [] : [(value, value)];
    }

    private static string? ValidateProcessor(IReadOnlyDictionary<string, string> values)
    {
        if (!int.TryParse(V(values, "sockets"), out var sockets) || sockets < 1
            || !int.TryParse(V(values, "cores"), out var cores) || cores is < 1 or > 256)
            return Loc.T("GuestSettings_CoresMin");
        if (!IsIntOrEmpty(V(values, "vcpus"), 1, sockets * cores)) return Loc.T("HwEd_VcpusRange", sockets * cores);
        if (V(values, "cpulimit") is { Length: > 0 } limit
            && !(double.TryParse(limit, NumberStyles.Float, CultureInfo.InvariantCulture, out var l)
                 && l is >= 0 and <= 128))
            return Loc.T("HwEd_CpuLimitRange");
        return IsIntOrEmpty(V(values, "cpuunits"), 1, 262144) ? null : Loc.T("HwEd_CpuUnitsRange");
    }

    /// <summary>x86 은 빈 종류를 x86-64-v2-AES 로 명시해 보내고, ARM 은 빈 종류면 cpu 를 지운다(웹 UI 와 같이).</summary>
    internal static IReadOnlyDictionary<string, string> BuildProcessor(IReadOnlyDictionary<string, string> values,
        PropertyString originalCpu, bool isArm)
    {
        var type = V(values, "cputype");
        var flags = V(values, "flags");
        var cpu = type.Length == 0 && isArm
            ? PropertyString.Empty
            : originalCpu.With("cputype", type.Length > 0 ? type : DefaultCpuType).With("flags", flags);
        var limit = V(values, "cpulimit");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sockets"] = V(values, "sockets"),
            ["cores"] = V(values, "cores"),
            ["cpu"] = cpu.Format("cputype"),
            ["vcpus"] = V(values, "vcpus"),
            ["cpulimit"] = limit == "0" ? string.Empty : limit,
            ["affinity"] = V(values, "affinity"),
            ["cpuunits"] = V(values, "cpuunits"),
            ["numa"] = V(values, "numa") == "1" ? "1" : "0"
        };
    }

    /// <summary>노드가 아는 CPU 모델(GET …/capabilities/qemu/cpu) — 제조사 순(AMD·Intel·QEMU·Host) 다음 이름순.</summary>
    private static async Task<IReadOnlyList<(string Value, string Label)>> CpuModelsAsync(HardwareContext ctx)
    {
        try
        {
            // arch 는 9.1+ 서버만 안다 — Core 가 낮은 서버엔 보내지 않는다
            var rows = await ctx.Api.Guests.CpuModelsAsync(ctx.Guest.Node, ctx.Get("arch"));
            return rows.Select(r => (Name: ActionHelpers.Value(r, "name"), Vendor: CpuVendor(r)))
                .Where(r => r.Name.Length > 0)
                .OrderBy(r => VendorOrder(r.Vendor)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => (r.Name, $"{r.Name.Replace("custom-", "", StringComparison.Ordinal)} — {r.Vendor}"))
                .ToList();
        }
        catch (Exception ex) when (ex is ProxmoxClient.Core.Api.ProxmoxApiException)
        {
            App.Log($"[하드웨어] CPU 모델 목록 조회 실패: {ex.Message}");
            return [("host", "host"), ("kvm64", "kvm64"), ("qemu64", "qemu64"), (DefaultCpuType, DefaultCpuType)];
        }
    }

    private static string CpuVendor(IReadOnlyDictionary<string, string> row)
    {
        if (ActionHelpers.Value(row, "name") == "host") return "Host";
        var vendor = ActionHelpers.Value(row, "vendor") switch
        {
            "AuthenticAMD" => "AMD",
            "GenuineIntel" => "Intel",
            "default" => "QEMU",
            var other => other
        };
        return ActionHelpers.Value(row, "custom") == "1" ? $"Custom ({vendor})" : vendor;
    }

    private static int VendorOrder(string vendor)
    {
        return vendor switch { "AMD" => 0, "Intel" => 1, "QEMU" => 2, "Host" => 3, _ => 4 };
    }
}
