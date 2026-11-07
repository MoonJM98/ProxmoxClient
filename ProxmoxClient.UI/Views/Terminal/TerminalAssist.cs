using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>
///     휴대폰 터미널 입력 도우미 — 화면에서 지금 입력 중인 명령 줄을 읽고, bash 가 Tab 두 번에 찍는 후보 목록을 골라낸다.
///     셸에 아무것도 따로 보내지 않고 화면 내용만 읽는다(사용자가 친 Tab 외에 숨은 명령 없음).
/// </summary>
public static partial class TerminalAssist
{
    /// <summary>
    ///     커서가 있는 논리 줄(자동 줄바꿈으로 이어진 줄 포함)의 글자 — 프롬프트 포함, 줄 끝 공백 제외.
    ///     (row = 그 논리 줄이 시작하는 절대 줄 번호)
    /// </summary>
    public static (string Text, long Row) CursorLine(TerminalBuffer buffer)
    {
        lock (buffer.Gate)
        {
            var cursorRow = buffer.AbsoluteRow(buffer.CursorY, 0);
            var start = cursorRow;
            // 위쪽으로 자동 줄바꿈으로 이어진 줄을 따라 올라간다
            while (buffer.GetAbsoluteLine(start - 1) is { Wrapped: true }) start--;

            var sb = new StringBuilder();
            for (var r = start; r <= cursorRow; r++)
                if (buffer.GetAbsoluteLine(r) is { } line)
                    foreach (var cell in line.Cells)
                        if (cell.Width != 0)
                            sb.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
            return (sb.ToString().TrimEnd(), start);
        }
    }

    /// <summary>커서 바로 앞 칸이 빈칸인지 — bash 가 낱말을 끝까지 완성하면 뒤에 공백을 붙인다(줄 글자는 끝 공백을 잘라 보이지 않는다).</summary>
    public static bool CursorAfterSpace(TerminalBuffer buffer)
    {
        lock (buffer.Gate)
        {
            var x = buffer.CursorX;
            if (x == 0 || buffer.GetAbsoluteLine(buffer.AbsoluteRow(buffer.CursorY, 0)) is not { } line || x > line.Cells.Length) return false;

            var rune = line.Cells[x - 1].Rune;
            return rune is 0 or ' ';
        }
    }

    /// <summary>
    ///     프롬프트 뒤의 입력(예: "root@ct:~# ls -la" → "ls -la"). 프롬프트를 못 찾으면 null —
    ///     편집기·대화형 프로그램 안이거나 프롬프트 모양이 달라 명령으로 보지 않는다.
    /// </summary>
    public static string? CommandFrom(string line)
    {
        var match = PromptRegex().Match(line);
        return match.Success ? line[match.Length..].Trim() : null;
    }

    /// <summary>bash 류 프롬프트 — "user@host:path# " · "[user@host ~]$ " 처럼 "# "·"$ "·"% "·"> " 로 끝나는 앞부분(가장 짧게).</summary>
    [GeneratedRegex(@"^[^\n]*?[#$%>] ")]
    private static partial Regex PromptRegex();

    /// <summary>입력 중인 마지막 낱말(공백 뒤) — 후보를 고르면 이 낱말을 이어서 완성한다.</summary>
    public static string LastWord(string command)
    {
        var i = command.LastIndexOf(' ');
        return i < 0 ? command : command[(i + 1)..];
    }

    /// <summary>
    ///     bash 후보 목록 읽기 — Tab 전 입력 줄(promptRow)과 Tab 뒤 다시 그려진 같은 입력 줄 사이의 줄들이 후보다
    ///     (bash 는 후보를 칸 맞춰 여러 열로 찍고 프롬프트와 입력을 다시 그린다). 후보 줄이 없으면 빈 목록.
    /// </summary>
    public static IReadOnlyList<string> CompletionsBetween(TerminalBuffer buffer, long promptRow, string promptLine)
    {
        var (now, nowRow) = CursorLine(buffer);
        if (nowRow <= promptRow + 1 || now != promptLine) return [];

        var items = new List<string>();
        lock (buffer.Gate)
        {
            for (var r = promptRow + 1; r < nowRow; r++)
            {
                if (buffer.GetAbsoluteLine(r) is not { } line) continue;

                var sb = new StringBuilder();
                foreach (var cell in line.Cells)
                    if (cell.Width != 0)
                        sb.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
                // 열은 공백 두 칸 이상으로 나뉜다(이름 안의 공백 하나는 살린다)
                foreach (var part in ColumnGap().Split(sb.ToString().Trim()))
                    if (part.Length > 0 && !items.Contains(part))
                        items.Add(part);
            }
        }

        return items;
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex ColumnGap();

    /// <summary>
    ///     후보를 골랐을 때 보낼 글자 — 지금 낱말(word)에 이어 붙일 나머지. 폴더(끝이 /)면 공백 없이, 아니면 공백 하나.
    ///     bash 는 경로의 마지막 부분만 찍으므로 낱말의 마지막 '/' 뒤와 비교한다.
    /// </summary>
    public static string Remainder(string word, string candidate)
    {
        var tail = word[(word.LastIndexOf('/') + 1)..];
        var rest = candidate.StartsWith(tail, StringComparison.Ordinal) ? candidate[tail.Length..] : candidate;
        return candidate.EndsWith('/') ? rest : rest + " ";
    }
}

/// <summary>
///     최근 명령 기록 — 서버·대상(노드 셸, CT 콘솔)마다 따로, 이 기기에만 저장한다(앱 데이터 terminal-history.json).
///     Enter 를 누른 순간 화면의 입력 줄에서 읽어 Tab 완성·방향키 편집 결과까지 그대로 남는다.
/// </summary>
public sealed class TerminalHistory(string key, string? path = null)
{
    private const int Limit = 200;
    private static readonly object FileLock = new();
    private readonly string _path = path ?? Core.Settings.JsonSettingsStore<object>.InAppData("terminal-history.json");

    /// <summary>최근 것부터.</summary>
    public IReadOnlyList<string> Items
    {
        get
        {
            lock (FileLock) return Load().TryGetValue(key, out var list) ? list : [];
        }
    }

    public void Add(string command)
    {
        command = command.Trim();
        if (command.Length == 0 || command.Length > 2000) return;

        lock (FileLock)
        {
            var all = Load();
            var list = all.TryGetValue(key, out var existing) ? existing : [];
            list.Remove(command);
            list.Insert(0, command);
            if (list.Count > Limit) list.RemoveRange(Limit, list.Count - Limit);
            all[key] = list;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(all, UiJsonContext.Default.DictionaryStringListString));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 기록 저장 실패는 조용히 — 터미널 사용을 막지 않는다
            }
        }
    }

    private Dictionary<string, List<string>> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), UiJsonContext.Default.DictionaryStringListString) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
