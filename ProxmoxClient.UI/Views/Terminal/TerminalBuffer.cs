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
public sealed partial class TerminalBuffer
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
    private readonly StringBuilder _dcs = new();
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
    private readonly bool[] _g = new bool[4]; // G0~G3 가 DEC 그래픽인가(그리는 데는 SI/SO 로 고른 G0·G1 만 쓴다)
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

    /// <summary>
    ///     동기화 출력(DEC 2026) 중 — 프로그램이 한 화면을 다 쓸 때까지 그리지 않는다(Claude Code·neovim 등은 다시 그릴 때마다
    ///     켜고 끈다). 그리는 중간 상태가 보이면 글자가 튀거나 겹쳐 보인다.
    /// </summary>
    public bool SynchronizedOutput { get; private set; }

    /// <summary>
    ///     동기화 출력이 켜진 시각(<see cref="Environment.TickCount64" />) — 끄는 신호가 오지 않으면(프로그램이 죽음) 그리는 쪽이
    ///     잠시 뒤 그냥 그린다.
    /// </summary>
    public long SynchronizedSince { get; private set; }

    /// <summary>커서 깜박임(?12) — 질의에 답하려고 기억만 한다.</summary>
    public bool CursorBlink { get; private set; }

    /// <summary>색 테마 변경 알림(?2031) — 기억만 한다(테마가 바뀌지 않는다).</summary>
    public bool ColorSchemeReports { get; private set; }

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
    ///     화면 크기를 바꾼다. 열 수가 바뀌면 주 화면·스크롤백의 줄을 다시 접는다(<see cref="Reflow" />). 줄이 줄면 커서가 보이도록
    ///     위 줄을 스크롤백으로 밀고, 늘면 스크롤백에서 다시 가져온다(xterm 과 같다). 대체 화면은 자르기만 한다(프로그램이 다시 그린다).
    /// </summary>
    public void Resize(int columns, int rows) => Resize(columns, rows, 0);

    /// <summary>
    ///     <see cref="Resize(int,int)" /> — <paramref name="scrollOffset" />(위로 올려 보던 줄 수)를 같은 내용이 맨 위에 오도록
    ///     고쳐 돌려준다.
    /// </summary>
    public int Resize(int columns, int rows, int scrollOffset)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        lock (Gate)
        {
            if (columns == Columns && rows == Rows) return scrollOffset;

            scrollOffset = Reflow(columns, rows, scrollOffset);
            _alt = ResizeAlt(columns, rows);
            Columns = columns;
            Rows = rows;
            _tabs = ResizeTabs(_tabs, columns);
            _top = 0;
            _bottom = rows - 1;
            if (IsAlternateScreen)
            {
                _x = Math.Min(_x, columns - 1);
                _pendingWrap = false;
            }

            _y = Math.Clamp(_y, 0, rows - 1);
            Version++;
            return scrollOffset;
        }
    }

    /// <summary>대체 화면 — 줄 끝을 자르고, 줄이 줄면 커서 아래 빈 줄부터 버린 뒤 위 줄을 버린다(스크롤백 없음).</summary>
    private Line[] ResizeAlt(int columns, int rows)
    {
        var lines = new List<Line>(_alt);
        // 대체 화면이 꺼져 있으면 저장해 둔 커서를 기준으로 같은 만큼 옮긴다
        var cursorY = IsAlternateScreen ? _y : _savedAlt != default ? Math.Min(_savedAlt.Y, lines.Count - 1) : -1;

        if (rows < lines.Count)
        {
            var excess = lines.Count - rows;
            for (var i = lines.Count - 1; i > cursorY && excess > 0; i--, excess--)
                if (lines[i].IsBlank) lines.RemoveAt(i);
                else break;

            while (lines.Count > rows)
            {
                lines.RemoveAt(0);
                if (cursorY >= 0) cursorY--;
            }
        }

        while (lines.Count < rows) lines.Add(new Line(columns, TerminalColor.Default));

        foreach (var line in lines) line.Resize(columns);
        cursorY = Math.Max(0, cursorY);
        if (IsAlternateScreen) _y = cursorY;
        else if (_savedAlt != default)
            _savedAlt = _savedAlt with { X = Math.Min(_savedAlt.X, columns - 1), Y = cursorY, PendingWrap = false };
        return [.. lines];
    }

    /// <summary>탭 위치 — 있던 열은 그대로, 새로 생긴 열은 8칸마다.</summary>
    private static bool[] ResizeTabs(bool[] old, int columns)
    {
        var tabs = NewTabs(columns);
        Array.Copy(old, tabs, Math.Min(old.Length, columns));
        return tabs;
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
                    // 붙은 글자는 받은 그대로(정규화하지 않는다 — 파일 이름 등을 원래 코드 포인트로 붙여 넣게)
                    if (line.GetExtra(c) is { } extra) text.Append(extra);
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
        Charset96,
        Csi,
        Osc,
        OscEscape,
        Ignore, // APC·PM·SOS — ST 까지 버린다
        IgnoreEscape,
        Dcs, // DCS — ST 까지 모은다(XTGETTCAP 질의에 답한다)
        DcsEscape,
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

        if (_pendingHigh != 0 && !char.IsLowSurrogate(c)) _pendingHigh = '\0'; // 짝 없는 앞 반쪽은 버린다
        int cp = c;
        if (char.IsLowSurrogate(c))
        {
            if (_pendingHigh == 0) return;

            cp = char.ConvertToUtf32(_pendingHigh, c);
            _pendingHigh = '\0';
        }

        if (_state is State.Osc or State.OscEscape or State.Ignore or State.IgnoreEscape or State.Dcs or State.DcsEscape)
        {
            String(cp);
            return;
        }

        if (cp < 0x20 || cp == 0x7F)
        {
            Control(cp);
            return;
        }

        if (cp is >= 0x80 and <= 0x9F)
        {
            // C1 제어 문자(8비트) — ESC + (c - 0x40) 과 같다. 글자로 그리지 않는다
            _state = State.Escape;
            _intermediate = '\0';
            Escape(cp - 0x40);
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
            case State.Charset96:
                _g[_charsetSlot] = false;
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
        if (c != 0x1B) _lastRune = 0; // REP 은 바로 앞 글자만 되풀이한다(제어열 시작 ESC 는 빼고)
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
            case 'P':
                _state = State.Dcs;
                _dcs.Clear();
                break;
            case '_' or '^' or 'X':
                _state = State.Ignore;
                break;
            case '(' or ')' or '*' or '+':
                _charsetSlot = c - '(';
                _state = State.Charset;
                break;
            case '-' or '.' or '/':
                // 96글자 문자 집합 지정(G1~G3) — DEC 그래픽은 94글자 쪽에만 있다. 다음 글자 하나를 먹고 일반 글자로 둔다
                _charsetSlot = c - ',';
                _state = State.Charset96;
                break;
            case 'Z':
                _responses.Append(DeviceAttributes); // DECID — DA1 과 같은 답
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
        // CAN·SUB 는 문자열을 끊는다. C1 ST(0x9C)는 끝맺음
        if (c is 0x18 or 0x1A)
        {
            _state = State.Ground;
            return;
        }

        if (c == 0x9C)
        {
            if (_state is State.Osc or State.OscEscape) EndOsc(false);
            else if (_state is State.Dcs or State.DcsEscape) EndDcs();
            else _state = State.Ground;
            return;
        }

        switch (_state)
        {
            case State.Osc:
                if (c == 0x07) EndOsc(true);
                else if (c == 0x1B) _state = State.OscEscape;
                else if (_osc.Length < OscLimit()) _osc.Append(char.ConvertFromUtf32(c));
                break;
            case State.OscEscape:
                if (c == '\\') EndOsc(false);
                else AbortString(c);
                break;
            case State.Dcs:
                if (c == 0x1B) _state = State.DcsEscape;
                else if (c is 0x07 or 0x18 or 0x1A) _state = State.Ground;
                else if (_dcs.Length < 1024) _dcs.Append(char.ConvertFromUtf32(c));
                break;
            case State.DcsEscape:
                if (c == '\\') EndDcs();
                else AbortString(c);
                break;
            case State.Ignore:
                if (c == 0x1B) _state = State.IgnoreEscape;
                else if (c == 0x07 || c == 0x18 || c == 0x1A) _state = State.Ground;
                break;
            case State.IgnoreEscape:
                if (c == '\\') _state = State.Ground;
                else AbortString(c);
                break;
        }
    }

    /// <summary>
    ///     문자열 안의 ESC 뒤가 ST 가 아니다 — 끝나지 않은 문자열(잘린 제목 등)이다. 버리고 그 ESC 를 새 제어열로 읽는다
    ///     (xterm 과 같다). 그러지 않으면 뒤따르는 출력을 BEL 이 올 때까지 모두 삼킨다.
    /// </summary>
    private void AbortString(int c)
    {
        _state = State.Escape;
        _intermediate = '\0';
        if (c is < 0x20 or 0x7F) Control(c);
        else Escape(c);
    }

    /// <summary>OSC 길이 한도 — 원격 복사(OSC 52)는 큰 글을 담으므로 넉넉히, 나머지는 4KB.</summary>
    private int OscLimit()
    {
        return _osc.Length >= 3 && _osc[0] == '5' && _osc[1] == '2' && _osc[2] == ';' ? 1 << 20 : 4096;
    }

    /// <summary>
    ///     원격 프로그램이 클립보드에 넣으라고 보낸 글(OSC 52 — tmux·vim·neovim 의 복사). 수신 스레드에서 불린다.
    ///     클립보드 읽기 요청(?)에는 답하지 않는다(서버가 사용자 클립보드를 몰래 읽지 못하게).
    /// </summary>
    public event Action<string>? ClipboardWrite;

    private void ClipboardOsc(string value)
    {
        var sep = value.IndexOf(';');
        if (sep < 0) return;

        var data = value[(sep + 1)..];
        if (data == "?" || data.Length == 0) return;

        try
        {
            ClipboardWrite?.Invoke(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
        }
        catch (FormatException)
        {
            // 잘못된 base64 — 무시
        }
    }

    private void EndOsc(bool bel)
    {
        _state = State.Ground;
        var text = _osc.ToString();
        var sep = text.IndexOf(';');
        if (sep <= 0) return;

        var code = text[..sep];
        if (code is "0" or "2") Title = text[(sep + 1)..];
        if (code == "52") ClipboardOsc(text[(sep + 1)..]);
        // 글자색·바탕색 질의(OSC 10/11 ;?) — systemd·vim·Claude Code 가 밝은/어두운 바탕을 고를 때 묻는다. 묻은 끝맺음으로 답한다
        if (code is "10" or "11" && text[(sep + 1)..] == "?")
        {
            var rgb = code == "10" ? TerminalRenderer.DefaultForeground : TerminalRenderer.DefaultBackground;
            string Channel(int shift) => $"{(rgb >> shift) & 0xFF:x2}{(rgb >> shift) & 0xFF:x2}";
            _responses.Append($"\x1b]{code};rgb:{Channel(16)}/{Channel(8)}/{Channel(0)}{(bel ? "\x07" : "\x1b\\")}");
        }
    }

    /// <summary>DA1 답 — VT220 급(62) · ANSI 색(22). 그림(sixel 4)·클립보드 읽기는 알리지 않는다.</summary>
    private const string DeviceAttributes = "\x1b[?62;22c";

    /// <summary>XTVERSION 에 알리는 판.</summary>
    private static readonly string AppVersion =
        typeof(TerminalBuffer).Assembly.GetName().Version?.ToString(3) ?? "1.0";

    /// <summary>터미널이 알려 주는 이름(XTGETTCAP name) — 서버의 TERM 과 같게.</summary>
    private const string TerminalName = "xterm-256color";

    /// <summary>
    ///     DCS 끝 — XTGETTCAP(DCS + q 이름16진 ST) 질의에 답한다. 'name'(터미널 이름)만 알려 주고 나머지는 모른다고 답한다
    ///     (답이 없으면 묻는 쪽이 시간 초과까지 기다린다).
    /// </summary>
    private void EndDcs()
    {
        _state = State.Ground;
        var text = _dcs.ToString();
        if (text.StartsWith("$q", StringComparison.Ordinal))
        {
            RequestSetting(text[2..]);
            return;
        }

        if (!text.StartsWith("+q", StringComparison.Ordinal)) return;

        foreach (var hex in text[2..].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null;
            try
            {
                name = Encoding.ASCII.GetString(Convert.FromHexString(hex));
            }
            catch (FormatException)
            {
            }

            if (name is null || !Capabilities.TryGetValue(name, out var value))
                _responses.Append($"\x1bP0+r{hex}\x1b\\");
            else if (value is null)
                _responses.Append($"\x1bP1+r{hex}\x1b\\"); // 참·거짓 능력(Tc) — 값 없이
            else
                _responses.Append($"\x1bP1+r{hex}={Convert.ToHexString(Encoding.UTF8.GetBytes(value))}\x1b\\");
        }
    }

    /// <summary>
    ///     XTGETTCAP 로 알려 주는 능력 — neovim·tmux 가 24비트 색·커서 모양을 여기서 확인한다(termproxy 는 COLORTERM 을
    ///     넘기지 않아 이게 없으면 256색으로 떨어진다). 값이 null 이면 참·거짓 능력. 클립보드(Ms)는 읽기 질의에 답하지
    ///     않으므로 알리지 않는다.
    /// </summary>
    private static readonly Dictionary<string, string?> Capabilities = new()
    {
        ["name"] = TerminalName,
        ["TN"] = TerminalName,
        ["Co"] = "256",
        ["colors"] = "256",
        ["RGB"] = "8/8/8",
        ["Tc"] = null,
        ["setrgbf"] = "\\E[38;2;%p1%d;%p2%d;%p3%dm",
        ["setrgbb"] = "\\E[48;2;%p1%d;%p2%d;%p3%dm",
        ["Ss"] = "\\E[%p1%d q",
        ["Se"] = "\\E[2 q",
        ["Smulx"] = "\\E[4:%p1%dm",
        ["Sync"] = "\\E[?2026%?%p1%{1}%-%tl%eh%;"
    };

    /// <summary>DECRQSS(DCS $ q 설정 ST) — 지금 설정을 돌려준다. 모르는 설정은 0 $ r.</summary>
    private void RequestSetting(string setting)
    {
        string? value = setting switch
        {
            "m" => CurrentSgr() + "m",
            "r" => $"{_top + 1};{_bottom + 1}r",
            " q" => $"{CursorStyle} q",
            "\"p" => "62;1\"p",
            "\"q" => "0\"q",
            _ => null
        };
        _responses.Append(value is null ? "\x1bP0$r\x1b\\" : $"\x1bP1$r{value}\x1b\\");
    }

    /// <summary>지금 글자 꾸밈을 SGR 매개변수로(DECRQSS 'm') — neovim 은 24비트 색을 넣어 보고 되돌아오는지로 지원을 본다.</summary>
    private string CurrentSgr()
    {
        var parts = new List<string> { "0" };
        foreach (var (flag, code) in new[]
                 {
                     (CellFlags.Bold, "1"), (CellFlags.Faint, "2"), (CellFlags.Italic, "3"), (CellFlags.Underline, "4"),
                     (CellFlags.Blink, "5"), (CellFlags.Inverse, "7"), (CellFlags.Hidden, "8"), (CellFlags.Strike, "9")
                 })
            if (_flags.HasFlag(flag)) parts.Add(code);
        AddColor(_fg, 30, 38);
        AddColor(_bg, 40, 48);
        return string.Join(';', parts);

        void AddColor(uint color, int basic, int extended)
        {
            if (TerminalColor.IsIndexed(color, out var index))
                parts.Add(index < 8 ? $"{basic + index}" : index < 16 ? $"{basic + 60 + index - 8}" : $"{extended}:5:{index}");
            else if (TerminalColor.IsRgb(color, out var rgb))
                parts.Add($"{extended}:2::{(rgb >> 16) & 0xFF}:{(rgb >> 8) & 0xFF}:{rgb & 0xFF}");
        }
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
            if (P0(0) <= 6) CursorStyle = P0(0);
            return;
        }

        if (_prefix == '>' && final == 'q' && _intermediate == '\0')
        {
            // XTVERSION — 이름을 xterm 으로 대지 않는다(tmux 가 이 터미널에 없는 xterm 기능을 켠다)
            _responses.Append($"\x1bP>|ProxmoxClient({AppVersion})\x1b\\");
            return;
        }

        if (_intermediate == '!' && final == 'p')
        {
            SoftReset();
            return;
        }

        if (_intermediate == '$' && final == 'p')
        {
            ReportMode();
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
                MoveTo(0, Math.Min(_y + P(0, 1), _y <= _bottom ? _bottom : Rows - 1));
                break;
            case 'F':
                MoveTo(0, Math.Max(_y - P(0, 1), _y >= _top ? _top : 0));
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
                // 앞 글자 되풀이(REP) — 바로 앞이 글자가 아니면(제어 문자·제어열 뒤) 하지 않는다
                if (_lastRune != 0)
                    for (var i = Math.Min(P(0, 1), Columns * Rows); i > 0; i--) Print(_lastRune);
                break;
            case 'c':
                // DA2: 판 277 이상이어야 vim 이 SGR 마우스(넓은 화면 좌표)를 쓴다. DA3: 단말 번호
                if (_prefix == '>') _responses.Append("\x1b[>1;277;0c");
                else if (_prefix == '=') _responses.Append("\x1bP!|00000000\x1b\\");
                else if (_prefix == '\0') _responses.Append(DeviceAttributes);
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
            case 't':
                // 창 크기 질의(글자 칸) — systemd 는 콘솔을 열 때 이걸 먼저 묻고(0.33초), 답이 없으면 커서를 끝으로 보내 위치를
                // 묻는다. 늦게 온 답은 로그인 칸에 '^[[24;80R' 로 찍히므로 첫 질문에 바로 답한다
                if (_prefix == '\0' && P0(0) is 18 or 19)
                    _responses.Append($"\x1b[{(P0(0) == 18 ? 8 : 9)};{Rows};{Columns}t");
                break;
        }
    }

    // ------------------------------------------------------------ 글자 쓰기

    private void Print(int cp)
    {
        if (_g[_gl] && cp is >= 0x5F and <= 0x7E) cp = DecGraphics[cp - 0x5F];

        var width = CharWidth.Of(cp);
        // 결합 문자·ZWJ 뒤 글자는 새 칸을 쓰지 않고 앞 칸에 붙는다
        if (width == 0 || JoinsPrevious())
        {
            Attach(cp);
            return;
        }

        if (CharWidth.IsRegionalIndicator(cp) && PairRegional(cp)) return;

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

    /// <summary>붙을 칸 — 커서 바로 앞 글자(넓은 글자의 뒤 칸이면 앞 칸). 없으면 -1.</summary>
    private int PreviousCell()
    {
        var x = _pendingWrap ? _x : _x - 1;
        if (x < 0 || x >= Columns) return -1;

        var cells = Screen[_y].Cells;
        if (cells[x].Width == 0 && x > 0) x--;
        return cells[x].Width == 0 ? -1 : x;
    }

    /// <summary>앞 칸이 ZWJ(U+200D)로 끝나면 다음 글자는 그 묶음에 붙는다(👨‍👩‍👧 같은 이모지).</summary>
    private bool JoinsPrevious()
    {
        var x = PreviousCell();
        return x >= 0 && Screen[_y].GetExtra(x) is { Length: > 0 } extra && extra[^1] == '‍';
    }

    /// <summary>결합 문자를 앞 칸에 붙인다. 앞 칸이 없으면(줄 처음) 버린다.</summary>
    private void Attach(int cp)
    {
        var x = PreviousCell();
        if (x < 0) return;

        Screen[_y].AppendExtra(x, cp);
    }

    /// <summary>
    ///     국기 — 짝 없는 지역 표시 문자 바로 뒤에 또 오면 앞 칸을 2칸으로 넓혀 한 묶음으로 만든다(wcwidth 합과 같은 2칸).
    ///     만들었으면 true.
    /// </summary>
    private bool PairRegional(int cp)
    {
        if (_pendingWrap || _x < 1 || _x >= Columns) return false;

        var row = Screen[_y];
        ref var prev = ref row.Cells[_x - 1];
        if (prev.Width != 1 || !CharWidth.IsRegionalIndicator(prev.Rune) || prev.Flags.HasFlag(CellFlags.Combined))
            return false;

        if (_insertMode) ShiftRight(row, _x, 1);
        ClearCell(row, _x);
        prev = ref row.Cells[_x - 1];
        prev.Width = 2;
        row.AppendExtra(_x - 1, cp);
        row.Cells[_x] = new TerminalCell
        {
            Rune = 0, Width = 0, Flags = _flags, Foreground = row.Cells[_x - 1].Foreground, Background = row.Cells[_x - 1].Background
        };
        _x++;
        if (_x >= Columns)
        {
            _x = Columns - 1;
            _pendingWrap = true;
        }

        return true;
    }

    /// <summary>칸을 덮어쓰기 전에, 그 칸이 넓은 글자의 절반이면 나머지 절반을 비운다.</summary>
    private void ClearCell(Line row, int x)
    {
        if (x < 0 || x >= Columns) return;

        var cells = row.Cells;
        if (cells[x].Width == 0 && x > 0)
        {
            cells[x - 1] = TerminalCell.Blank(cells[x - 1].Background);
            row.ClearExtra(x - 1);
        }

        if (cells[x].Width == 2 && x + 1 < Columns) cells[x + 1] = TerminalCell.Blank(cells[x + 1].Background);
        cells[x] = TerminalCell.Blank(_bg);
        row.ClearExtra(x);
    }

    private void ShiftRight(Line row, int x, int count)
    {
        var cells = row.Cells;
        count = Math.Min(count, Columns - x);
        if (cells[x].Width == 0 && x > 0) ClearCell(row, x - 1); // 넓은 글자의 오른쪽 반에 끼워 넣으면 그 글자를 지운다(반쪽이 남지 않게)
        Array.Copy(cells, x, cells, x + count, Columns - x - count);
        for (var i = x; i < x + count; i++) cells[i] = TerminalCell.Blank(_bg);
        row.ShiftExtras(x, count);
        FixRightEdge(row);
    }

    private void FixRightEdge(Line row)
    {
        if (row.Cells[Columns - 1].Width != 2) return;

        row.Cells[Columns - 1] = TerminalCell.Blank(row.Cells[Columns - 1].Background);
        row.ClearExtra(Columns - 1);
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
        if (mode != 3) _pendingWrap = false;
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
        _pendingWrap = false; // xterm 처럼 — 끝 칸에 쓴 뒤 줄을 지우고 쓰면 다음 줄로 넘어가지 않는다
        var row = Screen[_y];
        var x = Math.Min(_x, Columns - 1);
        var (from, to) = mode switch { 0 => (x, Columns - 1), 1 => (0, x), _ => (0, Columns - 1) };
        for (var i = from; i <= to; i++) ClearCell(row, i);
        if (mode != 1) row.Wrapped = false;
    }

    private void EraseChars(int count)
    {
        _pendingWrap = false;
        var row = Screen[_y];
        var x = Math.Min(_x, Columns - 1);
        for (var i = x; i < Math.Min(Columns, x + count); i++) ClearCell(row, i);
    }

    private void InsertChars(int count)
    {
        _pendingWrap = false;
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
        row.ShiftExtras(x, -count);
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
                SwitchScreen(on, false);
                break;
            case 1047:
                // 1047 은 대체 화면에서 나올 때 그 화면을 지운다(47 은 지우지 않는다)
                if (!on && IsAlternateScreen)
                    foreach (var line in _alt)
                        line.Reset(TerminalColor.Default);
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
            case 12:
                CursorBlink = on;
                break;
            case 66:
                ApplicationKeypad = on; // DECNKM
                break;
            case 2031:
                ColorSchemeReports = on; // 밝은/어두운 테마가 바뀌면 알림 — 이 앱은 늘 어두운 터미널이라 바뀔 일이 없다
                break;
            case 2026:
                SynchronizedOutput = on;
                if (on) SynchronizedSince = Environment.TickCount64;
                break;
        }
    }

    /// <summary>
    ///     모드 질의(DECRQM, CSI ? Ps $ p) — 1 켜짐 · 2 꺼짐 · 0 모름. 프로그램은 이 답으로 동기화 출력(2026) 같은 기능을 쓸지
    ///     정한다.
    /// </summary>
    private void ReportMode()
    {
        var mode = P0(0);
        if (_prefix != '?')
        {
            var plain = mode switch { 4 => _insertMode, 20 => _newLineMode, _ => (bool?)null };
            _responses.Append($"\x1b[{mode};{(plain is null ? 0 : plain.Value ? 1 : 2)}$y");
            return;
        }

        bool? state = mode switch
        {
            1 => ApplicationCursorKeys,
            6 => _originMode,
            7 => _autoWrap,
            25 => CursorVisible,
            47 or 1047 or 1049 => IsAlternateScreen,
            1000 => MouseTracking == MouseTracking.Click,
            1002 => MouseTracking == MouseTracking.Drag,
            1003 => MouseTracking == MouseTracking.Any,
            1006 => SgrMouse,
            2004 => BracketedPaste,
            2026 => SynchronizedOutput,
            12 => CursorBlink,
            66 => ApplicationKeypad,
            2031 => ColorSchemeReports,
            _ => null
        };
        _responses.Append($"\x1b[?{mode};{(state is null ? 0 : state.Value ? 1 : 2)}$y");
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
        if (mode == 5 && _prefix == '\0')
        {
            _responses.Append("\x1b[0n");
        }
        else if (mode == 6 && _prefix is '\0' or '?')
        {
            var row = _originMode ? _y - _top + 1 : _y + 1;
            var col = Math.Min(_x, Columns - 1) + 1;
            _responses.Append(_prefix == '?' ? $"\x1b[?{row};{col};1R" : $"\x1b[{row};{col}R"); // DECXCPR 은 쪽 번호까지
        }
        else if (_prefix == '?')
        {
            // 색 테마(?996 → 997;1 어두움) · 프린터 없음(?15) · 사용자 키 잠김 없음(?25) · 키보드 종류(?26)
            var reply = mode switch
            {
                996 => "\x1b[?997;1n",
                15 => "\x1b[?13n",
                25 => "\x1b[?21n",
                26 => "\x1b[?27;1;0;0n",
                _ => null
            };
            if (reply is not null) _responses.Append(reply);
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
        ApplicationKeypad = false;
        _pendingWrap = false;
        _top = 0;
        _bottom = Rows - 1;
        Array.Fill(_g, false);
        _gl = 0;
        ResetStyle();
        _savedMain = _savedAlt = default;
    }

    private void FullReset()
    {
        SoftReset();
        SynchronizedOutput = false;
        CursorBlink = false;
        ColorSchemeReports = false;
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

        /// <summary>
        ///     칸에 붙은 글자(결합 문자·ZWJ 이모지·국기 둘째 글자 등) — 칸 번호 → 덧붙은 코드 포인트. 드물어서 따로 둔다.
        ///     칸에 <see cref="CellFlags.Combined" /> 가 있을 때만 쓴다(칸을 옮기거나 지우다 남은 값은 무시된다).
        /// </summary>
        public Dictionary<int, string>? Extra { get; private set; }

        /// <summary>한 칸에 붙일 수 있는 글자 수(UTF-16) — 끝없이 붙는 결합 문자(자고 글자)로 커지지 않게.</summary>
        public const int MaxExtra = 32;

        /// <summary>칸에 붙은 글자(없으면 null).</summary>
        public string? GetExtra(int column)
        {
            return Extra is not null && (uint)column < (uint)Cells.Length &&
                   Cells[column].Flags.HasFlag(CellFlags.Combined) && Extra.TryGetValue(column, out var s)
                ? s
                : null;
        }

        /// <summary>칸에 글자를 덧붙인다.</summary>
        public void AppendExtra(int column, int cp)
        {
            var before = GetExtra(column) ?? "";
            if (before.Length >= MaxExtra) return;

            Extra ??= [];
            Extra[column] = before + char.ConvertFromUtf32(cp);
            Cells[column].Flags |= CellFlags.Combined;
        }

        /// <summary>결합 문자를 통째로 넣는다(줄 다시 감기에서 칸을 옮길 때) — 칸의 Combined 표시는 칸과 함께 이미 옮겨졌다.</summary>
        public void SetExtra(int column, string extra)
        {
            Extra ??= [];
            Extra[column] = extra;
        }

        public void ClearExtra(int column)
        {
            if (Extra is null) return;

            Extra.Remove(column);
            if (Extra.Count == 0) Extra = null;
        }

        /// <summary>칸을 옮길 때(삽입·삭제) 붙은 글자도 함께 — <paramref name="from" /> 부터 <paramref name="delta" /> 만큼.</summary>
        public void ShiftExtras(int from, int delta)
        {
            if (Extra is null) return;

            var moved = new Dictionary<int, string>();
            foreach (var (column, s) in Extra)
            {
                var to = column >= from ? column + delta : column;
                if (column >= from && (to < from || to >= Cells.Length)) continue;

                moved[to] = s;
            }

            Extra = moved.Count > 0 ? moved : null;
        }

        public Line Reset(uint background)
        {
            Array.Fill(Cells, TerminalCell.Blank(background));
            Wrapped = false;
            Extra = null;
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
            if (Extra is null) return;

            foreach (var column in Extra.Keys.Where(k => k >= columns).ToList()) Extra.Remove(column);
            if (Extra.Count == 0) Extra = null;
        }
    }
}
