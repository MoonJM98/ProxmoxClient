using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>옵션 항목을 어떤 입력으로 고치는지.</summary>
public enum OptionKind
{
    Text,
    Bool,
    Choice,

    /// <summary>VM OS 유형 — 유형·버전 두 단계 선택(<see cref="OsTypes" />).</summary>
    OsType,

    /// <summary>VM 부팅 순서 — 장치 목록에서 켜고 순서를 정한다(<see cref="BootOrder" />).</summary>
    BootOrder
}

/// <summary>설정(config) 한 줄 — 웹 UI 의 Options 목록에 해당한다.</summary>
public sealed class GuestOption
{
    public required string Key { get; init; }
    public required string LabelKey { get; init; }
    public OptionKind Kind { get; init; } = OptionKind.Text;

    /// <summary>라벨 문구의 {0} 에 넣을 값(예: "IP 설정 (net{0})" 의 장치 번호).</summary>
    public string? LabelArg { get; init; }

    /// <summary>Choice 일 때 고를 수 있는 값(값, 라벨 리소스 키 또는 원문).</summary>
    public (string Value, string Label)[]? Choices { get; init; }

    /// <summary>비워 둘 때 화면에 대신 보여 줄 설명 리소스 키.</summary>
    public string? EmptyLabelKey { get; init; }

    /// <summary>설정에 없을 때 서버가 쓰는 값 — 편집 창을 이 값으로 연다(예: 태블릿 포인터는 기본 켜짐 "1").</summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    ///     고른 값이 <see cref="DefaultValue" /> 와 같으면 값을 보내지 않고 삭제한다(웹 UI 의 deleteDefaultValue).
    ///     예: VM 의 "부팅 시 시작" 을 끄면 onboot=0 이 아니라 delete=onboot.
    /// </summary>
    public bool DeleteDefault { get; init; }

    public bool VmOnly { get; init; }
    public bool CtOnly { get; init; }

    /// <summary>여러 칸으로 나눠 고치는 항목(시작 순서·에이전트 등) — 있으면 Kind 대신 이 편집기를 쓴다.</summary>
    public OptionEditor? Editor { get; init; }

    /// <summary>만든 뒤에는 바꿀 수 없는 항목(CT 아키텍처·비특권 여부) — 보여 주기만 한다.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>설정에 따라 줄을 보일지(예: 네트워크 장치가 있는 ipconfigN 만). 없으면 늘 보인다.</summary>
    public Func<IReadOnlyDictionary<string, string>, bool>? VisibleIf { get; init; }

    /// <summary>대기 중 변경을 판단할 키(부팅 순서는 boot·bootdisk). 없으면 <see cref="Key" /> 하나.</summary>
    public IReadOnlyList<string>? PendingKeys { get; init; }

    public string Label => LabelArg is null ? Loc.T(LabelKey) : Loc.T(LabelKey, LabelArg);

    public IReadOnlyList<string> KeysForPending => PendingKeys ?? [Key];

    public bool AppliesTo(ResourceKind kind)
    {
        var isVm = kind == ResourceKind.Qemu;
        return (!VmOnly || isVm) && (!CtOnly || !isVm);
    }

    /// <summary>설정이 있을 때만 보이는 줄(훅스크립트 등 기본값 없는 항목).</summary>
    public static Func<IReadOnlyDictionary<string, string>, bool> WhenSet(string key)
    {
        return config => config.ContainsKey(key);
    }
}

/// <summary>
///     여러 칸 편집기 — 웹 UI 의 옵션 편집 창처럼 값을 칸으로 나눠 보여 주고 다시 모은다.
/// </summary>
public sealed class OptionEditor
{
    /// <summary>현재 값·설정 전체로 입력 칸을 만든다.</summary>
    public required Func<string, IReadOnlyDictionary<string, string>, IReadOnlyList<FormField>> Fields { get; init; }

    /// <summary>
    ///     입력 결과(와 원래 값) → 바꿀 설정들. 값이 빈 문자열이면 그 키를 삭제한다(서버 기본값).
    ///     DNS 처럼 한 창에서 두 키를 함께 바꾸는 편집기도 있다.
    /// </summary>
    public required Func<IReadOnlyDictionary<string, string>, string, IReadOnlyDictionary<string, string>> Build
    {
        get;
        init;
    }

    /// <summary>입력 검사 — 문제가 있으면 보여 줄 문구, 없으면 null.</summary>
    public Func<IReadOnlyDictionary<string, string>, string?>? Validate { get; init; }

    /// <summary>목록에 보일 문구(없으면 원래 값 그대로).</summary>
    public Func<string, string>? Display { get; init; }
}
