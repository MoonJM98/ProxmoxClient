using System.Text;

namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>마우스 보고 방식(DEC 1000/1002/1003) — 끄면 마우스는 선택·스크롤에 쓴다.</summary>
public enum MouseTracking
{
    None,
    Click,
    Drag,
    Any
}

/// <summary>
///     VT/xterm 에뮬레이터의 화면 상태 — 서버 출력(문자)을 해석해 칸 격자·스크롤백·커서를 갱신한다.
///     pve-xtermjs(xterm.js) 가 하는 일 중 셸·편집기(vim·mc·htop·apt 진행 막대)에 필요한 부분을 맡는다:
///     커서 이동·지우기·삽입/삭제, 스크롤 영역, 대체 화면(1049), 색(16·256·24비트), 넓은 글자(한글),
///     DEC 선 그리기 문자, 자동 줄바꿈, 탭, 상태 질의(DSR·DA) 응답, 괄호 붙여넣기·커서 키 모드·마우스 모드.
///     <para>
///         스레드: <see cref="Feed" /> 는 수신 스레드에서, 그리기는 UI 스레드에서 부른다 — 둘 다 <see cref="Gate" /> 를 잡는다.
///     </para>
/// </summary>
public sealed class TerminalBuffer
{
    public const int DefaultScrollback = 3000;
    private const int MaxParams = 32;

    /// <summary>DEC 특수 그래픽(ESC ( 0) — 0x5F~0x7E 를 선 그리기 문자로.</summary>
    private static readonly string DecGraphics =
        " ◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    public readonly object Gate = new();
    private readonly List<Line> _scrollback = [];
    private readonly int _scrollbackLimit;
    private readonly int[] _params = new int[MaxParams];
    private readonly bool[] _subParam = new bool[MaxParams];
    private readonly StringBuilder _osc = new();
    private readonly StringBuilder _responses = new();

    private Line[] _main;
    private Line[] _alt;
    private bool[] _tabs;

    private State _state;
    private int _paramCount;
    private bool _paramStarted;
    private char _prefix;
    private char _intermediate;
    private char _pendingHigh;
    private int _charsetSlot;

    private int _x, _y;
    private bool _pendingWrap;
    private CellFlags _flags;
    private uint _fg, _bg;
    private int _top, _bottom;
    private bool _originMode;
    private bool _autoWrap = true;
    private bool _insertMode;
    private bool _newLineMode;
    private readonly bool[] _g = new bool[2]; // G0/G1 가 DEC 그래픽인가
    private int _gl; // 0 = G0, 1 = G1(SO)
    private int _lastRune = ' ';
    private SavedCursor _savedMain, _savedAlt;

    public TerminalBuffer(int columns, int rows, int scrollback = DefaultScrollback)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        _scrollbackLimit = scrollback;
        _main = NewScreen(Columns, Rows);
        _alt = NewScreen(Columns, Rows);
        _tabs = NewTabs(Columns);
        _bottom = Rows - 1;
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    public int CursorX => Math.Min(_x, Columns - 1);
    public int CursorY => _y;
    public bool CursorVisible { get; private set; } = true;

    /// <summary>DECSCUSR — 0·1·2 블록, 3·4 밑줄, 5·6 세로 막대.</summary>
    public int CursorStyle { get; private set; }

    public bool IsAlternateScreen { get; private set; }
    public bool ApplicationCursorKeys { get; private set; }
    public bool ApplicationKeypad { get; private set; }
    public bool BracketedPaste { get; private set; }
    public MouseTracking MouseTracking { get; private set; }
    public bool SgrMouse { get; private set; }

    public int ScrollbackCount => _scrollback.Count;

    /// <summary>스크롤백 한도를 넘어 버린 줄 수 — 선택 영역을 줄 번호로 붙잡아 둘 때 쓴다.</summary>
    public long DroppedLines { get; private set; }

    /// <summary>스크롤백에 들어간 줄 누계 — 위로 올려 보던 화면이 새 출력에 밀리지 않게 보정할 때 쓴다.</summary>
    public long ScrolledLines { get; private set; }

    /// <summary>변경할 때마다 늘어난다 — 그리는 쪽이 같은 상태를 다시 그리지 않게.</summary>
    public long Version { get; private set; }

    /// <summary>창 제목(OSC 0·2).</summary>
    public string? Title { get; private set; }

    private Line[] Screen => IsAlternateScreen ? _alt : _main;

    /// <summary>
    ///     서버 출력을 해석한다. 응답(DSR·DA)이 생기면 돌려준다(보낼 것이 없으면 null) — 호출한 쪽이 서버로 보낸다.
    /// </summary>
    public string? Feed(ReadOnlySpan<char> text)
    {
        lock (Gate)
        {
            foreach (var c in text) Process(c);
            Version++;
            if (_responses.Length == 0) return null;

            var reply = _responses.ToString();
            _responses.Clear();
            return reply;
        }
    }

    /// <summary>
    ///     화면 크기를 바꾼다. 줄이 줄면 커서가 보이도록 위 줄을 스크롤백으로 밀고, 늘면 스크롤백에서 다시 가져온다(xterm 과 같다).
    /// </summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        lock (Gate)
        {
            if (columns == Columns && rows == Rows) return;

            _main = ResizeScreen(_main, columns, rows, true);
            _alt = ResizeScreen(_alt, columns, rows, false);
            Columns = columns;
            Rows = rows;
            _tabs = NewTabs(columns);
            _top = 0;
            _bottom = rows - 1;
            _x = Math.Min(_x, columns - 1);
            _y = Math.Clamp(_y, 0, rows - 1);
            _pendingWrap = false;
            Version++;
        }
    }

    private Line[] ResizeScreen(Line[] screen, int columns, int rows, bool main)
    {
        var lines = new List<Line>(screen);
        var cursorY = main == !IsAlternateScreen ? _y : -1;

        if (rows < lines.Count)
        {
            // 커서 아래 빈 줄부터 버리고, 그래도 많으면 위 줄을 스크롤백으로
            var excess = lines.Count - rows;
            for (var i = lines.Count - 1; i > cursorY && excess > 0; i--, excess--)
                if (lines[i].IsBlank) lines.RemoveAt(i);
                else break;

            while (lines.Count > rows)
            {
                if (main) PushScrollback(lines[0]);
                lines.RemoveAt(0);
                if (cursorY >= 0) cursorY--;
            }
        }
        else if (rows > lines.Count)
        {
            var add = rows - lines.Count;
            if (main)
                // 스크롤백에서 되가져온다(커서도 그만큼 내려간다)
                while (add > 0 && _scrollback.Count > 0)
                {
                    lines.Insert(0, _scrollback[^1]);
                    _scrollback.RemoveAt(_scrollback.Count - 1);
                    ScrolledLines--;
                    if (cursorY >= 0) cursorY++;
                    add--;
                }

            while (add-- > 0) lines.Add(new Line(columns, TerminalColor.Default));
        }

        foreach (var line in lines) line.Resize(columns);
        if (cursorY >= 0) _y = Math.Max(0, cursorY);
        return [.. lines];
    }

    /// <summary>보이는 줄 — <paramref name="scrollOffset" /> 만큼 위(스크롤백)로 올려 본다.</summary>
    public Line GetVisibleLine(int row, int scrollOffset)
    {
        var index = _scrollback.Count - scrollOffset + row;
        return index < _scrollback.Count ? _scrollback[Math.Max(0, index)] : Screen[index - _scrollback.Count];
    }

    /// <summary>절대 줄 번호(버린 줄 포함) → 줄. 범위를 벗어나면 null.</summary>
    public Line? GetAbsoluteLine(long absolute)
    {
        var index = absolute - DroppedLines;
        if (index < 0) return null;
        if (index < _scrollback.Count) return _scrollback[(int)index];

        index -= _scrollback.Count;
        return index < Rows ? Screen[index] : null;
    }

    /// <summary>보이는 줄의 절대 줄 번호.</summary>
    public long AbsoluteRow(int row, int scrollOffset)
    {
        return DroppedLines + _scrollback.Count - scrollOffset + row;
    }

    /// <summary>
    ///     두 위치 사이 글자(끝 포함). 자동 줄바꿈으로 이어진 줄은 붙이고, 줄 끝 공백은 지운다.
    /// </summary>
    public string GetText(long startRow, int startCol, long endRow, int endCol)
    {
        if (startRow > endRow || startRow == endRow && startCol > endCol)
            (startRow, startCol, endRow, endCol) = (endRow, endCol, startRow, startCol);

        var sb = new StringBuilder();
        lock (Gate)
        {
            for (var r = startRow; r <= endRow; r++)
            {
                if (GetAbsoluteLine(r) is not { } line) continue;

                var from = r == startRow ? startCol : 0;
                var to = r == endRow ? endCol : line.Cells.Length - 1;
                var text = new StringBuilder();
                for (var c = Math.Max(0, from); c <= to && c < line.Cells.Length; c++)
                {
                    var cell = line.Cells[c];
                    if (cell.Width == 0) continue;

                    text.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
                }

                var s = text.ToString();
                if (!line.Wrapped || r == endRow) s = s.TrimEnd(' ');
                sb.Append(s);
                if (r != endRow && !line.Wrapped) sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------ 해석기

    private enum State
    {
        Ground,
        Escape,
        Charset,
        Csi,
        Osc,
        OscEscape,
        Ignore, // DCS·APC·PM — ST 까지 버린다
        IgnoreEscape,
        Hash,
        SkipOne
    }

    private void Process(char c)
    {
        // 서러게이트 쌍 → 코드 포인트
        if (char.IsHighSurrogate(c))
        {
            _pendingHigh = c;
            return;
        }

        int cp = c;
        if (char.IsLowSurrogate(c))
        {
            if (_pendingHigh == 0) return;

            cp = char.ConvertToUtf32(_pendingHigh, c);
            _pendingHigh = '\0';
        }

        if (_state is State.Osc or State.OscEscape or State.Ignore or State.IgnoreEscape)
        {
            String(cp);
            return;
        }

        if (cp < 0x20 || cp == 0x7F)
        {
            Control(cp);
            return;
        }

        switch (_state)
        {
            case State.Ground:
                Print(cp);
                break;
            case State.Escape:
                Escape(cp);
                break;
            case State.Charset:
                _g[_charsetSlot] = cp == '0';
                _state = State.Ground;
                break;
            case State.Hash:
                if (cp == '8') Alignment();
                _state = State.Ground;
                break;
            case State.SkipOne:
                _state = State.Ground;
                break;
            case State.Csi:
                Csi(cp);
                break;
        }
    }

    private void Control(int c)
    {
        switch (c)
        {
            case 0x07: // BEL
                break;
            case 0x08:
                if (_x > 0) _x = Math.Min(_x, Columns - 1) - 1;
                _pendingWrap = false;
                break;
            case 0x09:
                Tab(1);
                break;
            case 0x0A:
            case 0x0B:
            case 0x0C:
                LineFeed();
                if (_newLineMode) _x = 0;
                break;
            case 0x0D:
                _x = 0;
                _pendingWrap = false;
                break;
            case 0x0E:
                _gl = 1;
                break;
            case 0x0F:
                _gl = 0;
                break;
            case 0x18:
            case 0x1A:
                _state = State.Ground;
                break;
            case 0x1B:
                _state = State.Escape;
                _intermediate = '\0';
                break;
        }
    }

    private void Escape(int c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[':
                _state = State.Csi;
                _paramCount = 0;
                _paramStarted = false;
                _prefix = '\0';
                _intermediate = '\0';
                Array.Fill(_subParam, false);
                break;
            case ']':
                _state = State.Osc;
                _osc.Clear();
                break;
            case 'P' or '_' or '^' or 'X':
                _state = State.Ignore;
                break;
            case '(' or ')' or '*' or '+':
                _charsetSlot = c is '(' or '*' ? 0 : 1;
                _state = State.Charset;
                break;
            case '#':
                _state = State.Hash;
                break;
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                LineFeed();
                _x = 0;
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'H':
                _tabs[Math.Min(_x, Columns - 1)] = true;
                break;
            case 'c':
                FullReset();
                break;
            case '=':
                ApplicationKeypad = true;
                break;
            case '>':
                ApplicationKeypad = false;
                break;
            case ' ' or '%':
                _state = State.SkipOne; // ESC SP F, ESC % G 등 — 다음 글자 하나를 버린다
                break;
        }
    }

    private void String(int c)
    {
        switch (_state)
        {
            case State.Osc:
                if (c == 0x07) EndOsc();
                else if (c == 0x1B) _state = State.OscEscape;
                else if (_osc.Length < 4096) _osc.Append(char.ConvertFromUtf32(c));
                break;
            case State.OscEscape:
                if (c == '\\') EndOsc();
                else _state = State.Osc;
                break;
            case State.Ignore:
                if (c == 0x1B) _state = State.IgnoreEscape;
                else if (c == 0x07 || c == 0x18 || c == 0x1A) _state = State.Ground;
                break;
            case State.IgnoreEscape:
                _state = c == '\\' ? State.Ground : State.Ignore;
                break;
        }
    }

    private void EndOsc()
    {
        _state = State.Ground;
        var text = _osc.ToString();
        var sep = text.IndexOf(';');
        if (sep <= 0) return;

        if (text[..sep] is "0" or "2") Title = text[(sep + 1)..];
    }

    private void Csi(int c)
    {
        if (c is >= '0' and <= '9')
        {
            if (!_paramStarted)
            {
                _paramStarted = true;
                if (_paramCount < MaxParams) _params[_paramCount++] = 0;
            }

            if (_paramCount > 0)
                _params[_paramCount - 1] = Math.Min(_params[_paramCount - 1] * 10 + (c - '0'), 99999);
            return;
        }

        if (c is ';' or ':')
        {
            if (!_paramStarted && _paramCount < MaxParams) _params[_paramCount++] = -1; // 빈 값 = 기본값
            if (c == ':' && _paramCount < MaxParams) _subParam[_paramCount] = true; // 다음 값이 앞 값의 하위 값
            _paramStarted = false;
            return;
        }

        if (c is '?' or '>' or '<' or '=')
        {
            _prefix = (char)c;
            return;
        }

        if (c is >= 0x20 and <= 0x2F)
        {
            _intermediate = (char)c;
            return;
        }

        if (c is < 0x40 or > 0x7E) return; // 잘못된 바이트 — 무시

        _state = State.Ground;
        Dispatch((char)c);
    }

    private int P(int index, int fallback)
    {
        return index < _paramCount && _params[index] > 0 ? _params[index] : fallback;
    }

    private int P0(int index)
    {
        return index < _paramCount && _params[index] >= 0 ? _params[index] : 0;
    }

    private void Dispatch(char final)
    {
        if (_intermediate == ' ' && final == 'q')
        {
            CursorStyle = P0(0);
            return;
        }

        if (_intermediate == '!' && final == 'p')
        {
            SoftReset();
            return;
        }

        if (_intermediate != '\0') return;

        switch (final)
        {
            case '@':
                InsertChars(P(0, 1));
                break;
            case 'A':
                MoveTo(_x, Math.Max(_y - P(0, 1), _y >= _top ? _top : 0));
                break;
            case 'B':
            case 'e':
                MoveTo(_x, Math.Min(_y + P(0, 1), _y <= _bottom ? _bottom : Rows - 1));
                break;
            case 'C':
            case 'a':
                MoveTo(Math.Min(_x, Columns - 1) + P(0, 1), _y);
                break;
            case 'D':
                MoveTo(Math.Min(_x, Columns - 1) - P(0, 1), _y);
                break;
            case 'E':
                MoveTo(0, Math.Min(_y + P(0, 1), _bottom));
                break;
            case 'F':
                MoveTo(0, Math.Max(_y - P(0, 1), _top));
                break;
            case 'G':
            case '`':
                MoveTo(P(0, 1) - 1, _y);
                break;
            case 'H':
            case 'f':
                MoveAbsolute(P(1, 1) - 1, P(0, 1) - 1);
                break;
            case 'I':
                Tab(P(0, 1));
                break;
            case 'J':
                EraseDisplay(P0(0));
                break;
            case 'K':
                EraseLine(P0(0));
                break;
            case 'L':
                InsertLines(P(0, 1));
                break;
            case 'M':
                DeleteLines(P(0, 1));
                break;
            case 'P':
                DeleteChars(P(0, 1));
                break;
            case 'S':
                if (_prefix == '\0') ScrollUp(P(0, 1));
                break;
            case 'T':
                if (_prefix == '\0') ScrollDown(P(0, 1));
                break;
            case 'X':
                EraseChars(P(0, 1));
                break;
            case 'Z':
                BackTab(P(0, 1));
                break;
            case 'b':
                for (var i = Math.Min(P(0, 1), Columns * Rows); i > 0; i--) Print(_lastRune);
                break;
            case 'c':
                if (_prefix == '>') _responses.Append("\x1b[>0;276;0c");
                else if (_prefix == '\0') _responses.Append("\x1b[?1;2c");
                break;
            case 'd':
                MoveAbsolute(_x, P(0, 1) - 1);
                break;
            case 'g':
                if (P0(0) == 0) _tabs[Math.Min(_x, Columns - 1)] = false;
                else if (P0(0) == 3) Array.Fill(_tabs, false);
                break;
            case 'h':
                SetModes(true);
                break;
            case 'l':
                SetModes(false);
                break;
            case 'm':
                if (_prefix == '\0') Sgr();
                break;
            case 'n':
                StatusReport();
                break;
            case 'r':
                if (_prefix == '\0') SetScrollRegion(P(0, 1) - 1, P(1, Rows) - 1);
                break;
            case 's':
                if (_prefix == '\0') SaveCursor();
                break;
            case 'u':
                if (_prefix == '\0') RestoreCursor();
                break;
        }
    }

    // ------------------------------------------------------------ 글자 쓰기

    private void Print(int cp)
    {
        if (_g[_gl] && cp is >= 0x5F and <= 0x7E) cp = DecGraphics[cp - 0x5F];

        var width = CharWidth.Of(cp);
        if (width == 0) return; // 결합 문자는 앞 글자에 붙지 않고 버린다(칸 수만 맞춘다)

        _lastRune = cp;
        var screen = Screen;
        if (_pendingWrap)
        {
            if (_autoWrap)
            {
                screen[_y].Wrapped = true;
                LineFeed();
                _x = 0;
            }

            _pendingWrap = false;
        }

        if (width == 2 && _x >= Columns - 1)
        {
            if (_autoWrap && Columns > 1)
            {
                ClearCell(screen[_y], Columns - 1);
                screen[_y].Wrapped = true;
                LineFeed();
                _x = 0;
            }
            else
            {
                _x = Math.Max(0, Columns - 2);
            }
        }

        var row = Screen[_y];
        if (_insertMode) ShiftRight(row, _x, width);

        ClearCell(row, _x);
        if (width == 2) ClearCell(row, _x + 1);
        row.Cells[_x] = new TerminalCell
        {
            Rune = cp, Width = (byte)width, Flags = _flags, Foreground = _fg, Background = _bg
        };
        if (width == 2)
            row.Cells[_x + 1] = new TerminalCell { Rune = 0, Width = 0, Flags = _flags, Foreground = _fg, Background = _bg };

        _x += width;
        if (_x >= Columns)
        {
            _x = Columns - 1;
            _pendingWrap = true;
        }
    }

    /// <summary>칸을 덮어쓰기 전에, 그 칸이 넓은 글자의 절반이면 나머지 절반을 비운다.</summary>
    private void ClearCell(Line row, int x)
    {
        if (x < 0 || x >= Columns) return;

        var cells = row.Cells;
        if (cells[x].Width == 0 && x > 0) cells[x - 1] = TerminalCell.Blank(cells[x - 1].Background);
        if (cells[x].Width == 2 && x + 1 < Columns) cells[x + 1] = TerminalCell.Blank(cells[x + 1].Background);
        cells[x] = TerminalCell.Blank(_bg);
    }

    private void ShiftRight(Line row, int x, int count)
    {
        var cells = row.Cells;
        count = Math.Min(count, Columns - x);
        Array.Copy(cells, x, cells, x + count, Columns - x - count);
        for (var i = x; i < x + count; i++) cells[i] = TerminalCell.Blank(_bg);
        FixRightEdge(row);
    }

    private void FixRightEdge(Line row)
    {
        if (row.Cells[Columns - 1].Width == 2) row.Cells[Columns - 1] = TerminalCell.Blank(row.Cells[Columns - 1].Background);
    }

    // ------------------------------------------------------------ 커서·스크롤

    private void MoveTo(int x, int y)
    {
        _x = Math.Clamp(x, 0, Columns - 1);
        _y = Math.Clamp(y, 0, Rows - 1);
        _pendingWrap = false;
    }

    private void MoveAbsolute(int x, int y)
    {
        if (_originMode) MoveTo(x, Math.Clamp(y + _top, _top, _bottom));
        else MoveTo(x, y);
    }

    private void LineFeed()
    {
        _pendingWrap = false;
        if (_y == _bottom) ScrollUp(1);
        else if (_y < Rows - 1) _y++;
    }

    private void ReverseIndex()
    {
        _pendingWrap = false;
        if (_y == _top) ScrollDown(1);
        else if (_y > 0) _y--;
    }

    private void ScrollUp(int count)
    {
        var screen = Screen;
        count = Math.Min(count, _bottom - _top + 1);
        for (var n = 0; n < count; n++)
        {
            var first = screen[_top];
            if (_top == 0 && !IsAlternateScreen) PushScrollback(first);
            Array.Copy(screen, _top + 1, screen, _top, _bottom - _top);
            screen[_bottom] = _top == 0 && !IsAlternateScreen ? new Line(Columns, _bg) : first.Reset(_bg);
        }
    }

    private void ScrollDown(int count)
    {
        var screen = Screen;
        count = Math.Min(count, _bottom - _top + 1);
        for (var n = 0; n < count; n++)
        {
            var last = screen[_bottom];
            Array.Copy(screen, _top, screen, _top + 1, _bottom - _top);
            screen[_top] = last.Reset(_bg);
        }
    }

    private void PushScrollback(Line line)
    {
        _scrollback.Add(line);
        ScrolledLines++;
        if (_scrollback.Count <= _scrollbackLimit + 256) return;

        _scrollback.RemoveRange(0, 256); // 한 줄씩 지우면 매번 목록 전체를 당긴다 — 모아서
        DroppedLines += 256;
    }

    private void SetScrollRegion(int top, int bottom)
    {
        bottom = Math.Min(bottom, Rows - 1);
        if (top >= bottom) return;

        _top = Math.Max(0, top);
        _bottom = bottom;
        MoveAbsolute(0, 0);
    }

    private void Tab(int count)
    {
        var x = Math.Min(_x, Columns - 1);
        while (count-- > 0)
        {
            x++;
            while (x < Columns - 1 && !_tabs[x]) x++;
        }

        _x = Math.Min(x, Columns - 1);
        _pendingWrap = false;
    }

    private void BackTab(int count)
    {
        var x = Math.Min(_x, Columns - 1);
        while (count-- > 0 && x > 0)
        {
            x--;
            while (x > 0 && !_tabs[x]) x--;
        }

        _x = x;
        _pendingWrap = false;
    }

    // ------------------------------------------------------------ 지우기·삽입

    private void EraseDisplay(int mode)
    {
        var screen = Screen;
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var y = _y + 1; y < Rows; y++) screen[y].Reset(_bg);
                break;
            case 1:
                EraseLine(1);
                for (var y = 0; y < _y; y++) screen[y].Reset(_bg);
                break;
            case 2:
                foreach (var line in screen) line.Reset(_bg);
                break;
            case 3:
                if (!IsAlternateScreen)
                {
                    DroppedLines += _scrollback.Count;
                    _scrollback.Clear();
                }

                break;
        }
    }

    private void EraseLine(int mode)
    {
        var row = Screen[_y];
        var x = Math.Min(_x, Columns - 1);
        var (from, to) = mode switch { 0 => (x, Columns - 1), 1 => (0, x), _ => (0, Columns - 1) };
        for (var i = from; i <= to; i++) ClearCell(row, i);
        if (mode != 1) row.Wrapped = false;
    }

    private void EraseChars(int count)
    {
        var row = Screen[_y];
        var x = Math.Min(_x, Columns - 1);
        for (var i = x; i < Math.Min(Columns, x + count); i++) ClearCell(row, i);
    }

    private void InsertChars(int count)
    {
        ShiftRight(Screen[_y], Math.Min(_x, Columns - 1), count);
    }

    private void DeleteChars(int count)
    {
        var row = Screen[_y];
        var x = Math.Min(_x, Columns - 1);
        count = Math.Min(count, Columns - x);
        ClearCell(row, x);
        ClearCell(row, x + count - 1);
        if (x + count < Columns && row.Cells[x + count].Width == 0) row.Cells[x + count] = TerminalCell.Blank(_bg);
        Array.Copy(row.Cells, x + count, row.Cells, x, Columns - x - count);
        for (var i = Columns - count; i < Columns; i++) row.Cells[i] = TerminalCell.Blank(_bg);
        _pendingWrap = false;
    }

    private void InsertLines(int count)
    {
        if (_y < _top || _y > _bottom) return;

        var screen = Screen;
        count = Math.Min(count, _bottom - _y + 1);
        for (var n = 0; n < count; n++)
        {
            var last = screen[_bottom];
            Array.Copy(screen, _y, screen, _y + 1, _bottom - _y);
            screen[_y] = last.Reset(_bg);
        }

        _x = 0;
        _pendingWrap = false;
    }

    private void DeleteLines(int count)
    {
        if (_y < _top || _y > _bottom) return;

        var screen = Screen;
        count = Math.Min(count, _bottom - _y + 1);
        for (var n = 0; n < count; n++)
        {
            var first = screen[_y];
            Array.Copy(screen, _y + 1, screen, _y, _bottom - _y);
            screen[_bottom] = first.Reset(_bg);
        }

        _x = 0;
        _pendingWrap = false;
    }

    private void Alignment()
    {
        foreach (var line in Screen)
            for (var i = 0; i < Columns; i++)
                line.Cells[i] = new TerminalCell { Rune = 'E', Width = 1 };
    }

    // ------------------------------------------------------------ 모드·색·상태

    private void SetModes(bool on)
    {
        for (var i = 0; i < Math.Max(1, _paramCount); i++)
        {
            var mode = P0(i);
            if (_prefix == '?') SetPrivateMode(mode, on);
            else if (_prefix == '\0')
                switch (mode)
                {
                    case 4:
                        _insertMode = on;
                        break;
                    case 20:
                        _newLineMode = on;
                        break;
                }
        }
    }

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1:
                ApplicationCursorKeys = on;
                break;
            case 6:
                _originMode = on;
                MoveAbsolute(0, 0);
                break;
            case 7:
                _autoWrap = on;
                break;
            case 25:
                CursorVisible = on;
                break;
            case 47:
            case 1047:
                SwitchScreen(on, false);
                break;
            case 1048:
                if (on) SaveCursor();
                else RestoreCursor();
                break;
            case 1049:
                if (on)
                {
                    SaveCursor();
                    SwitchScreen(true, true);
                }
                else
                {
                    SwitchScreen(false, false);
                    RestoreCursor();
                }

                break;
            case 1000:
                MouseTracking = on ? MouseTracking.Click : MouseTracking.None;
                break;
            case 1002:
                MouseTracking = on ? MouseTracking.Drag : MouseTracking.None;
                break;
            case 1003:
                MouseTracking = on ? MouseTracking.Any : MouseTracking.None;
                break;
            case 1006:
                SgrMouse = on;
                break;
            case 2004:
                BracketedPaste = on;
                break;
        }
    }

    private void SwitchScreen(bool alternate, bool clear)
    {
        if (alternate == IsAlternateScreen) return;

        IsAlternateScreen = alternate;
        if (alternate && clear)
            foreach (var line in _alt)
                line.Reset(TerminalColor.Default);
        _pendingWrap = false;
    }

    private void Sgr()
    {
        if (_paramCount == 0)
        {
            ResetStyle();
            return;
        }

        for (var i = 0; i < _paramCount; i++)
        {
            var p = _params[i] < 0 ? 0 : _params[i];
            switch (p)
            {
                case 0:
                    ResetStyle();
                    break;
                case 1:
                    _flags |= CellFlags.Bold;
                    break;
                case 2:
                    _flags |= CellFlags.Faint;
                    break;
                case 3:
                    _flags |= CellFlags.Italic;
                    break;
                case 4:
                    // 4:0 은 밑줄 끄기, 4:n 은 밑줄 모양 — 모양은 하나로 그린다
                    var off = i + 1 < _paramCount && _subParam[i + 1] && _params[i + 1] == 0;
                    _flags = off ? _flags & ~CellFlags.Underline : _flags | CellFlags.Underline;
                    while (i + 1 < _paramCount && _subParam[i + 1]) i++;
                    break;
                case 5:
                case 6:
                    _flags |= CellFlags.Blink;
                    break;
                case 7:
                    _flags |= CellFlags.Inverse;
                    break;
                case 8:
                    _flags |= CellFlags.Hidden;
                    break;
                case 9:
                    _flags |= CellFlags.Strike;
                    break;
                case 21:
                    _flags |= CellFlags.Underline;
                    break;
                case 22:
                    _flags &= ~(CellFlags.Bold | CellFlags.Faint);
                    break;
                case 23:
                    _flags &= ~CellFlags.Italic;
                    break;
                case 24:
                    _flags &= ~CellFlags.Underline;
                    break;
                case 25:
                    _flags &= ~CellFlags.Blink;
                    break;
                case 27:
                    _flags &= ~CellFlags.Inverse;
                    break;
                case 28:
                    _flags &= ~CellFlags.Hidden;
                    break;
                case 29:
                    _flags &= ~CellFlags.Strike;
                    break;
                case >= 30 and <= 37:
                    _fg = TerminalColor.Indexed(p - 30);
                    break;
                case 38:
                    _fg = ExtendedColor(ref i, _fg);
                    break;
                case 39:
                    _fg = TerminalColor.Default;
                    break;
                case >= 40 and <= 47:
                    _bg = TerminalColor.Indexed(p - 40);
                    break;
                case 48:
                    _bg = ExtendedColor(ref i, _bg);
                    break;
                case 49:
                    _bg = TerminalColor.Default;
                    break;
                case 58:
                    ExtendedColor(ref i, 0); // 밑줄 색 — 읽고 버린다
                    break;
                case >= 90 and <= 97:
                    _fg = TerminalColor.Indexed(p - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _bg = TerminalColor.Indexed(p - 100 + 8);
                    break;
            }
        }
    }

    /// <summary>38/48 뒤의 5;n · 2;r;g;b (쌍점 형식 38:2::r:g:b·38:2:r:g:b 포함).</summary>
    private uint ExtendedColor(ref int i, uint current)
    {
        if (i + 1 < _paramCount && _subParam[i + 1])
        {
            var start = i + 1;
            var end = start;
            while (end + 1 < _paramCount && _subParam[end + 1]) end++;
            i = end;
            var count = end - start + 1;
            var kind = _params[start];
            if (kind == 5 && count >= 2) return TerminalColor.Indexed(Math.Max(0, _params[start + 1]));
            if (kind == 2 && count >= 4)
            {
                var r = end - 2; // 색 공간 칸이 있든 없든 마지막 세 값이 r·g·b
                return TerminalColor.Rgb(Math.Max(0, _params[r]), Math.Max(0, _params[r + 1]), Math.Max(0, _params[r + 2]));
            }

            return current;
        }

        if (i + 1 >= _paramCount) return current;

        var mode = _params[i + 1];
        if (mode == 5 && i + 2 < _paramCount)
        {
            i += 2;
            return TerminalColor.Indexed(Math.Max(0, _params[i]));
        }

        if (mode == 2 && i + 4 < _paramCount)
        {
            var color = TerminalColor.Rgb(Math.Max(0, _params[i + 2]), Math.Max(0, _params[i + 3]),
                Math.Max(0, _params[i + 4]));
            i += 4;
            return color;
        }

        i++;
        return current;
    }

    private void ResetStyle()
    {
        _flags = CellFlags.None;
        _fg = TerminalColor.Default;
        _bg = TerminalColor.Default;
    }

    private void StatusReport()
    {
        var mode = P0(0);
        if (mode == 5)
        {
            _responses.Append("\x1b[0n");
        }
        else if (mode == 6)
        {
            var row = _originMode ? _y - _top + 1 : _y + 1;
            _responses.Append(_prefix == '?' ? "\x1b[?" : "\x1b[").Append(row).Append(';')
                .Append(Math.Min(_x, Columns - 1) + 1).Append('R');
        }
    }

    private void SaveCursor()
    {
        var saved = new SavedCursor(_x, _y, _flags, _fg, _bg, _originMode, _autoWrap, _g[0], _g[1], _gl, _pendingWrap);
        if (IsAlternateScreen) _savedAlt = saved;
        else _savedMain = saved;
    }

    private void RestoreCursor()
    {
        var s = IsAlternateScreen ? _savedAlt : _savedMain;
        _x = Math.Min(s.X, Columns - 1);
        _y = Math.Min(s.Y, Rows - 1);
        _flags = s.Flags;
        _fg = s.Fg;
        _bg = s.Bg;
        _originMode = s.Origin;
        _autoWrap = s.Wrap || s == default;
        _g[0] = s.G0;
        _g[1] = s.G1;
        _gl = s.Gl;
        _pendingWrap = s.PendingWrap && _x == Columns - 1;
    }

    private void SoftReset()
    {
        CursorVisible = true;
        _originMode = false;
        _autoWrap = true;
        _insertMode = false;
        ApplicationCursorKeys = false;
        _top = 0;
        _bottom = Rows - 1;
        _g[0] = _g[1] = false;
        _gl = 0;
        ResetStyle();
        _savedMain = _savedAlt = default;
    }

    private void FullReset()
    {
        SoftReset();
        if (IsAlternateScreen) SwitchScreen(false, false);
        foreach (var line in _main) line.Reset(TerminalColor.Default);
        _x = _y = 0;
        _pendingWrap = false;
        BracketedPaste = false;
        MouseTracking = MouseTracking.None;
        SgrMouse = false;
        ApplicationKeypad = false;
        CursorStyle = 0;
        _newLineMode = false;
        _tabs = NewTabs(Columns);
    }

    private static Line[] NewScreen(int columns, int rows)
    {
        var lines = new Line[rows];
        for (var i = 0; i < rows; i++) lines[i] = new Line(columns, TerminalColor.Default);
        return lines;
    }

    private static bool[] NewTabs(int columns)
    {
        var tabs = new bool[columns];
        for (var i = 8; i < columns; i += 8) tabs[i] = true;
        return tabs;
    }

    private readonly record struct SavedCursor(
        int X,
        int Y,
        CellFlags Flags,
        uint Fg,
        uint Bg,
        bool Origin,
        bool Wrap,
        bool G0,
        bool G1,
        int Gl,
        bool PendingWrap);

    /// <summary>화면 한 줄. <see cref="Wrapped" /> 는 자동 줄바꿈으로 다음 줄에 이어진다는 뜻(복사할 때 붙인다).</summary>
    public sealed class Line
    {
        public Line(int columns, uint background)
        {
            Cells = new TerminalCell[columns];
            Reset(background);
        }

        public TerminalCell[] Cells { get; private set; }
        public bool Wrapped { get; set; }

        public bool IsBlank
        {
            get
            {
                foreach (var cell in Cells)
                    if (cell.Rune is not (0 or ' ') || cell.Background != TerminalColor.Default)
                        return false;
                return true;
            }
        }

        public Line Reset(uint background)
        {
            Array.Fill(Cells, TerminalCell.Blank(background));
            Wrapped = false;
            return this;
        }

        public void Resize(int columns)
        {
            if (columns == Cells.Length) return;

            var old = Cells;
            var cells = new TerminalCell[columns];
            Array.Copy(old, cells, Math.Min(old.Length, columns));
            for (var i = old.Length; i < columns; i++) cells[i] = TerminalCell.Blank(TerminalColor.Default);
            if (cells[columns - 1].Width == 2) cells[columns - 1] = TerminalCell.Blank(cells[columns - 1].Background);
            Cells = cells;
        }
    }
}
