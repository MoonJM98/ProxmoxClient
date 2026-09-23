using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>
///     VM 의 OS 유형(ostype) — 웹 UI 처럼 "유형 → 버전" 두 단계로 고른다.
///     목록과 표시 문구는 웹 UI(pve-manager 의 kvm_ostypes)를 따른다.
/// </summary>
internal static class OsTypes
{
    /// <summary>OS 유형 하나와 그 버전들(값, 표시 문구). 버전이 "-" 하나뿐이면 버전 선택이 없다.</summary>
    internal sealed record Family(string LabelKey, IReadOnlyList<(string Value, string Label)> Versions);

    /// <summary>서버 기본값 — 설정에 ostype 이 없으면 서버는 other 로 다룬다.</summary>
    public const string DefaultValue = "other";

    private const string NoVersion = "-";

    public static readonly IReadOnlyList<Family> Families =
    [
        new("OsType_Linux", [("l26", "6.x - 2.6 Kernel"), ("l24", "2.4 Kernel")]),
        new("OsType_Windows",
        [
            ("win11", "11/2022/2025"), ("win10", "10/2016/2019"), ("win8", "8.x/2012/2012r2"),
            ("win7", "7/2008r2"), ("w2k8", "Vista/2008"), ("wxp", "XP/2003"), ("w2k", "2000")
        ]),
        new("OsType_Solaris", [("solaris", NoVersion)]),
        new("OsType_Other", [("other", NoVersion)])
    ];

    /// <summary>서버는 받지만 웹 UI 가 더는 내놓지 않는 옛 값 — 이미 설정된 게스트의 표시에만 쓴다.</summary>
    private static readonly IReadOnlyList<(string Value, string FamilyKey, string Label)> Legacy =
    [
        ("wvista", "OsType_Windows", "Vista"),
        ("w2k3", "OsType_Windows", "2003")
    ];

    /// <summary>값이 속한 유형 — 모르는 값·빈 값은 "기타".</summary>
    public static Family FamilyOf(string? value)
    {
        var key = string.IsNullOrEmpty(value) ? DefaultValue : value;
        return Families.FirstOrDefault(f => f.Versions.Any(v => v.Value == key))
               ?? Families.FirstOrDefault(f => Legacy.Any(l => l.Value == key && l.FamilyKey == f.LabelKey))
               ?? Families[^1];
    }

    /// <summary>목록에 보일 문구 — 예: "Microsoft Windows 11/2022/2025", "기타".</summary>
    public static string Describe(string? value)
    {
        var key = string.IsNullOrEmpty(value) ? DefaultValue : value;
        var family = FamilyOf(key);
        var label = family.Versions.FirstOrDefault(v => v.Value == key).Label
                    ?? Legacy.FirstOrDefault(l => l.Value == key).Label;
        var familyName = Loc.T(family.LabelKey);

        if (label is null) return key == DefaultValue ? familyName : $"{familyName} ({key})";
        return label == NoVersion ? familyName : $"{familyName} {label}";
    }

    /// <summary>편집 창의 버전 목록 — 옛 값이 설정돼 있으면 그 값도 넣어 그대로 저장할 수 있게 한다.</summary>
    public static IReadOnlyList<(string Value, string Label)> VersionsFor(Family family, string? current)
    {
        var legacy = Legacy.Where(l => l.Value == current && l.FamilyKey == family.LabelKey)
            .Select(l => (l.Value, l.Label));
        return [.. family.Versions, .. legacy];
    }

    public static bool HasVersionChoice(Family family)
    {
        return family.Versions is not [(_, NoVersion)];
    }
}
