namespace ProxmoxClient.UI.Views.Create;

/// <summary>
///     마법사의 OS 유형 목록 — "유형 → 버전" 두 단계(웹 UI 의 kvm_ostypes 와 같은 목록).
///     WPF 의 Guest/Tabs/OsTypes 중 마법사가 쓰는 부분만 옮겼다(그 영역은 이 작업에서 건드리지 않는다).
/// </summary>
internal static class WizardOsTypes
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

    /// <summary>값이 속한 유형 — 모르는 값·빈 값은 "기타".</summary>
    public static Family FamilyOf(string? value)
    {
        var key = string.IsNullOrEmpty(value) ? DefaultValue : value;
        return Families.FirstOrDefault(f => f.Versions.Any(v => v.Value == key)) ?? Families[^1];
    }

    public static bool HasVersionChoice(Family family)
    {
        return family.Versions is not [(_, NoVersion)];
    }
}
