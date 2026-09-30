using System.Globalization;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>
///     CT 마운트 포인트(루트 디스크 포함) — 웹 UI lxc/MPEdit.js.
///     저장: file, 기존 키 순서대로, 그다음 mp·mountoptions·backup·quota·ro·acl·replicate·keepattrs.
///     꺼진 체크는 키를 뺀다(acl 만 0 을 보낸다). 예: mp0=local-lvm:8,mp=/mnt/data,backup=1
/// </summary>
internal static partial class CtEditors
{
    /// <summary>새 마운트 포인트 — 번호·저장소·크기(기본 8 GiB)·경로.</summary>
    public static async Task<HardwareEdit> CreateMountPointAsync(HardwareContext ctx)
    {
        var storages = await ctx.StoragesAsync("rootdir");
        var free = FreeMountPoints(ctx);
        return new HardwareEdit
        {
            Title = Loc.T("CtAdd_MountPoint"),
            Fields =
            [
                new FormField { Key = "mpid", LabelKey = "Ct_MountPointId", Kind = FormFieldKind.Choice, Choices = free,
                    Initial = free.FirstOrDefault().Value ?? "", Required = true },
                new FormField { Key = "storage", LabelKey = "Table_Storage", Kind = FormFieldKind.Choice,
                    Required = true,
                    Choices = storages.Select(s => (s.Id, $"{s.Id} ({s.Type})")).ToList() },
                new FormField { Key = "size", LabelKey = "HwEd_DiskSizeGib", Initial = DefaultMpSizeGib,
                    Required = true },
                .. MountFields(PropertyString.Empty, isRoot: false, isCreate: true, ctx)
            ],
            Validate = values => ValidSize(V(values, "size")) ? ValidatePath(values) : Loc.T("HwEd_DiskSizeRange"),
            Build = values =>
            {
                var file = PropertyString.Parse($"{V(values, "storage")}:{V(values, "size")}", "file");
                return One(V(values, "mpid"), ApplyMount(file, values, isRoot: false).Format("file"));
            }
        };
    }

    /// <summary>기존 마운트 포인트·루트 디스크 편집 — 볼륨·크기 등 원래 키는 그대로 둔다.</summary>
    public static HardwareEdit EditMountPoint(HardwareContext ctx, string key)
    {
        var isRoot = key == "rootfs";
        var mount = PropertyString.Parse(ctx.Get(key), "file");
        return new HardwareEdit
        {
            // 창 제목에 볼륨을 보인다(웹 UI 의 읽기 전용 "디스크 이미지" 칸)
            Title = $"{(isRoot ? Loc.T("Ct_RootDisk") : Loc.T("Ct_MountPoint", key))} — {mount.Get("file")}",
            Fields = MountFields(mount, isRoot, isCreate: false, ctx),
            Validate = values => isRoot ? null : ValidatePath(values),
            Build = values => One(key, ApplyMount(mount, values, isRoot).Format("file"))
        };
    }

    /// <summary>미사용 디스크를 마운트 포인트로 붙인다(서버가 unusedN 을 정리한다 — 지우지 않는다).</summary>
    public static HardwareEdit AttachUnused(HardwareContext ctx, string unusedKey)
    {
        var volume = ctx.Get(unusedKey);
        var free = FreeMountPoints(ctx);
        return new HardwareEdit
        {
            Title = Loc.T("HwEd_AttachUnused", volume),
            Fields =
            [
                new FormField { Key = "mpid", LabelKey = "Ct_MountPointId", Kind = FormFieldKind.Choice, Choices = free,
                    Initial = free.FirstOrDefault().Value ?? "", Required = true },
                .. MountFields(PropertyString.Empty, isRoot: false, isCreate: true, ctx)
            ],
            Validate = ValidatePath,
            Build = values => One(V(values, "mpid"),
                ApplyMount(PropertyString.Parse(volume, "file"), values, isRoot: false).Format("file"))
        };
    }

    private static IReadOnlyList<(string Value, string Label)> FreeMountPoints(HardwareContext ctx)
    {
        return Enumerable.Range(0, MaxMountPoints).Select(i => $"mp{i}")
            .Where(k => !ctx.Config.Current.ContainsKey(k) && !ctx.Effective.ContainsKey(k))
            .Select(k => (k, k)).ToList();
    }

    /// <summary>
    ///     마운트 옵션 칸 — 루트 디스크는 경로·백업·읽기 전용·속성 유지를 숨기고 nodev·noexec 를 뺀다.
    ///     쿼터는 비특권 CT 에서 쓸 수 없다(웹 UI 는 칸을 끈다 — 여기서는 안내).
    /// </summary>
    private static IReadOnlyList<FormField> MountFields(PropertyString mount, bool isRoot, bool isCreate,
        HardwareContext ctx)
    {
        var options = isRoot ? MountOptions.Where(o => o.Value is not ("nodev" or "noexec")).ToArray() : MountOptions;
        var fields = new List<FormField>();
        if (!isRoot)
        {
            fields.Add(new FormField
            {
                Key = "mp", LabelKey = "Ct_MountPath", Initial = mount.Get("mp"), Required = true, Hint = "/some/path"
            });
            fields.Add(Check("backup", "HwEd_Backup", isCreate || mount.IsOn("backup")));
        }

        var unprivileged = ctx.Get("unprivileged") == "1";
        fields.Add(Check("quota", "Ct_Quota", mount.IsOn("quota"),
            unprivileged ? Loc.T("Ct_QuotaPrivilegedOnly") : null));
        if (!isRoot) fields.Add(Check("ro", "HwEd_ReadOnly", mount.IsOn("ro"), advanced: true));
        fields.Add(new FormField { Key = "mountoptions", LabelKey = "Ct_MountOptions", Kind = FormFieldKind.MultiChoice,
            Choices = options, Initial = mount.Get("mountoptions").Replace(';', ','), Advanced = true });
        fields.Add(new FormField { Key = "acl", LabelKey = "Ct_Acl", Kind = FormFieldKind.Choice, Advanced = true,
            Initial = mount.Get("acl"),
            Choices = [("", "Hw_Default"), ("1", "Common_Enabled"), ("0", "Common_Disabled")] });
        fields.Add(Check("skipreplication", "HwEd_SkipReplication", mount.Get("replicate") == "0", advanced: true));
        if (!isRoot)
            fields.Add(Check("keepattrs", "Ct_KeepAttrs", mount.IsOn("keepattrs"), Loc.T("Ct_KeepAttrsHint"),
                advanced: true));
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

    /// <summary>입력을 마운트 문자열에 반영한다 — 꺼진 체크는 키를 뺀다(acl 은 0 을 그대로 보낸다).</summary>
    internal static PropertyString ApplyMount(PropertyString mount, IReadOnlyDictionary<string, string> values,
        bool isRoot)
    {
        string? On(string key) => V(values, key) == "1" ? "1" : null;
        var result = mount;
        if (!isRoot) result = result.With("mp", V(values, "mp"));
        result = result.With("mountoptions", V(values, "mountoptions").Replace(',', ';'));
        if (!isRoot) result = result.With("backup", On("backup"));
        result = result.With("quota", On("quota"));
        if (!isRoot) result = result.With("ro", On("ro"));
        result = result.With("acl", V(values, "acl"))
            .With("replicate", V(values, "skipreplication") == "1" ? "0" : null);
        if (!isRoot) result = result.With("keepattrs", On("keepattrs"));
        return result;
    }

    private static string? ValidatePath(IReadOnlyDictionary<string, string> values)
    {
        return V(values, "mp").StartsWith('/') ? null : Loc.T("Ct_MountPathInvalid");
    }

    private static bool ValidSize(string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gib)
               && gib is >= 0.001 and <= 131072;
    }

    private static IReadOnlyDictionary<string, string> One(string key, string value)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };
    }
}
