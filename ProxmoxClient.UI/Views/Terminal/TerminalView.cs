using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>
///     터미널 화면 — <see cref="TerminalBuffer" /> 를 그리고 키·마우스·터치를 터미널 입력으로 바꾼다.
///     <list type="bullet">
///         <item>출력: 수신 스레드에서 <see cref="Write" /> → 버퍼 해석 → 화면 갱신은 한 프레임에 한 번으로 모은다.</item>
///         <item>입력: 보이지 않는 입력 칸(IME·소프트 키보드)을 커서 위치에 두고, 조합 중인 글자(한글)는 커서 자리에 그린다.</item>
///         <item>마우스: 끌어 선택·두 번 눌러 단어·세 번 눌러 줄, 오른쪽 단추는 선택이 있으면 복사·없으면 붙여넣기(Windows 터미널).
///             프로그램이 마우스 보고를 켜면(mc·htop) 클릭·휠을 그대로 보낸다(Shift 를 누르면 선택).</item>
///         <item>터치: 끌어 스크롤, 길게 눌러 선택, 두 손가락 벌려 글자 크기, 탭하면 키보드.</item>
///     </list>
/// </summary>
public sealed class TerminalView : Grid
{
    private const double MinFont = 8;
    private const double MaxFont = 32;
    private static readonly Thickness Pad = new(6, 4);

    private readonly Surface _surface;
    private readonly TextBox _input;
    private readonly DispatcherTimer _longPress = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly Dictionary<IPointer, Point> _touches = [];
    private TerminalRenderer _renderer;
    private int _redrawQueued;
    private long _seenScrolled;
    private string? _preedit;

    // 선택(절대 줄 번호 · 칸)
    private (long Row, int Col)? _anchor;
    private (long Row, int Col) _caret;
    private bool _selecting;

    // 터치
    private Point _touchStart;
    private DateTime _touchStartTime;
    private double _scrollRemainder;
    private bool _touchMoved;
    private bool _touchSelecting;
    private bool _touchHandles; // 손가락으로 만든 선택 — 양 끝에 손잡이를 그리고, 손잡이를 끌어 넓히거나 줄인다
    private Point _touchPos;
    private double _pinchDistance;
    private double _pinchFont;

    // 마우스 보고 — 누른 단추(비트 0 왼쪽 1 가운데 2 오른쪽), 마지막으로 보고한 칸, 휠 나머지(터치패드의 잘게 나뉜 휠)
    private int _mouseButtons;
    private (int Col, int Row) _lastMouseCell = (-1, -1);
    private double _wheelX;
    private double _wheelY;
    private double _wheelLines;

    // 포커스 보고(?1004) — 마지막으로 알린 상태
    private bool _focusReported;
    private bool _focusReportQueued;

    // 바탕(OSC 11·화면 반전을 따른다)
    private readonly SolidColorBrush _background = new(Color.FromUInt32(0xFF000000 | TerminalRenderer.DefaultBackground));

    public TerminalView(double fontSize = 14)
    {
        Buffer = new TerminalBuffer(80, 24);
        _renderer = new TerminalRenderer(fontSize);
        Background = _background;
        ClipToBounds = true;
        Focusable = false;

        _surface = new Surface(this);
        _surface.Cursor = new Cursor(StandardCursorType.Ibeam); // 실제 마우스 — 글자를 고르는 곳
        _input = new TextBox
        {
            Width = 2, Height = 2, Opacity = 0.01, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), AcceptsReturn = false, AcceptsTab = true,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Text = string.Empty
        };
        Controls.SoftKeyboard.Configure(_input); // Enter 로 키보드가 닫히지 않게
        _input.TextChanged += OnInputText;
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _input.GotFocus += (_, _) => OnInputFocusChanged();
        _input.LostFocus += (_, _) => OnInputFocusChanged();
        _input.TemplateApplied += (_, _) => WatchPreedit();

        Children.Add(_surface);
        Children.Add(_input);

        _longPress.Tick += (_, _) => OnLongPress();
    }

    public TerminalBuffer Buffer { get; }

    /// <summary>서버로 보낼 입력(키·붙여넣기·상태 응답). 수신 스레드에서 불릴 수도 있다(DSR 응답).</summary>
    public Action<string>? Input { get; set; }

    /// <summary>칸 수가 바뀌었다(열, 행) — 서버 PTY 크기를 맞춘다.</summary>
    public event Action<int, int>? SizeChanged2;

    /// <summary>Enter 로 명령 한 줄을 실행했다(프롬프트 뒤 글자).</summary>
    public event Action<string>? LineSubmitted;

    /// <summary>선택이 생기거나 없어졌다.</summary>
    public event Action<bool>? SelectionChanged;

    /// <summary>스크롤백을 거슬러 본 줄 수(0 = 맨 아래, 새 출력이 보인다).</summary>
    public int ScrollOffset { get; private set; }

    public double TerminalFontSize => _renderer.FontSize;

    /// <summary>이 글자를 그리는 글꼴 이름(없으면 null — 검사용).</summary>
    public string? FontFor(int rune) => _renderer.FontFor(rune);

    /// <summary>
    ///     특수 키 막대에 고정해 둔 Ctrl·Alt·Shift(보기만 한다). 실제로 키나 글자를 보낼 때 붙이고
    ///     <see cref="ClearStickyModifiers" /> 로 푼다 — Shift 같은 수식키만 눌렀을 때는 풀리지 않는다.
    /// </summary>
    public Func<KeyModifiers>? StickyModifiers { get; set; }

    /// <summary>고정 키를 푼다(입력을 보냈다).</summary>
    public Action? ClearStickyModifiers { get; set; }

    public bool HasSelection => _anchor is { } a && a != _caret;

    // ------------------------------------------------------------ 출력

    /// <summary>서버 출력(아무 스레드). 상태 질의 응답은 바로 <see cref="Input" /> 으로 보낸다.</summary>
    public void Write(ReadOnlySpan<char> text)
    {
        if (Buffer.Feed(text) is { } reply) Input?.Invoke(reply);
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 0)
            Dispatcher.UIThread.Post(Redraw, DispatcherPriority.Render);
    }

    /// <summary>동기화 출력을 끄는 신호가 이만큼 오지 않으면 그냥 그린다(xterm·kitty 와 비슷한 값).</summary>
    private const int SyncTimeoutMs = 200;

    private DispatcherTimer? _syncTimer;

    private void Redraw()
    {
        Interlocked.Exchange(ref _redrawQueued, 0);
        lock (Buffer.Gate)
        {
            // 동기화 출력 중 — 프로그램이 화면을 다 쓸 때까지 기다린다(그리는 중간 상태가 깜박이며 보이지 않게)
            var waited = Environment.TickCount64 - Buffer.SynchronizedSince;
            if (Buffer.SynchronizedOutput && waited < SyncTimeoutMs)
            {
                _syncTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(SyncTimeoutMs), DispatcherPriority.Render,
                    (_, _) =>
                    {
                        _syncTimer!.Stop();
                        Redraw();
                    });
                _syncTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, SyncTimeoutMs - waited));
                _syncTimer.Stop();
                _syncTimer.Start();
                return;
            }

            _syncTimer?.Stop();
            // 위로 올려 보던 중이면 새 줄만큼 따라 올려 같은 내용을 보이게 한다
            var scrolled = Buffer.ScrolledLines - _seenScrolled;
            _seenScrolled = Buffer.ScrolledLines;
            if (ScrollOffset > 0) ScrollOffset = (int)Math.Clamp(ScrollOffset + scrolled, 0, Buffer.ScrollbackCount);
            if (Buffer.IsAlternateScreen) ScrollOffset = 0;
        }

        UpdateBackground();
        PlaceInput();
        _surface.InvalidateVisual();
    }

    /// <summary>바탕 붓을 지금 기본 바탕색(OSC 11·화면 반전)에 맞춘다 — 칸 격자 밖 여백도 같은 색으로.</summary>
    private void UpdateBackground()
    {
        uint rgb;
        lock (Buffer.Gate)
            rgb = Buffer.ReverseVideo ? Buffer.DefaultForeground : Buffer.DefaultBackground;
        var color = Color.FromUInt32(0xFF000000 | rgb);
        if (_background.Color != color) _background.Color = color;
    }

    // ------------------------------------------------------------ 크기

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        FitToSize(finalSize);
        return size;
    }

    private void FitToSize(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;

        var columns = Math.Max(10, (int)((size.Width - Pad.Left - Pad.Right) / _renderer.CellWidth));
        var rows = Math.Max(2, (int)((size.Height - Pad.Top - Pad.Bottom) / _renderer.CellHeight));
        if (columns == Buffer.Columns && rows == Buffer.Rows) return;

        lock (Buffer.Gate)
        {
            // 위로 올려 보던 중이면 같은 내용이 맨 위에 오게 — 아직 반영 못 한 새 줄을 먼저 더하고, 다시 접은 뒤 기준을 새로 잡는다
            if (ScrollOffset > 0)
                ScrollOffset = (int)Math.Clamp(ScrollOffset + Buffer.ScrolledLines - _seenScrolled, 0, Buffer.ScrollbackCount);
            var offset = Buffer.Resize(columns, rows, ScrollOffset);
            ScrollOffset = Buffer.IsAlternateScreen ? 0 : offset;
            _seenScrolled = Buffer.ScrolledLines;
        }

        ClearSelection(); // 선택은 절대 줄 번호라 다시 접으면 어긋난다
        SizeChanged2?.Invoke(columns, rows);
        PlaceInput();
        _surface.InvalidateVisual();
    }

    private string? _fontFamily;

    /// <summary>설정의 터미널 글꼴(없거나 기본값이면 JetBrains Mono).</summary>
    public void SetFontFamily(string? family)
    {
        _fontFamily = family;
        _renderer = new TerminalRenderer(_renderer.FontSize, family);
        FitToSize(Bounds.Size);
        PlaceInput();
        _surface.InvalidateVisual();
    }

    public void SetFontSize(double size)
    {
        size = Math.Clamp(Math.Round(size * 2) / 2, MinFont, MaxFont);
        if (Math.Abs(size - _renderer.FontSize) < 0.01) return;

        _renderer = new TerminalRenderer(size, _fontFamily);
        FitToSize(Bounds.Size);
        PlaceInput();
        _surface.InvalidateVisual();
    }

    // ------------------------------------------------------------ 키 입력

    /// <summary>입력 칸에 포커스(모바일은 키보드가 올라온다).</summary>
    public void FocusInput()
    {
        Controls.SoftKeyboard.Show(_input, this); // 키보드가 닫혀 있어도 다시 연다
    }

    public bool IsInputFocused => _input.IsFocused;

    /// <summary>
    ///     터미널로 보낸다 — 아래로 되돌림, 선택 해제. 고정 키는 무엇을 보내든 풀리고, <paramref name="applySticky" /> 이면
    ///     글자 하나에만 붙는다(여러 글자 — 붙여넣기 등 — 에는 붙이지 않는다).
    /// </summary>
    public void Send(string text, bool applySticky = true)
    {
        if (text.Length == 0) return;

        var sticky = StickyModifiers?.Invoke() ?? KeyModifiers.None;
        if (sticky != KeyModifiers.None)
        {
            ClearStickyModifiers?.Invoke();
            if (applySticky && IsSingleRune(text))
                text = TerminalKeys.WithModifiers(text, sticky.HasFlag(KeyModifiers.Control),
                    sticky.HasFlag(KeyModifiers.Alt), sticky.HasFlag(KeyModifiers.Shift));
        }

        ScrollOffset = 0;
        if (HasSelection) ClearSelection();
        // Enter — 지금 화면의 입력 줄을 명령으로 알린다(최근 명령 기록). 편집기 등 대체 화면·여러 줄 붙여넣기·Shift+Enter 는 제외
        if (text.EndsWith('\r') && text.IndexOf('\r') == text.Length - 1 && !text.Contains('\x1b') &&
            !Buffer.IsAlternateScreen &&
            TerminalAssist.CommandFrom(TerminalAssist.CursorLine(Buffer).Text + text[..^1]) is { Length: > 0 } command)
            LineSubmitted?.Invoke(command);
        Input?.Invoke(text);
        RestartBlink();
        _surface.InvalidateVisual();
    }

    private static bool IsSingleRune(string text)
    {
        return System.Text.Rune.TryGetRuneAt(text, 0, out var rune) && rune.Utf16SequenceLength == text.Length;
    }

    /// <summary>특수 키 하나(특수 키 막대) — 고정한 Ctrl·Alt·Shift 는 수식키로 붙는다.</summary>
    public void SendKey(Key key)
    {
        var modifiers = StickyModifiers?.Invoke() ?? KeyModifiers.None;
        if (EncodeKey(key, modifiers) is { } text) Send(text, false);
    }

    private string? EncodeKey(Key key, KeyModifiers modifiers, string? keySymbol = null,
        PhysicalKey physical = PhysicalKey.None)
    {
        return TerminalKeys.Encode(key, modifiers, Buffer.ApplicationCursorKeys, Buffer.ApplicationKeypad,
            Buffer.NewLineMode, keySymbol, physical);
    }

    /// <summary>
    ///     붙여넣기 — 줄바꿈은 CR 로(셸이 빈 줄을 더 실행하지 않게). 괄호 붙여넣기 모드면 감싸고 제어 문자를 걸러
    ///     붙여넣은 글이 명령으로 해석되지 않게 한다. 아니면 터치 기기에서 여러 줄은 실행 전에 묻는다.
    /// </summary>
    public void Paste(string text)
    {
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (text.Length == 0) return;

        if (Buffer.BracketedPaste)
        {
            Send("\x1b[200~" + CleanPaste(text) + "\x1b[201~", false);
            return;
        }

        if (Platform.PlatformInfo.IsTouch && text.Contains('\r'))
        {
            _ = ConfirmPasteAsync(text);
            return;
        }

        Send(text, false);
    }

    /// <summary>괄호 붙여넣기 안의 글 — TAB·CR 말고 C0 제어 문자(ESC 포함)와 C1(0x80~0x9F)을 뺀다.</summary>
    public static string CleanPaste(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
            if (c is '\t' or '\r' || c >= ' ' && c is not (>= '\x80' and <= '\x9f'))
                sb.Append(c);
        return sb.ToString();
    }

    private async Task ConfirmPasteAsync(string text)
    {
        var lines = text.TrimEnd('\r').Split('\r').Length;
        var preview = text.Length > 200 ? text[..200] + "…" : text;
        if (await Services.AppServices.Dialogs.ConfirmAsync(Localization.L.T("Terminal_PasteConfirmTitle"),
                Localization.L.T("Terminal_PasteConfirm", lines, preview.TrimEnd('\r').Replace('\r', '\n')),
                Localization.L.T("Terminal_PasteConfirmRun")))
            Send(text, false);
        FocusInput();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        // 한글 등 IME 가 조합 중이면 IME 에 맡긴다 — 글자가 확정된 뒤 입력 칸으로 들어온다
        if (e.Key == Key.ImeProcessed ||
            !string.IsNullOrEmpty(_preedit) && (mods & (KeyModifiers.Control | KeyModifiers.Alt)) == 0)
            return;

        // 복사·붙여넣기 단축키(Ctrl+Shift+C/V, Ctrl+Insert, Shift+Insert)
        if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key is Key.C or Key.V ||
            mods == KeyModifiers.Control && e.Key == Key.Insert || mods == KeyModifiers.Shift && e.Key == Key.Insert)
        {
            _ = e.Key is Key.C or Key.Insert && mods.HasFlag(KeyModifiers.Control) ? CopyAsync() : PasteFromClipboardAsync();
            e.Handled = true;
            return;
        }

        // Shift+PgUp/PgDn — 스크롤백. 대체 화면은 스크롤백이 없으니 프로그램에 그대로 보낸다
        if (mods == KeyModifiers.Shift && e.Key is Key.PageUp or Key.PageDown && !Buffer.IsAlternateScreen)
        {
            ScrollBy(e.Key == Key.PageUp ? Buffer.Rows - 1 : -(Buffer.Rows - 1));
            e.Handled = true;
            return;
        }

        // 고정 키는 보기만 — 실제로 보낼 때(Send) 풀린다. Shift·Ctrl 만 누른 것은 아무것도 보내지 않으니 그대로 남는다
        mods |= StickyModifiers?.Invoke() ?? KeyModifiers.None;
        if (EncodeKey(e.Key, mods, e.KeySymbol, e.PhysicalKey) is not { } text) return;

        Send(text, false);
        e.Handled = true;
    }

    /// <summary>한 번에 이보다 길게 들어온 글은 붙여넣기로 본다.</summary>
    private const int PasteThreshold = 32;

    /// <summary>
    ///     입력 칸에 들어온 글자(소프트 키보드·IME 확정). 줄바꿈이 섞였거나 긴 글은 붙여넣기로(Gboard 클립보드·길게 눌러
    ///     붙여넣기·Cmd+V) — 괄호 붙여넣기가 적용된다. 끝의 줄바꿈 하나는 Enter 로 본다.
    /// </summary>
    private void OnInputText(object? sender, TextChangedEventArgs e)
    {
        var text = _input.Text;
        if (string.IsNullOrEmpty(text)) return;

        _input.Text = string.Empty;
        var body = text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2]
            : text.EndsWith('\n') || text.EndsWith('\r') ? text[..^1] : text;
        var enter = body.Length < text.Length;
        if (body.Contains('\n') || body.Contains('\r') || body.Length > PasteThreshold)
        {
            Paste(text);
            return;
        }

        Send(enter ? body + (Buffer.NewLineMode ? "\r\n" : "\r") : body);
    }

    private void OnInputFocusChanged()
    {
        _surface.InvalidateVisual();
        if (_focusReportQueued) return;

        // 키보드를 다시 열 때(SoftKeyboard.Show) 포커스가 잠깐 나갔다 돌아온다 — 정리된 뒤의 상태만 알린다
        _focusReportQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _focusReportQueued = false;
            var focused = _input.IsFocused;
            if (focused == _focusReported) return;

            _focusReported = focused;
            if (Buffer.FocusReporting) Input?.Invoke(focused ? "\x1b[I" : "\x1b[O");
        }, DispatcherPriority.Background);
    }

    /// <summary>입력 칸의 조합 중 글자(한글 IME)를 지켜보고 커서 자리에 그린다.</summary>
    private void WatchPreedit()
    {
        if (_input.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is not { } presenter) return;

        presenter.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextPresenter.PreeditTextProperty) return;

            _preedit = e.NewValue as string;
            _surface.InvalidateVisual();
        };
    }

    /// <summary>입력 칸을 커서 자리로 — IME 후보 창이 커서 옆에 뜬다.</summary>
    private void PlaceInput()
    {
        _input.Margin = new Thickness(Pad.Left + Buffer.CursorX * _renderer.CellWidth,
            Pad.Top + Buffer.CursorY * _renderer.CellHeight, 0, 0);
    }

    // ------------------------------------------------------------ 복사·붙여넣기

    public string GetSelectedText()
    {
        if (_anchor is not { } a || a == _caret) return string.Empty;

        var (start, end) = Snapped(a, _caret);
        return Buffer.GetText(start.Row, start.Col, end.Row, end.Col);
    }

    public async Task<bool> CopyAsync()
    {
        var text = GetSelectedText();
        if (text.Length == 0) return false;

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
        ClearSelection();
        return true;
    }

    public async Task PasteFromClipboardAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        var text = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard);
        if (!string.IsNullOrEmpty(text)) Paste(text);
    }

    public void ClearSelection()
    {
        var had = HasSelection;
        _anchor = null;
        _selecting = false;
        _touchSelecting = false;
        _touchHandles = false;
        if (had) SelectionChanged?.Invoke(false);
        _surface.InvalidateVisual();
    }

    public void SelectAll()
    {
        lock (Buffer.Gate)
        {
            _anchor = (Buffer.DroppedLines, 0);
            _caret = (Buffer.AbsoluteRow(Buffer.Rows - 1, 0), Buffer.Columns - 1);
        }

        SelectionChanged?.Invoke(true);
        _surface.InvalidateVisual();
    }

    private static ((long Row, int Col) Start, (long Row, int Col) End) Ordered((long Row, int Col) a,
        (long Row, int Col) b)
    {
        return a.Row < b.Row || a.Row == b.Row && a.Col <= b.Col ? (a, b) : (b, a);
    }

    /// <summary>
    ///     차례로 놓고 넓은 글자에 맞춘 선택 — 시작이 넓은 글자의 뒤 칸이면 앞 칸으로, 끝이 넓은 글자의 앞 칸이면 뒤 칸까지
    ///     (글자 반쪽만 칠하거나 복사에서 빠지지 않게).
    /// </summary>
    private ((long Row, int Col) Start, (long Row, int Col) End) Snapped((long Row, int Col) a, (long Row, int Col) b)
    {
        var (start, end) = Ordered(a, b);
        lock (Buffer.Gate)
        {
            if (Buffer.GetAbsoluteLine(start.Row) is { } first && start.Col > 0 && start.Col < first.Cells.Length &&
                first.Cells[start.Col].Width == 0)
                start.Col--;
            if (Buffer.GetAbsoluteLine(end.Row) is { } last && end.Col + 1 < last.Cells.Length &&
                last.Cells[end.Col].Width == 2)
                end.Col++;
        }

        return (start, end);
    }

    private (long Row, int Col) CellAt(Point p)
    {
        var col = (int)Math.Clamp((p.X - Pad.Left) / _renderer.CellWidth, 0, Buffer.Columns - 1);
        var row = (int)Math.Clamp((p.Y - Pad.Top) / _renderer.CellHeight, 0, Buffer.Rows - 1);
        lock (Buffer.Gate)
        {
            return (Buffer.AbsoluteRow(row, ScrollOffset), col);
        }
    }

    private (int Col, int Row) ScreenCellAt(Point p)
    {
        return ((int)Math.Clamp((p.X - Pad.Left) / _renderer.CellWidth, 0, Buffer.Columns - 1),
            (int)Math.Clamp((p.Y - Pad.Top) / _renderer.CellHeight, 0, Buffer.Rows - 1));
    }

    /// <summary>단어(공백·구분 기호 사이) 또는 줄 전체를 고른다.</summary>
    private void SelectWord((long Row, int Col) at, bool wholeLine)
    {
        lock (Buffer.Gate)
        {
            if (Buffer.GetAbsoluteLine(at.Row) is not { } line) return;

            var cells = line.Cells;
            if (wholeLine)
            {
                _anchor = (at.Row, 0);
                _caret = (at.Row, cells.Length - 1);
                return;
            }

            static bool IsWord(TerminalCell c) => c.Width == 0 || c.Rune != 0 && c.Rune != ' ' &&
                (char.IsLetterOrDigit((char)Math.Min(c.Rune, 0xFFFF)) || c.Rune > 0x7F || "-_./~:@%+=".Contains((char)c.Rune));

            var col = Math.Min(at.Col, cells.Length - 1);
            if (!IsWord(cells[col]))
            {
                _anchor = _caret = (at.Row, col);
                return;
            }

            var start = col;
            var end = col;
            while (start > 0 && IsWord(cells[start - 1])) start--;
            while (end + 1 < cells.Length && IsWord(cells[end + 1])) end++;
            _anchor = (at.Row, start);
            _caret = (at.Row, end);
        }
    }

    // ------------------------------------------------------------ 스크롤

    private void ScrollBy(int lines)
    {
        if (Buffer.IsAlternateScreen) return;

        var max = Buffer.ScrollbackCount;
        var next = Math.Clamp(ScrollOffset + lines, 0, max);
        if (next == ScrollOffset) return;

        ScrollOffset = next;
        _surface.InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;

        if (Buffer.MouseTracking != MouseTracking.None && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // 한 칸(노치)에 보고 하나. 터치패드의 잘게 나뉜 값은 모아서 한 칸이 되면 보낸다
            _wheelY += e.Delta.Y;
            _wheelX += e.Delta.X;
            var notchesY = (int)Math.Truncate(_wheelY);
            var notchesX = (int)Math.Truncate(_wheelX);
            _wheelY -= notchesY;
            _wheelX -= notchesX;
            var (col, row) = ScreenCellAt(e.GetPosition(this));
            for (var i = 0; i < Math.Abs(notchesY); i++)
                Input?.Invoke(TerminalKeys.Mouse(notchesY > 0 ? 64 : 65, col, row, false, Buffer.SgrMouse, e.KeyModifiers));
            for (var i = 0; i < Math.Abs(notchesX); i++)
                Input?.Invoke(TerminalKeys.Mouse(notchesX > 0 ? 66 : 67, col, row, false, Buffer.SgrMouse, e.KeyModifiers));
            return;
        }

        // 화면 스크롤 — 한 칸에 3줄
        _wheelLines += e.Delta.Y * 3;
        var lines = (int)Math.Truncate(_wheelLines);
        _wheelLines -= lines;
        if (lines == 0) return;

        if (Buffer.IsAlternateScreen) SendScrollArrows(lines);
        else ScrollBy(lines);
    }

    /// <summary>대체 화면(less·vim 등)은 스크롤백이 없다 — 방향키로 보낸다(xterm alternateScroll, ?1007 로 끌 수 있다).</summary>
    private void SendScrollArrows(int lines)
    {
        if (!Buffer.AlternateScroll) return;

        var text = EncodeKey(lines > 0 ? Key.Up : Key.Down, KeyModifiers.None)!;
        Input?.Invoke(string.Concat(Enumerable.Repeat(text, Math.Abs(lines))));
    }

    // ------------------------------------------------------------ 마우스·터치

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var pos = point.Position;

        if (e.Pointer.Type == PointerType.Touch)
        {
            _touches[e.Pointer] = pos;
            if (_touches.Count == 2)
            {
                _longPress.Stop();
                var pts = _touches.Values.ToArray();
                _pinchDistance = Distance(pts[0], pts[1]);
                _pinchFont = _renderer.FontSize;
            }
            else if (_touches.Count == 1 && GrabHandle(pos))
            {
                // 선택 손잡이를 잡았다 — 반대쪽 끝을 고정하고 이 끝을 끈다
                _touchStart = _touchPos = pos;
                _touchStartTime = DateTime.UtcNow;
                _touchMoved = false;
                _touchSelecting = true;
                _surface.InvalidateVisual();
            }
            else if (_touches.Count == 1)
            {
                _touchStart = pos;
                _touchStartTime = DateTime.UtcNow;
                _touchMoved = false;
                _scrollRemainder = 0;
                if (!_touchSelecting) _longPress.Start();
            }

            e.Handled = true;
            return;
        }

        FocusInput();
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var kind = point.Properties.PointerUpdateKind;

        if (Buffer.MouseTracking != MouseTracking.None && !shift)
        {
            var button = kind switch
            {
                PointerUpdateKind.LeftButtonPressed => 0, PointerUpdateKind.MiddleButtonPressed => 1,
                PointerUpdateKind.RightButtonPressed => 2, _ => -1
            };
            if (button < 0) return;

            _mouseButtons |= 1 << button;
            var (col, row) = _lastMouseCell = ScreenCellAt(pos);
            Input?.Invoke(TerminalKeys.Mouse(button, col, row, false, Buffer.SgrMouse, e.KeyModifiers));
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (kind == PointerUpdateKind.RightButtonPressed)
        {
            _ = HasSelection ? CopyAsync() : PasteFromClipboardAsync();
            e.Handled = true;
            return;
        }

        if (kind != PointerUpdateKind.LeftButtonPressed) return;

        var cell = CellAt(pos);
        if (e.ClickCount >= 2)
        {
            SelectWord(cell, e.ClickCount >= 3);
            SelectionChanged?.Invoke(HasSelection);
        }
        else if (shift && _anchor is not null)
        {
            _caret = cell;
            SelectionChanged?.Invoke(HasSelection);
        }
        else
        {
            var had = HasSelection;
            _anchor = _caret = cell;
            _selecting = true;
            _touchHandles = false;
            if (had) SelectionChanged?.Invoke(false);
        }

        e.Pointer.Capture(this);
        _surface.InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (e.Pointer.Type == PointerType.Touch)
        {
            if (!_touches.ContainsKey(e.Pointer)) return;

            _touches[e.Pointer] = pos;
            if (_touches.Count >= 2)
            {
                var pts = _touches.Values.ToArray();
                if (_pinchDistance > 10) SetFontSize(_pinchFont * Distance(pts[0], pts[1]) / _pinchDistance);
                _touchMoved = true;
                return;
            }

            if (_touchSelecting)
            {
                _touchPos = pos;
                _touchMoved = true;
                _caret = CellAt(pos);
                _surface.InvalidateVisual();
                return;
            }

            var dy = pos.Y - _touchStart.Y;
            if (!_touchMoved && Distance(pos, _touchStart) < 10) return;

            _touchMoved = true;
            _longPress.Stop();
            _scrollRemainder += dy / _renderer.CellHeight;
            _touchStart = pos;
            var lines = (int)_scrollRemainder;
            if (lines == 0) return;

            _scrollRemainder -= lines;
            if (Buffer.MouseTracking != MouseTracking.None)
            {
                // 프로그램이 마우스를 받는다(Claude Code 전체 화면·htop·mc) — 휠로 보내 그 프로그램이 스크롤하게
                var (col, row) = ScreenCellAt(pos);
                for (var i = 0; i < Math.Abs(lines); i++)
                    Input?.Invoke(TerminalKeys.Mouse(lines > 0 ? 64 : 65, col, row, false, Buffer.SgrMouse));
            }
            else if (Buffer.IsAlternateScreen)
            {
                SendScrollArrows(lines);
            }
            else
            {
                ScrollBy(lines);
            }

            return;
        }

        if (_mouseButtons != 0)
        {
            var cell = ScreenCellAt(pos);
            // 이미 단추를 누른 채 다른 단추를 누르거나 떼면 Avalonia 는 PointerMoved 로 알린다
            var (changed, released) = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.LeftButtonPressed => (0, false), PointerUpdateKind.MiddleButtonPressed => (1, false),
                PointerUpdateKind.RightButtonPressed => (2, false), PointerUpdateKind.LeftButtonReleased => (0, true),
                PointerUpdateKind.MiddleButtonReleased => (1, true), PointerUpdateKind.RightButtonReleased => (2, true),
                _ => (-1, false)
            };
            if (changed >= 0)
            {
                if (released) _mouseButtons &= ~(1 << changed);
                else _mouseButtons |= 1 << changed;
                _lastMouseCell = cell;
                Input?.Invoke(TerminalKeys.Mouse(changed, cell.Col, cell.Row, released, Buffer.SgrMouse, e.KeyModifiers));
                if (_mouseButtons == 0) e.Pointer.Capture(null);
                return;
            }

            // 끌기 보고(?1002·?1003) — 칸이 바뀔 때만. 단추가 여럿이면 번호가 낮은 것(xterm)
            if (Buffer.MouseTracking is MouseTracking.Drag or MouseTracking.Any && cell != _lastMouseCell)
            {
                _lastMouseCell = cell;
                var lowest = System.Numerics.BitOperations.TrailingZeroCount(_mouseButtons);
                Input?.Invoke(TerminalKeys.Mouse(lowest + 32, cell.Col, cell.Row, false, Buffer.SgrMouse, e.KeyModifiers));
            }

            return;
        }

        if (Buffer.MouseTracking == MouseTracking.Any && !_selecting && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // ?1003 — 단추 없이 움직여도 알린다(35), 칸이 바뀔 때만
            var cell = ScreenCellAt(pos);
            if (cell == _lastMouseCell) return;

            _lastMouseCell = cell;
            Input?.Invoke(TerminalKeys.Mouse(35, cell.Col, cell.Row, false, Buffer.SgrMouse, e.KeyModifiers));
            return;
        }

        if (!_selecting) return;

        _caret = CellAt(pos);
        _surface.InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pos = e.GetPosition(this);

        if (e.Pointer.Type == PointerType.Touch)
        {
            _longPress.Stop();
            var wasPinch = _touches.Count >= 2;
            _touches.Remove(e.Pointer);
            if (wasPinch || _touches.Count > 0) return;

            if (_touchSelecting)
            {
                _touchSelecting = false;
                SelectionChanged?.Invoke(HasSelection);
                _surface.InvalidateVisual(); // 확대경을 지운다
                return;
            }

            if (_touchMoved) return;

            // 타이머가 늦었어도(바쁜 UI 스레드) 오래 눌렀으면 길게 누르기
            if (DateTime.UtcNow - _touchStartTime >= _longPress.Interval)
            {
                SelectWord(CellAt(_touchStart), false);
                _touchHandles = true;
                SelectionChanged?.Invoke(HasSelection);
                _surface.InvalidateVisual();
                return;
            }

            // 탭: 선택 해제 · 키보드 · (마우스 보고 중이면) 클릭
            if (HasSelection) ClearSelection();
            if (Buffer.MouseTracking != MouseTracking.None)
            {
                var (col, row) = ScreenCellAt(pos);
                Input?.Invoke(TerminalKeys.Mouse(0, col, row, false, Buffer.SgrMouse));
                Input?.Invoke(TerminalKeys.Mouse(0, col, row, true, Buffer.SgrMouse));
            }

            FocusInput();
            return;
        }

        if (_mouseButtons != 0)
        {
            var released = e.InitialPressMouseButton switch
            {
                MouseButton.Left => 0, MouseButton.Middle => 1, MouseButton.Right => 2, _ => -1
            };
            if (released < 0 || (_mouseButtons & (1 << released)) == 0)
                released = System.Numerics.BitOperations.TrailingZeroCount(_mouseButtons);
            _mouseButtons &= ~(1 << released);
            var (col, row) = _lastMouseCell = ScreenCellAt(pos);
            Input?.Invoke(TerminalKeys.Mouse(released, col, row, true, Buffer.SgrMouse, e.KeyModifiers));
            if (_mouseButtons == 0) e.Pointer.Capture(null);
            return;
        }

        if (!_selecting) return;

        _selecting = false;
        e.Pointer.Capture(null);
        if (_anchor == _caret) _anchor = null; // 그냥 누른 것 — 선택 아님
        SelectionChanged?.Invoke(HasSelection);
        _surface.InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_mouseButtons == 0) _lastMouseCell = (-1, -1);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _touches.Remove(e.Pointer);
        _longPress.Stop();
    }

    private void OnLongPress()
    {
        _longPress.Stop();
        if (_touchMoved || _touches.Count != 1) return;

        SelectWord(CellAt(_touchStart), false);
        _touchSelecting = true;
        _touchHandles = true;
        _touchPos = _touchStart;
        _surface.InvalidateVisual();
    }

    // ------------------------------------------------------------ 손가락 선택 — 손잡이·확대경

    private const double HandleRadius = 9;
    private const double HandleGrab = 30; // 손잡이를 잡았다고 보는 거리(손가락은 크다)

    /// <summary>선택 끝(시작이면 그 칸 왼쪽, 끝이면 오른쪽)의 화면 위치 — 칸 아래쪽. 화면 밖이면 null.</summary>
    private Point? EdgePoint((long Row, int Col) cell, bool isEnd)
    {
        var top = Buffer.AbsoluteRow(0, Math.Min(ScrollOffset, Buffer.ScrollbackCount));
        var row = cell.Row - top;
        if (row < 0 || row >= Buffer.Rows) return null;

        var x = Pad.Left + (cell.Col + (isEnd ? 1 : 0)) * _renderer.CellWidth;
        return new Point(Math.Round(x), Pad.Top + (row + 1) * _renderer.CellHeight);
    }

    /// <summary>손잡이 동그라미 가운데 — 칸 아래, 아래에 자리가 없으면(맨 아랫줄) 칸 위로 뒤집는다.</summary>
    private Point KnobCenter(Point edge, bool isEnd)
    {
        var x = Math.Clamp(edge.X + (isEnd ? HandleRadius : -HandleRadius), HandleRadius,
            Math.Max(HandleRadius, _surface.Bounds.Width - HandleRadius)); // 화면 끝에서 잘리지 않게
        var below = edge.Y + HandleRadius * 2 <= _surface.Bounds.Height;
        return new Point(x, below ? edge.Y + HandleRadius : edge.Y - _renderer.CellHeight - HandleRadius);
    }

    /// <summary>칸 크기(검사·도우미 배치용).</summary>
    public Size CellSize => new(_renderer.CellWidth, _renderer.CellHeight);

    /// <summary>손잡이 근처를 눌렀으면 반대쪽 끝을 고정(<see cref="_anchor" />)하고 true.</summary>
    private bool GrabHandle(Point pos)
    {
        if (!_touchHandles || _anchor is not { } a || a == _caret) return false;

        lock (Buffer.Gate)
        {
            var (start, end) = Ordered(a, _caret);
            var startKnob = EdgePoint(start, false) is { } s ? KnobCenter(s, false) : (Point?)null;
            var endKnob = EdgePoint(end, true) is { } e ? KnobCenter(e, true) : (Point?)null;
            var toStart = startKnob is { } sk ? Distance(pos, sk) : double.MaxValue;
            var toEnd = endKnob is { } ek ? Distance(pos, ek) : double.MaxValue;
            if (Math.Min(toStart, toEnd) > HandleGrab) return false;

            if (toEnd <= toStart)
            {
                _anchor = start;
                _caret = end;
            }
            else
            {
                _anchor = end;
                _caret = start;
            }

            return true;
        }
    }

    /// <summary>선택 양 끝 손잡이 — 끝 칸 아래의 세로 막대와 동그라미(Android 텍스트 선택처럼).</summary>
    private void DrawHandles(DrawingContext context)
    {
        if (!_touchHandles || _anchor is not { } a || a == _caret) return;

        var (start, end) = Snapped(a, _caret);
        var brush = _renderer.Brush(0x3B78FF);
        foreach (var (cell, isEnd) in new[] { (start, false), (end, true) })
        {
            if (EdgePoint(cell, isEnd) is not { } p) continue;

            context.FillRectangle(brush, new Rect(p.X - 1, p.Y - _renderer.CellHeight, 2, _renderer.CellHeight));
            var knob = KnobCenter(p, isEnd);
            context.DrawEllipse(brush, null, knob, HandleRadius, HandleRadius);
            // 막대에 붙은 쪽 모서리를 채워 물방울 모양으로
            var cornerY = knob.Y > p.Y ? p.Y : knob.Y;
            context.FillRectangle(brush, new Rect(isEnd ? p.X : p.X - HandleRadius, cornerY, HandleRadius, HandleRadius));
        }
    }

    /// <summary>
    ///     끄는 동안 손가락 위에 확대경 — 손가락에 가린 칸과 선택 끝을 1.6배로 보인다. 손가락 바로 위가 화면 위쪽 끝이면
    ///     손가락 아래에 둔다.
    /// </summary>
    private void DrawLoupe(DrawingContext context, int offset)
    {
        if (!_touchSelecting) return;

        const double zoom = 1.6;
        var r = _renderer;
        var bounds = _surface.Bounds;
        var w = Math.Min(bounds.Width - 16, Math.Round(r.CellWidth * 14 * zoom));
        var h = Math.Round(r.CellHeight * 2.2 * zoom);
        var x = Math.Clamp(_touchPos.X - w / 2, 8, Math.Max(8, bounds.Width - w - 8));
        var y = _touchPos.Y - 56 - h;
        if (y < 4) y = _touchPos.Y + 56;
        var frame = new Rect(x, y, w, h);

        // 확대할 곳: 손가락이 가리키는 칸의 가운데
        var (col, row) = ScreenCellAt(_touchPos);
        var focus = new Point(Pad.Left + (col + 0.5) * r.CellWidth, Pad.Top + (row + 0.5) * r.CellHeight);

        context.DrawRectangle(r.Brush(0x000000, 0x80), null, frame.Translate(new Vector(0, 2)).Inflate(1), 10, 10);
        using (context.PushClip(new RoundedRect(frame, 10)))
        {
            context.FillRectangle(r.Brush(r.ScreenBackground), frame);
            var matrix = Matrix.CreateTranslation(-focus.X, -focus.Y) * Matrix.CreateScale(zoom, zoom) *
                         Matrix.CreateTranslation(frame.Center.X, frame.Center.Y);
            using (context.PushTransform(matrix))
            {
                for (var i = Math.Max(0, row - 2); i <= Math.Min(Buffer.Rows - 1, row + 2); i++)
                    r.DrawLine(context, Buffer.GetVisibleLine(i, offset), Buffer.Columns,
                        new Point(Pad.Left, Pad.Top + i * r.CellHeight));
                DrawSelection(context, offset);
                // 지금 끄는 끝을 막대로
                var caretEnd = _anchor is { } a && Ordered(a, _caret).End == _caret;
                if (EdgePoint(_caret, caretEnd) is { } p)
                    context.FillRectangle(r.Brush(0x3B78FF), new Rect(p.X - 1, p.Y - r.CellHeight, 2, r.CellHeight));
            }
        }

        context.DrawRectangle(null, new Pen(r.Brush(0xFFFFFF, 0x60)), frame, 10, 10);
    }

    private static double Distance(Point a, Point b)
    {
        return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    // ------------------------------------------------------------ 그리기

    /// <summary>화면을 그린 횟수(검사용 — 동기화 출력 중에 그리지 않는지 본다).</summary>
    public long FramesDrawn { get; private set; }

    private void RenderTerminal(DrawingContext context)
    {
        FramesDrawn++;
        var r = _renderer;
        var bounds = _surface.Bounds;

        lock (Buffer.Gate)
        {
            r.BeginFrame(Buffer);
            r.BlinkOff = !_blinkOn;
            context.FillRectangle(r.Brush(r.ScreenBackground), new Rect(bounds.Size));
            var rows = Buffer.Rows;
            var columns = Buffer.Columns;
            var offset = Math.Min(ScrollOffset, Buffer.ScrollbackCount);
            for (var row = 0; row < rows; row++)
            {
                var line = Buffer.GetVisibleLine(row, offset);
                r.DrawLine(context, line, columns, new Point(Pad.Left, Pad.Top + row * r.CellHeight));
            }

            DrawSelection(context, offset);
            if (offset == 0) DrawCursor(context);
            DrawHandles(context);
            DrawLoupe(context, offset);
            UpdateBlinkTimer(r.DrewBlink || offset == 0 && CursorBlinks);
        }

        if (ScrollOffset > 0) DrawScrollIndicator(context);
    }

    // ------------------------------------------------------------ 깜박임

    /// <summary>깜박임 주기 — xterm·VTE 와 같은 값.</summary>
    private const int BlinkMs = 530;

    private DispatcherTimer? _blinkTimer;
    private bool _blinkOn = true;

    /// <summary>DECSCUSR 홀수(1 블록·3 밑줄·5 막대)는 깜박이는 커서 — 기본(0)은 깜박이지 않는다.</summary>
    private bool CursorBlinks => Buffer.CursorStyle is 1 or 3 or 5 && Buffer.CursorVisible && _input.IsFocused;

    /// <summary>깜박일 것(깜박이는 글자·커서)이 화면에 있을 때만 시계를 돌린다.</summary>
    private void UpdateBlinkTimer(bool needed)
    {
        if (needed)
        {
            if (_blinkTimer is null)
            {
                _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BlinkMs) };
                _blinkTimer.Tick += (_, _) =>
                {
                    _blinkOn = !_blinkOn;
                    _surface.InvalidateVisual();
                };
            }

            if (!_blinkTimer.IsEnabled) _blinkTimer.Start();
            return;
        }

        if (_blinkTimer is not { IsEnabled: true }) return;

        _blinkTimer.Stop();
        _blinkOn = true;
    }

    /// <summary>입력하면 깜박임을 켜진 때부터 다시 — 치는 동안 커서가 사라져 보이지 않게.</summary>
    private void RestartBlink()
    {
        _blinkOn = true;
        if (_blinkTimer is not { IsEnabled: true }) return;

        _blinkTimer.Stop();
        _blinkTimer.Start();
    }

    private void DrawSelection(DrawingContext context, int offset)
    {
        if (_anchor is not { } a || a == _caret) return;

        var r = _renderer;
        var (start, end) = Snapped(a, _caret);
        var brush = r.Brush(0x3B78FF, 0x70);
        for (var row = 0; row < Buffer.Rows; row++)
        {
            var abs = Buffer.AbsoluteRow(row, offset);
            if (abs < start.Row || abs > end.Row) continue;

            var from = abs == start.Row ? start.Col : 0;
            var to = abs == end.Row ? end.Col : Buffer.Columns - 1;
            var x0 = Math.Round(Pad.Left + from * r.CellWidth);
            var x1 = Math.Round(Pad.Left + (to + 1) * r.CellWidth);
            context.FillRectangle(brush, new Rect(x0, Pad.Top + row * r.CellHeight, x1 - x0, r.CellHeight));
        }
    }

    private void DrawCursor(DrawingContext context)
    {
        var r = _renderer;
        var x = Buffer.CursorX;
        var y = Buffer.CursorY;
        var origin = new Point(Math.Round(Pad.Left + x * r.CellWidth), Pad.Top + y * r.CellHeight);
        var line = Buffer.GetVisibleLine(y, 0);
        var cell = line.Cells[Math.Min(x, line.Cells.Length - 1)];
        var width = Math.Max((int)cell.Width, 1);
        var span = Math.Round(r.CellWidth * width);
        var (fg, bg, _) = r.Colors(cell);
        var cursorColor = Buffer.CursorColor ?? (fg == bg ? r.ScreenForeground : fg);

        // 조합 중 글자(한글 IME)를 커서 자리에 밑줄과 함께
        if (!string.IsNullOrEmpty(_preedit))
        {
            var px = origin.X;
            foreach (var rune in _preedit.EnumerateRunes())
            {
                var w = Math.Max(1, CharWidth.Of(rune.Value));
                var rect = new Rect(px, origin.Y, Math.Round(r.CellWidth * w), r.CellHeight);
                context.FillRectangle(r.Brush(0x264F78), rect);
                r.DrawSingle(context, rune.Value, w, false, r.Brush(0xFFFFFF), new Point(px, origin.Y));
                context.FillRectangle(r.Brush(0xFFFFFF), new Rect(px, rect.Bottom - 2, rect.Width, 2));
                px += rect.Width;
            }

            return;
        }

        if (!Buffer.CursorVisible) return;
        if (CursorBlinks && !_blinkOn) return;

        var brush = r.Brush(cursorColor);
        if (!_input.IsFocused)
        {
            var pen = new Pen(brush);
            context.DrawRectangle(null, pen, new Rect(origin.X + 0.5, origin.Y + 0.5, span - 1, r.CellHeight - 1));
            return;
        }

        switch (Buffer.CursorStyle)
        {
            case 3 or 4:
                context.FillRectangle(brush, new Rect(origin.X, origin.Y + r.CellHeight - 2, span, 2));
                return;
            case 5 or 6:
                context.FillRectangle(brush, new Rect(origin.X, origin.Y, 2, r.CellHeight));
                return;
        }

        context.FillRectangle(brush, new Rect(origin.X, origin.Y, span, r.CellHeight));
        if (cell.Rune is not (0 or ' ') && cell.Width > 0)
            r.DrawSingle(context, cell.Rune, width, cell.Flags.HasFlag(CellFlags.Bold), r.Brush(bg), origin,
                cell.Flags.HasFlag(CellFlags.Italic));
    }

    /// <summary>스크롤백을 보는 중 — 오른쪽에 위치 막대.</summary>
    private void DrawScrollIndicator(DrawingContext context)
    {
        var total = Buffer.ScrollbackCount + Buffer.Rows;
        var h = _surface.Bounds.Height;
        var thumb = Math.Max(24, h * Buffer.Rows / total);
        var top = (h - thumb) * (1 - (double)ScrollOffset / Math.Max(1, Buffer.ScrollbackCount));
        context.FillRectangle(_renderer.Brush(0xFFFFFF, 0x66), new Rect(_surface.Bounds.Width - 5, top, 3, thumb));
    }

    /// <summary>그리기 전용 — 입력 칸 아래에 깔린다.</summary>
    private sealed class Surface(TerminalView owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            owner.RenderTerminal(context);
        }
    }
}
