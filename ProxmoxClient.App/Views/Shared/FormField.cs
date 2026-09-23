namespace ProxmoxClient.App.Views.Shared;

public enum FormFieldKind
{
    Text,
    Password,
    Bool,
    Choice,

    /// <summary>여러 줄 입력(PEM 인증서 등). 파일에서 불러오기 버튼이 함께 붙는다.</summary>
    Multiline,

    /// <summary>여러 개 고르기(체크 목록). 값은 쉼표로 이어 붙인다.</summary>
    MultiChoice,

    /// <summary>구역 제목(입력 없음) — 웹 UI 편집 창의 탭·구분선처럼 칸 묶음을 나눈다. 결과에 들어가지 않는다.</summary>
    Section
}

/// <summary>입력 대화상자의 한 칸 — 서버 필드 이름과 입력 방식.</summary>
public sealed class FormField
{
    public required string Key { get; init; }

    public required string LabelKey { get; init; }

    public FormFieldKind Kind { get; init; } = FormFieldKind.Text;

    /// <summary>선택 목록(값, 표시 이름). 표시 이름이 리소스 키가 아니면 그대로 보인다.</summary>
    public IReadOnlyList<(string Value, string Label)>? Choices { get; init; }

    public bool Required { get; init; }

    /// <summary>처음 채워 둘 값. Bool 은 "1"/"0".</summary>
    public string Initial { get; init; } = string.Empty;

    /// <summary>웹 UI 의 "고급" 항목 — 창 아래 "고급" 체크박스를 켜야 보인다.</summary>
    public bool Advanced { get; init; }

    /// <summary>칸 아래 작은 안내(이미 번역된 문구). 예: "비우면 기본값(1000)".</summary>
    public string? Hint { get; init; }

    /// <summary>Multiline 칸에 "파일에서 불러오기" 버튼을 붙일지(인증서·키 입력용). 메모 같은 칸은 끈다.</summary>
    public bool CanLoadFile { get; init; } = true;

    /// <summary>입력 앞뒤 공백을 자를지(기본). SSH 키처럼 원문 그대로 보내야 하는 칸은 끈다.</summary>
    public bool Trim { get; init; } = true;
}
