using ProxmoxClient.Core.Api.Versioning;
using ProxmoxClient.Presentation.Guest.Tabs;

namespace ProxmoxClient.Presentation.Shared;

/// <summary>
///     공용 코드가 만드는 화면의 설계도 — 표·글 편집·하위 탭. 각 앱이 자기 컨트롤(WPF TableTab · Avalonia TableTab 등)로
///     만든다(앱마다 ViewSpecs.Build). 생성자 매개변수는 두 앱 컨트롤의 생성자와 같다.
/// </summary>
public abstract class ViewSpec;

/// <summary>서버 목록 하나를 보여 주는 표 — 앱의 TableTab 생성자와 같다.</summary>
public sealed class TableSpec(
    Func<Task<IReadOnlyList<IReadOnlyDictionary<string, string>>>> load,
    IReadOnlyList<TableColumn> columns,
    string hintKey,
    IReadOnlyList<TableAction>? actions = null,
    bool filterable = false,
    Action<IReadOnlyDictionary<string, string>, object?>? open = null) : ViewSpec
{
    public Func<Task<IReadOnlyList<IReadOnlyDictionary<string, string>>>> Load { get; } = load;

    public IReadOnlyList<TableColumn> Columns { get; } = columns;

    public string HintKey { get; } = hintKey;

    public IReadOnlyList<TableAction>? Actions { get; } = actions;

    public bool Filterable { get; } = filterable;

    /// <summary>행을 두 번 누르면 부를 동작 — 둘째 값은 대화상자의 주인(<see cref="TableAction.Run" /> 과 같다).</summary>
    public Action<IReadOnlyDictionary<string, string>, object?>? Open { get; } = open;
}

/// <summary>글 보기·편집 — 앱의 TextEditTab 생성자와 같다(save 가 없으면 읽기 전용).</summary>
public sealed class TextSpec(
    Func<Task<(string Text, string Digest)>> load,
    Func<string, string, Task>? save,
    string hintKey) : ViewSpec
{
    public Func<Task<(string Text, string Digest)>> Load { get; } = load;

    public Func<string, string, Task>? Save { get; } = save;

    public string HintKey { get; } = hintKey;

    /// <summary>
    ///     글 칸을 위쪽에 맞춘다 — Avalonia 노드 화면이 쓴다(TextBox 기본이 가운데). WPF 는 원래 위쪽이라 보지 않는다.
    /// </summary>
    public bool TopAligned { get; init; }
}

/// <summary>탐색 창 왼쪽 목록의 한 항목(앱의 NavTab) — 이름·아이콘, 처음 고를 때 만드는 내용, 쓰는 API 기능.</summary>
public sealed record NavTabSpec(string Id, string LabelKey, string IconKey, Func<ViewSpec> Create,
    ApiFeature? Requires = null);

/// <summary>하위 탭 하나 — 이름·내용, 그리고 쓰는 API 기능(서버가 못 쓰면 탭을 뺀다).</summary>
public sealed record SubTabSpec(string LabelKey, Func<ViewSpec> Create, ApiFeature? Requires = null);

/// <summary>하위 탭 묶음 — 앱의 SubTabsView 생성자와 같다.</summary>
public sealed class SubTabsSpec : ViewSpec
{
    public SubTabsSpec(IReadOnlyList<SubTabSpec> tabs)
    {
        Tabs = tabs;
    }

    public SubTabsSpec(IReadOnlyList<(string LabelKey, Func<ViewSpec> Create)> tabs)
    {
        Tabs = tabs.Select(t => new SubTabSpec(t.LabelKey, t.Create)).ToList();
    }

    public IReadOnlyList<SubTabSpec> Tabs { get; }
}

/// <summary>
///     옵션 목록(이름 · 값, 한 줄을 눌러 고친다) — 앱의 OptionsTab 생성자(지금 값만 읽는 쪽)와 같다.
///     target 이 주어지면 서버가 모르는 옵션은 목록에 두지 않는다.
/// </summary>
public sealed class OptionsSpec(
    IReadOnlyList<GuestOption> options,
    Func<Task<IReadOnlyDictionary<string, string>>> load,
    Func<IReadOnlyDictionary<string, string>, Task> save,
    ApiFeature? target = null) : ViewSpec
{
    public IReadOnlyList<GuestOption> Options { get; } = options;

    public Func<Task<IReadOnlyDictionary<string, string>>> Load { get; } = load;

    public Func<IReadOnlyDictionary<string, string>, Task> Save { get; } = save;

    public ApiFeature? Target { get; } = target;
}

/// <summary>
///     처음 보일 때 내용을 읽어 채우는 화면(예: 서버 버전에 따라 탭이 다른 HA) — 이미 채웠으면 다시 읽지 않는다.
/// </summary>
public sealed class DeferredSpec(Func<Task<ViewSpec>> create) : ViewSpec
{
    public Func<Task<ViewSpec>> Create { get; } = create;
}

/// <summary>앱이 직접 만든 컨트롤(WPF UIElement · Avalonia Control)을 그대로 넣는다 — 공용 설계도로 그릴 수 없는 화면.</summary>
public sealed class NativeView(Func<object> create) : ViewSpec
{
    public Func<object> Create { get; } = create;
}
