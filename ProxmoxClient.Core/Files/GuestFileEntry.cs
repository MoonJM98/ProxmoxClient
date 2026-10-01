namespace ProxmoxClient.Core.Files;

/// <summary>게스트 파일 종류.</summary>
public enum GuestFileKind
{
    Directory,
    File,
    Link,
    Other
}

/// <summary>
///     게스트(CT·VM) 폴더 안의 항목 하나.
/// </summary>
/// <param name="Name">이름(경로 없이).</param>
/// <param name="Size">바이트 크기(폴더는 의미 없음).</param>
/// <param name="Modified">수정 시각(UTC, 모르면 null).</param>
/// <param name="Mode">권한(예: "755" 또는 Windows 속성), 모르면 빈 문자열.</param>
/// <param name="Owner">소유자 이름(모르면 번호나 빈 문자열).</param>
/// <param name="Group">그룹 이름.</param>
/// <param name="LinkTarget">심볼릭 링크가 가리키는 곳(링크일 때만).</param>
/// <param name="LinkToDirectory">폴더를 가리키는 링크(심볼릭 링크·정션) — 폴더처럼 연다.</param>
public sealed record GuestFileEntry(
    string Name,
    GuestFileKind Kind,
    long Size,
    DateTime? Modified,
    string Mode,
    string Owner,
    string Group,
    string? LinkTarget = null,
    bool LinkToDirectory = false)
{
    /// <summary>진짜 폴더 — 지우기·받기처럼 안으로 들어가는 작업은 이것만(링크는 링크 자체로 다룬다).</summary>
    public bool IsDirectory => Kind == GuestFileKind.Directory;

    /// <summary>열면 폴더로 들어가는 항목(폴더, 폴더를 가리키는 링크) — 탐색·정렬·아이콘용.</summary>
    public bool OpensAsFolder => IsDirectory || LinkToDirectory;

    /// <summary>
    ///     숨김·시스템 항목 — 이름이 '.' 으로 시작하거나(리눅스), Windows 속성(PowerShell Mode 의 h·s)이 있을 때.
    ///     리눅스 Mode 는 8진 숫자라 글자와 겹치지 않는다.
    /// </summary>
    public bool IsHidden => Name.StartsWith('.') || Mode.Contains('h') || Mode.Contains('s');
}
