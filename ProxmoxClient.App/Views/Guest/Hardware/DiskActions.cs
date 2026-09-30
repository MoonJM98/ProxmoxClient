using System.Globalization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>
///     디스크 작업 메뉴(웹 UI HardwareView 의 Disk Action) — 저장소 이동·소유자 변경·크기 늘리기·EFI 인증서 갱신.
///     편집 창과 같은 형식(<see cref="HardwareEdit" />)으로 만들고, 보낼 곳·방식은 <see cref="DiskRequest" /> 가 정한다.
/// </summary>
internal static class DiskActions
{
    private const double MaxResizeGib = 131072;
    private static readonly string[] Buses = ["ide", "sata", "scsi", "virtio"];

    /// <summary>디스크 작업 요청 — 설정 변경이 아니라 별도 요청(move_disk·resize)으로 보낸다(작업 UPID).</summary>
    internal sealed record DiskRequest(Func<ProxmoxApiClient, Task<string>> Send);

    /// <summary>디스크 작업 창 — 입력 칸과, 입력을 요청으로 바꾸는 함수.</summary>
    internal sealed record DiskDialog(
        HardwareEdit Edit, Func<IReadOnlyDictionary<string, string>, DiskRequest> Request);

    private static string V(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
    }

    /// <summary>저장소 이동 — move_disk disk·storage[·format][·delete=1]. 원본 삭제는 기본으로 끈다(웹 UI 와 같이).</summary>
    public static async Task<DiskDialog> MoveAsync(HardwareContext ctx, string key)
    {
        var storages = await ctx.StoragesAsync(ctx.IsCt ? "rootdir" : "images");
        var edit = new HardwareEdit
        {
            Title = Loc.T("HwDisk_MoveTitle", key),
            Fields =
            [
                new FormField { Key = "storage", LabelKey = "HwEd_TargetStorage", Kind = FormFieldKind.Choice,
                    Choices = storages.Select(s => (s.Id, $"{s.Id} ({s.Type})")).ToList(), Required = true },
                .. ctx.IsCt ? Array.Empty<FormField>() : [FormatChoice()],
                new FormField { Key = "delete", LabelKey = "DeviceTable_DeleteSource", Kind = FormFieldKind.Bool,
                    Initial = "0" }
            ],
            Build = _ => new Dictionary<string, string>()
        };
        return new DiskDialog(edit, values =>
        {
            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ctx.IsCt ? "volume" : "disk"] = key, ["storage"] = V(values, "storage")
            };
            if (V(values, "format").Length > 0) form["format"] = V(values, "format");
            if (V(values, "delete") == "1") form["delete"] = "1";
            return new DiskRequest(api => api.Guests.MoveDiskAsync(ctx.Guest, form));
        });
    }

    private static FormField FormatChoice()
    {
        return new FormField
        {
            Key = "format", LabelKey = "HwEd_Format", Kind = FormFieldKind.Choice,
            Choices = [("", "HwEd_FormatKeep"), ("raw", "raw"), ("qcow2", "qcow2"), ("vmdk", "vmdk")]
        };
    }

    /// <summary>
    ///     소유자 변경 — 같은 노드의 다른 VM(템플릿 제외)으로 디스크를 넘긴다.
    ///     move_disk vmid·disk·target-vmid·target-disk(버스+번호, 미사용 디스크는 unused).
    /// </summary>
    public static async Task<DiskDialog> ReassignAsync(HardwareContext ctx, string key)
    {
        var vms = (await ctx.Api.Cluster.ResourcesAsync("vm"))
            .Where(r => ActionHelpers.Value(r, "type") == (ctx.IsCt ? "lxc" : "qemu")
                        && ActionHelpers.Value(r, "node") == ctx.Guest.Node
                        && ActionHelpers.Value(r, "template") != "1"
                        && ActionHelpers.Value(r, "vmid") != ctx.Guest.VmId.ToString(CultureInfo.InvariantCulture))
            .Select(r => (ActionHelpers.Value(r, "vmid"),
                $"{ActionHelpers.Value(r, "vmid")} ({ActionHelpers.Value(r, "name")})"))
            .OrderBy(r => int.TryParse(r.Item1, out var id) ? id : int.MaxValue)
            .ToList();
        var isUnused = key.StartsWith("unused", StringComparison.Ordinal);
        string[] buses = isUnused ? ["unused"] : ctx.IsCt ? ["mp"] : Buses;
        var edit = new HardwareEdit
        {
            Title = Loc.T("HwDisk_ReassignTitle", key),
            Fields =
            [
                new FormField { Key = "target", LabelKey = "HwDisk_TargetGuest", Kind = FormFieldKind.Choice,
                    Choices = vms, Required = true },
                new FormField { Key = "bus", LabelKey = "DeviceTable_Bus", Kind = FormFieldKind.Choice,
                    Choices = buses.Select(b => (b, b)).ToList(),
                    Initial = isUnused ? "unused" : DiskOptions.BusOf(key) },
                new FormField { Key = "id", LabelKey = "HwDisk_TargetId", Initial = "0", Required = true,
                    Hint = Loc.T("HwDisk_TargetIdHint") }
            ],
            Validate = values => int.TryParse(V(values, "id"), out var id) && id >= 0
                ? null
                : Loc.T("HwDisk_TargetIdHint"),
            Build = _ => new Dictionary<string, string>()
        };
        return new DiskDialog(edit, values => new DiskRequest(api => api.Guests.MoveDiskAsync(ctx.Guest,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vmid"] = ctx.Guest.VmId.ToString(CultureInfo.InvariantCulture), [ctx.IsCt ? "volume" : "disk"] = key,
                ["target-vmid"] = V(values, "target"),
                [ctx.IsCt ? "target-volume" : "target-disk"] = $"{V(values, "bus")}{V(values, "id")}"
            })));
    }

    /// <summary>크기 늘리기 — resize disk·size=+NG (소수 셋째 자리까지).</summary>
    public static DiskDialog Resize(HardwareContext ctx, string key)
    {
        var edit = new HardwareEdit
        {
            Title = Loc.T("DeviceTable_ResizeTitle", key),
            Fields =
            [
                new FormField { Key = "size", LabelKey = "HwDisk_SizeIncrement", Initial = "0", Required = true,
                    Hint = Loc.T("HwDisk_SizeIncrementHint") }
            ],
            Validate = values => double.TryParse(V(values, "size"), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var gib) && gib is > 0 and <= MaxResizeGib
                ? null
                : Loc.T("DeviceTable_SizeInvalid"),
            Build = _ => new Dictionary<string, string>()
        };
        return new DiskDialog(edit, values => new DiskRequest(api =>
            api.Guests.ResizeDiskAsync(ctx.Guest, key, $"+{V(values, "size")}G")));
    }

    /// <summary>EFI 디스크에 새 인증서(Microsoft 2023)를 넣을 수 있는가 — 미리 등록된 키가 있고 아직 2023k 가 아닐 때(PVE 9).</summary>
    public static bool CanEnrollCertificates(HardwareContext ctx)
    {
        var efi = PropertyString.Parse(ctx.Get("efidisk0"), "file");
        return efi.IsOn("pre-enrolled-keys") && efi.Get("ms-cert") != "2023k";
    }

    /// <summary>EFI 새 인증서 등록 — efidisk0 에 ms-cert=2023k 를 넣는다(웹 UI 처럼 키 이름순으로 다시 쓴다).</summary>
    public static IReadOnlyDictionary<string, string> EnrollCertificates(HardwareContext ctx)
    {
        var efi = PropertyString.Parse(ctx.Get("efidisk0"), "file").With("ms-cert", "2023k");
        var rest = efi.Items.Where(kv => kv.Key != "file").OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["efidisk0"] = string.Join(',', new[] { efi.Get("file") }.Concat(rest))
        };
    }
}
