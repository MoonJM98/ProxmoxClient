using System.Reflection;
using ProxmoxClient.UI.Views.Terminal;

// 이름이 ...Tests 인 static class 의 public static void M(Checker) 를 모두 돈다. 인자가 있으면 이름에 그 글자가 든 검사만.
var filter = args.FirstOrDefault() ?? "";
var checker = new Checker();
foreach (var type in typeof(Checker).Assembly.GetTypes()
             .Where(t => t.IsAbstract && t.IsSealed && t.Name.EndsWith("Tests", StringComparison.Ordinal))
             .OrderBy(t => t.Name))
foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
             .Where(m => m.GetParameters() is [{ ParameterType: var p }] && p == typeof(Checker)))
{
    var name = $"{type.Name}.{method.Name}";
    if (!name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

    checker.Begin(name);
    try
    {
        method.Invoke(null, [checker]);
    }
    catch (TargetInvocationException ex)
    {
        checker.Fail($"예외: {ex.InnerException}");
    }
}

Console.WriteLine($"\n검사 {checker.Total}개 · 실패 {checker.Failures}개");
return checker.Failures == 0 ? 0 : 1;

/// <summary>검사 도우미 — 터미널을 만들고, 화면 글자·응답을 보기 쉽게 꺼낸다.</summary>
public sealed class Checker
{
    private string _current = "";
    public int Total { get; private set; }
    public int Failures { get; private set; }

    public void Begin(string name) => _current = name;

    public void Fail(string message)
    {
        Total++;
        Failures++;
        Console.WriteLine($"실패 {_current}: {message}");
    }

    /// <summary>조건 확인 — 실패하면 기대·결과를 찍는다.</summary>
    public void Is(bool ok, string what, object? got = null)
    {
        Total++;
        if (ok)
        {
            Console.WriteLine($"통과 {_current}: {what}");
            return;
        }

        Failures++;
        Console.WriteLine($"실패 {_current}: {what} — 결과 {Show(got)}");
    }

    public void Equal<T>(T expected, T actual, string what)
    {
        Is(EqualityComparer<T>.Default.Equals(expected, actual), $"{what} (기대 {Show(expected)})", actual);
    }

    /// <summary>새 터미널.</summary>
    public static TerminalBuffer Term(int columns = 20, int rows = 5) => new(columns, rows);

    /// <summary>출력을 넣고 응답(있으면)을 돌려준다.</summary>
    public static string Feed(TerminalBuffer term, string text) => term.Feed(text) ?? "";

    /// <summary>보이는 줄 하나(오른쪽 공백 제거, 넓은 글자의 뒤 칸은 건너뜀).</summary>
    public static string Line(TerminalBuffer term, int row, int scrollOffset = 0)
    {
        lock (term.Gate)
            return CellText(term.GetVisibleLine(row, scrollOffset).Cells).TrimEnd();
    }

    public static string CellText(TerminalCell[] cells)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in cells)
            if (c.Width != 0) sb.Append(c.Rune == 0 ? " " : char.ConvertFromUtf32(c.Rune));
        return sb.ToString();
    }

    /// <summary>보이는 화면 전체(줄마다 \n).</summary>
    public static string Screen(TerminalBuffer term) =>
        string.Join("\n", Enumerable.Range(0, term.Rows).Select(r => Line(term, r)));

    public static string Show(object? value) => value switch
    {
        null => "(없음)",
        string s => "'" + s.Replace("\x1b", "\\e").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\x07", "\\a") + "'",
        _ => value.ToString() ?? ""
    };
}
