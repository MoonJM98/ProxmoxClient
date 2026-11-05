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
    private double _pinchDistance;
    private double _pinchFont;
    private int _mouseButton = -1;

    public TerminalView(double fontSize = 14)
    {
        Buffer = new TerminalBuffer(80, 24);
        _renderer = new TerminalRenderer(fontSize);
        Background = new SolidColorBrush(Color.FromUInt32(0xFF000000 | TerminalRenderer.DefaultBackground));
        ClipToBounds = true;
        Focusable = false;

        _surface = new Surface(this);
        _input = new TextBox
        {
            Width = 2, Height = 2, Opacity = 0.01, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), AcceptsReturn = false, AcceptsTab = true,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Text = string.Empty
        };
        TextInputOptions.SetAutoCapitalization(_input, false);
        TextInputOptions.SetShowSuggestions(_input, false);
        TextInputOptions.SetMultiline(_input, false);
        _input.TextChanged += OnInputText;
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _input.GotFocus += (_, _) => _surface.InvalidateVisual();
        _input.LostFocus += (_, _) => _surface.InvalidateVisual();
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

    /// <summary>선택이 생기거나 없어졌다.</summary>
    public event Action<bool>? SelectionChanged;

    /// <summary>스크롤백을 거슬러 본 줄 수(0 = 맨 아래, 새 출력이 보인다).</summary>
    public int ScrollOffset { get; private set; }

    public double TerminalFontSize => _renderer.FontSize;

    /// <summary>Ctrl·Alt 를 한 번 적용(특수 키 막대의 고정 키) — 다음 입력에 붙이고 풀린다.</summary>
    public Func<(bool Ctrl, bool Alt)>? TakeStickyModifiers { get; set; }

    public bool HasSelection => _anchor is { } a && a != _caret;

    // ------------------------------------------------------------ 출력

    /// <summary>서버 출력(아무 스레드). 상태 질의 응답은 바로 <see cref="Input" /> 으로 보낸다.</summary>
    public void Write(ReadOnlySpan<char> text)
    {
        if (Buffer.Feed(text) is { } reply) Input?.Invoke(reply);
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 0)
            Dispatcher.UIThread.Post(Redraw, DispatcherPriority.Render);
    }

    private void Redraw()
    {
        Interlocked.Exchange(ref _redrawQueued, 0);
        lock (Buffer.Gate)
        {
            // 위로 올려 보던 중이면 새 줄만큼 따라 올려 같은 내용을 보이게 한다
            var scrolled = Buffer.ScrolledLines - _seenScrolled;
            _seenScrolled = Buffer.ScrolledLines;
            if (ScrollOffset > 0) ScrollOffset = (int)Math.Clamp(ScrollOffset + scrolled, 0, Buffer.ScrollbackCount);
            if (Buffer.IsAlternateScreen) ScrollOffset = 0;
        }

        PlaceInput();
        _surface.InvalidateVisual();
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

        Buffer.Resize(columns, rows);
        ClearSelection();
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
        _input.Focus();
    }

    public bool IsInputFocused => _input.IsFocused;

    /// <summary>터미널로 보낸다 — 고정 키 적용, 아래로 되돌림, 선택 해제.</summary>
    public void Send(string text, bool applySticky = true)
    {
        if (text.Length == 0) return;

        if (applySticky && TakeStickyModifiers?.Invoke() is { } sticky && (sticky.Ctrl || sticky.Alt))
            text = TerminalKeys.WithModifiers(text, sticky.Ctrl, sticky.Alt);
        ScrollOffset = 0;
        if (HasSelection) ClearSelection();
        Input?.Invoke(text);
        _surface.InvalidateVisual();
    }

    /// <summary>특수 키 하나(특수 키 막대) — 고정한 Ctrl·Alt 는 수식키로 붙는다.</summary>
    public void SendKey(Key key)
    {
        var sticky = TakeStickyModifiers?.Invoke() ?? (false, false);
        var modifiers = (sticky.Ctrl ? KeyModifiers.Control : 0) | (sticky.Alt ? KeyModifiers.Alt : 0);
        if (TerminalKeys.Encode(key, modifiers, Buffer.ApplicationCursorKeys) is { } text) Send(text, false);
    }

    /// <summary>붙여넣기 — 줄바꿈은 CR 로(셸이 빈 줄을 더 실행하지 않게), 괄호 붙여넣기 모드면 감싼다.</summary>
    public void Paste(string text)
    {
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (Buffer.BracketedPaste) text = "\x1b[200~" + text.Replace("\x1b", "") + "\x1b[201~";
        Send(text, false);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        // 복사·붙여넣기 단축키(Ctrl+Shift+C/V, Ctrl+Insert, Shift+Insert)
        if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key is Key.C or Key.V ||
            mods == KeyModifiers.Control && e.Key == Key.Insert || mods == KeyModifiers.Shift && e.Key == Key.Insert)
        {
            _ = e.Key is Key.C or Key.Insert && mods.HasFlag(KeyModifiers.Control) ? CopyAsync() : PasteFromClipboardAsync();
            e.Handled = true;
            return;
        }

        if (mods == KeyModifiers.Shift && e.Key is Key.PageUp or Key.PageDown)
        {
            ScrollBy(e.Key == Key.PageUp ? Buffer.Rows - 1 : -(Buffer.Rows - 1));
            e.Handled = true;
            return;
        }

        var sticky = TakeStickyModifiers?.Invoke() ?? (false, false);
        if (sticky.Ctrl) mods |= KeyModifiers.Control;
        if (sticky.Alt) mods |= KeyModifiers.Alt;

        if (TerminalKeys.Encode(e.Key, mods, Buffer.ApplicationCursorKeys) is not { } text) return;

        Send(text, false);
        e.Handled = true;
    }

    private void OnInputText(object? sender, TextChangedEventArgs e)
    {
        var text = _input.Text;
        if (string.IsNullOrEmpty(text)) return;

        _input.Text = string.Empty;
        Send(text.Replace("\r\n", "\r").Replace('\n', '\r'));
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

        var (start, end) = Ordered(a, _caret);
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
        var lines = (int)Math.Round(e.Delta.Y * 3);
        if (lines == 0) return;

        if (Buffer.MouseTracking != MouseTracking.None && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var (col, row) = ScreenCellAt(e.GetPosition(this));
            for (var i = 0; i < Math.Abs(lines); i++)
                Input?.Invoke(TerminalKeys.Mouse(lines > 0 ? 64 : 65, col, row, false, Buffer.SgrMouse));
        }
        else if (Buffer.IsAlternateScreen)
        {
            // 대체 화면(less·vim 등)은 스크롤백이 없다 — 방향키로 보낸다(xterm alternateScroll)
            var key = lines > 0 ? Key.Up : Key.Down;
            var text = TerminalKeys.Encode(key, KeyModifiers.None, Buffer.ApplicationCursorKeys)!;
            Input?.Invoke(string.Concat(Enumerable.Repeat(text, Math.Abs(lines))));
        }
        else
        {
            ScrollBy(lines);
        }

        e.Handled = true;
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

            _mouseButton = button;
            var (col, row) = ScreenCellAt(pos);
            Input?.Invoke(TerminalKeys.Mouse(button, col, row, false, Buffer.SgrMouse));
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
            if (Buffer.IsAlternateScreen)
            {
                var key = lines > 0 ? Key.Up : Key.Down;
                var text = TerminalKeys.Encode(key, KeyModifiers.None, Buffer.ApplicationCursorKeys)!;
                Input?.Invoke(string.Concat(Enumerable.Repeat(text, Math.Abs(lines))));
            }
            else
            {
                ScrollBy(lines);
            }

            return;
        }

        if (_mouseButton >= 0 && Buffer.MouseTracking is MouseTracking.Drag or MouseTracking.Any)
        {
            var (col, row) = ScreenCellAt(pos);
            Input?.Invoke(TerminalKeys.Mouse(_mouseButton + 32, col, row, false, Buffer.SgrMouse));
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
                return;
            }

            if (_touchMoved) return;

            // 타이머가 늦었어도(바쁜 UI 스레드) 오래 눌렀으면 길게 누르기
            if (DateTime.UtcNow - _touchStartTime >= _longPress.Interval)
            {
                SelectWord(CellAt(_touchStart), false);
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

        if (_mouseButton >= 0)
        {
            var (col, row) = ScreenCellAt(pos);
            Input?.Invoke(TerminalKeys.Mouse(_mouseButton, col, row, true, Buffer.SgrMouse));
            _mouseButton = -1;
            e.Pointer.Capture(null);
            return;
        }

        if (!_selecting) return;

        _selecting = false;
        e.Pointer.Capture(null);
        if (_anchor == _caret) _anchor = null; // 그냥 누른 것 — 선택 아님
        SelectionChanged?.Invoke(HasSelection);
        _surface.InvalidateVisual();
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
        _surface.InvalidateVisual();
    }

    private static double Distance(Point a, Point b)
    {
        return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    // ------------------------------------------------------------ 그리기

    private void RenderTerminal(DrawingContext context)
    {
        var r = _renderer;
        var bounds = _surface.Bounds;
        context.FillRectangle(r.Brush(TerminalRenderer.DefaultBackground), new Rect(bounds.Size));

        lock (Buffer.Gate)
        {
            var rows = Buffer.Rows;
            var columns = Buffer.Columns;
            var offset = Math.Min(ScrollOffset, Buffer.ScrollbackCount);
            for (var row = 0; row < rows; row++)
            {
                var line = Buffer.GetVisibleLine(row, offset);
                r.DrawLine(context, line.Cells, columns, new Point(Pad.Left, Pad.Top + row * r.CellHeight));
            }

            DrawSelection(context, offset);
            if (offset == 0) DrawCursor(context);
        }

        if (ScrollOffset > 0) DrawScrollIndicator(context);
    }

    private void DrawSelection(DrawingContext context, int offset)
    {
        if (_anchor is not { } a || a == _caret) return;

        var r = _renderer;
        var (start, end) = Ordered(a, _caret);
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
        var cursorColor = fg == bg ? TerminalRenderer.DefaultForeground : fg;

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
            r.DrawSingle(context, cell.Rune, width, cell.Flags.HasFlag(CellFlags.Bold), r.Brush(bg), origin);
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
