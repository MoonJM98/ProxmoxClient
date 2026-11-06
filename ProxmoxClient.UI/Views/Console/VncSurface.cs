using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ProxmoxClient.UI.Views.Console;

/// <summary>
///     원격 화면 — 프레임버퍼(BGRX)를 GPU 비트맵으로 올려 확대·이동해 그린다.
///     입력: 터치는 <see cref="GestureInterpreter" />(마우스/터치 모드), 마우스·펜·키보드(데스크톱)는 그대로 원격에 보낸다.
///     프레임 갱신은 바뀐 영역만 모아 화면 갱신 주기마다 한 번 복사한다(지연·전력 최소화).
/// </summary>
public sealed class VncSurface : Control, IRemotePointer
{
    private readonly object _dirtyLock = new();
    private readonly DispatcherTimer _longPressTimer;
    private TiledFramebuffer? _bitmap;

    /// <summary>바뀐 영역들 — 떨어진 작은 영역(커서·시계)을 하나로 감싸 화면 전체를 다시 올리지 않게 따로 둔다.</summary>
    private readonly Core.Vnc.DirtyRegion _dirty = new();

    private readonly Core.Vnc.DirtyRegion.Rect[] _dirtyCopy = new Core.Vnc.DirtyRegion.Rect[Core.Vnc.DirtyRegion.Capacity];
    private bool _copyQueued;
    private Func<byte[]?>? _framebuffer;
    private int _mouseButtons;

    public VncSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        Viewport.Changed += InvalidateVisual;
        Gestures = new GestureInterpreter(Viewport, this);
        _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SlowTickMs) };
        _longPressTimer.Tick += (_, _) =>
        {
            if (Gestures.Tick(Environment.TickCount64)) InvalidateVisual();
            if (!Gestures.NeedsTicks) _longPressTimer.Stop(); // 손을 떼고 기다리던 클릭도 끝났다
            else UpdateTickRate();
        };
        _mouseEdgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _mouseEdgeTimer.Tick += (_, _) =>
        {
            if (!TickMouseEdge(Environment.TickCount64)) _mouseEdgeTimer.Stop();
        };
    }

    public Viewport Viewport { get; } = new();

    /// <summary>길게 누르기·두 번 탭 기다림은 느린 주기로, 확대한 채 끄는 중(가장자리 이동)은 화면 갱신 주기로.</summary>
    public const int SlowTickMs = 60, FastTickMs = 16;

    /// <summary>지금 손짓 타이머 주기(ms) — 검사용.</summary>
    public int TickIntervalMs => (int)_longPressTimer.Interval.TotalMilliseconds;

    private void UpdateTickRate()
    {
        var interval = TimeSpan.FromMilliseconds(Gestures.IsEdgeDragging ? FastTickMs : SlowTickMs);
        if (_longPressTimer.Interval != interval) _longPressTimer.Interval = interval;
    }

    public GestureInterpreter Gestures { get; }

    /// <summary>원격으로 포인터 이벤트를 보낸다(버튼 마스크, x, y).</summary>
    public Action<int, int, int>? PointerSink { get; set; }

    /// <summary>원격으로 키 이벤트를 보낸다(keysym, down).</summary>
    public Action<int, bool>? KeySink { get; set; }

    void IRemotePointer.Send(int buttons, int x, int y)
    {
        PointerSink?.Invoke(buttons, x, y);
        InvalidateVisual(); // 커서 표시
    }

    /// <summary>원격 화면 크기가 정해졌다(연결·해상도 변경).</summary>
    /// <summary>
    ///     원격 화면 크기가 정해졌다(연결·해상도 변경). 다시 연결해 크기가 같으면 확대·이동·커서를 그대로 둔다.
    /// </summary>
    public void Attach(int width, int height, Func<byte[]?> framebuffer)
    {
        _framebuffer = framebuffer;
        _bitmap?.Dispose();
        _bitmap = new TiledFramebuffer(width, height);
        var sameSize = Viewport.Remote == new Size(width, height);
        UpdatePixelScale();
        Viewport.SetSizes(Bounds.Size, new Size(width, height));
        if (!sameSize) Gestures.ResetCursor(new Point(width / 2.0, height / 2.0));
        MarkDirty(0, 0, width, height);
    }

    /// <summary>읽기 스레드에서 불러도 된다 — 바뀐 영역을 모아 UI 스레드에서 한 번에 복사한다.</summary>
    public void MarkDirty(int x, int y, int w, int h)
    {
        lock (_dirtyLock)
        {
            _dirty.Add(x, y, w, h);
            if (_copyQueued) return;

            _copyQueued = true;
        }

        Dispatcher.UIThread.Post(CopyDirty, DispatcherPriority.Render);
    }

    /// <summary>지난번 복사에서 올린 영역 수(검사용).</summary>
    public int LastCopiedRects { get; private set; }

    private void CopyDirty()
    {
        int count;
        lock (_dirtyLock)
        {
            count = _dirty.CopyTo(_dirtyCopy);
            _dirty.Clear();
            _copyQueued = false;
        }

        if (_bitmap is not { } bitmap || _framebuffer?.Invoke() is not { } source || count == 0) return;

        // 영역마다 걸친 타일만 다시 올라간다
        for (var i = 0; i < count; i++)
        {
            var r = _dirtyCopy[i];
            bitmap.Update(source, new PixelRect(r.X1, r.Y1, r.Width, r.Height));
        }

        LastCopiedRects = count;
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdatePixelScale();
        Viewport.SetSizes(e.NewSize, Viewport.Remote);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdatePixelScale();
    }

    /// <summary>화면 배율(논리 단위당 물리 픽셀) — 1:1 보기와 또렷하게 그리기 기준.</summary>
    public double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private void UpdatePixelScale()
    {
        Viewport.PixelScale = RenderScaling;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_bitmap is not { } bitmap) return;

        var vp = Viewport;
        var pixelScale = RenderScaling;
        // 원격 1픽셀이 화면 물리 픽셀 1.5개 이상이면 또렷하게(최근접), 줄이거나 조금 늘리면 부드럽게
        using (context.PushRenderOptions(new RenderOptions
               {
                   BitmapInterpolationMode = vp.Scale * pixelScale >= 1.5 ? BitmapInterpolationMode.None : BitmapInterpolationMode.MediumQuality
               }))
            bitmap.Render(context, vp.Offset, vp.Scale, new Rect(Bounds.Size), pixelScale);

        // 커서는 서버 응답을 기다리지 않고 지금 손가락·커서 자리에 그린다(서버는 커서를 화면에 그리지 않는다 — 모양만 보냄)
        // 실제 마우스를 쓰는 중이면 운영체제 커서만 — 직접 그리면 커서가 두 개 보인다
        if (DrawsSoftwareCursor && _shape is { } shape && _touchSeen)
            DrawShape(context, shape, vp.ToView(Gestures.Cursor), vp.Scale);
        else if (DrawsSoftwareCursor && Gestures.Mode == TouchInputMode.Mouse) DrawCursor(context, vp.ToView(Gestures.Cursor));
        if (Gestures.LongPressArmed)
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#B0FFFFFF")), 3),
                vp.ToView(Gestures.Cursor), 26, 26);
    }

    /// <summary>게스트가 보낸 커서 모양(없으면 null — 대체 화살표). 크기 0 이면 게스트가 커서를 숨긴 것.</summary>
    private (WriteableBitmap? Bitmap, PixelPoint Hot, PixelSize Size)? _shape;

    /// <summary>터치로 조작한 적이 있다 — 모양 커서는 터치 입력 동안 그린다(마우스는 운영체제 커서로 바뀐다).</summary>
    private bool _touchSeen;

    /// <summary>마지막 입력이 실제 마우스·펜이다 — 터치하면 다시 꺼진다.</summary>
    private bool _mouseActive;

    /// <summary>커서를 직접 그린다 — 실제 마우스를 쓰는 동안은 운영체제 커서가 보이므로 그리지 않는다.</summary>
    public bool DrawsSoftwareCursor => !_mouseActive;

    // ---- 확대 중 실제 마우스가 가장자리에 머물면 그쪽으로 화면을 옮긴다(마우스만으로는 확대한 화면을 옮길 방법이 없다)
    public const double MouseEdgeZone = 24;
    public const double MouseEdgeSpeed = 0.5; // 보기 px/ms, 가장자리에 닿았을 때

    private readonly DispatcherTimer _mouseEdgeTimer;
    private Point? _mouseView;
    private long _mouseEdgeLast;

    /// <summary>마우스가 가장자리에 있는 방향(-1~1) — 가장자리에 가까울수록 크다.</summary>
    private Vector MouseEdgeDirection(Point at)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var zone = Math.Min(MouseEdgeZone, Math.Min(w, h) / 4);
        if (zone <= 0) return default;

        double Push(double pos, double size) =>
            pos < zone ? -Math.Min(1, (zone - pos) / zone) : pos > size - zone ? Math.Min(1, (pos - (size - zone)) / zone) : 0;

        return new Vector(Push(at.X, w), Push(at.Y, h));
    }

    private void UpdateMouseEdge()
    {
        if (_mouseView is { } at && Viewport.IsZoomed && MouseEdgeDirection(at) != default)
        {
            if (!_mouseEdgeTimer.IsEnabled)
            {
                _mouseEdgeLast = Environment.TickCount64;
                _mouseEdgeTimer.Start();
            }
        }
        else
        {
            _mouseEdgeTimer.Stop();
        }
    }

    /// <summary>가장자리 이동 한 걸음 — 더 갈 곳이 없거나 가장자리를 벗어났으면 false(검사도 직접 부른다).</summary>
    public bool TickMouseEdge(long ms)
    {
        var elapsed = Math.Clamp(ms - _mouseEdgeLast, 0, 100);
        _mouseEdgeLast = ms;
        if (_mouseView is not { } at || !Viewport.IsZoomed) return false;

        var direction = MouseEdgeDirection(at);
        if (direction == default) return false;
        if (elapsed == 0) return true;

        var before = Viewport.Offset;
        Viewport.PanBy(-direction * MouseEdgeSpeed * elapsed); // 오른쪽 가장자리 = 오른쪽 내용이 보이게
        if (Viewport.Offset == before) return false; // 원격 화면 끝

        SendMouse(at); // 마우스는 그대로, 그 아래 원격 자리가 바뀌었다(누른 버튼 유지 — 끌기 이어짐)
        return true;
    }

    /// <summary>지금 커서를 그릴 자리(화면 좌표) — 서버 응답 전에 바로 바뀐다(검사용).</summary>
    public Point LocalCursorView => Viewport.ToView(Gestures.Cursor);

    public bool HasCursorShape => _shape is not null;

    /// <summary>
    ///     게스트 커서 모양(RichCursor) — UI 스레드에서. 마우스는 이 모양으로 운영체제 커서를 만들어 지연 없이 움직이고,
    ///     터치는 손가락·가상 커서 자리에 그린다. 서버는 커서를 화면 그림에 넣지 않으므로 화면 갱신을 기다릴 필요가 없다.
    /// </summary>
    /// <summary>게스트가 기본 커서로 돌아갔다(RDP) — 받은 모양을 버리고 대체 화살표·운영체제 기본 커서를 쓴다.</summary>
    public void ClearCursorShape()
    {
        _shape = null;
        Cursor = null;
        InvalidateVisual();
    }

    public void SetCursorShape(Core.Vnc.RfbCursor cursor)
    {
        if (cursor.IsEmpty)
        {
            _shape = (null, default, default);
            Cursor = new Cursor(StandardCursorType.None);
            InvalidateVisual();
            return;
        }

        var bitmap = new WriteableBitmap(new PixelSize(cursor.Width, cursor.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var fb = bitmap.Lock())
        {
            var rowBytes = cursor.Width * 4;
            for (var y = 0; y < cursor.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(cursor.Pixels, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
        }

        _shape = (bitmap, new PixelPoint(cursor.HotX, cursor.HotY), new PixelSize(cursor.Width, cursor.Height));
        try
        {
            Cursor = new Cursor(bitmap, new PixelPoint(cursor.HotX, cursor.HotY));
        }
        catch (Exception)
        {
            // 운영체제 커서를 못 만드는 플랫폼(모바일) — 터치 쪽 그리기만 쓴다
        }

        InvalidateVisual();
    }

    private static void DrawShape(DrawingContext context, (WriteableBitmap? Bitmap, PixelPoint Hot, PixelSize Size) shape,
        Point at, double scale)
    {
        if (shape.Bitmap is not { } bitmap) return; // 게스트가 커서를 숨김

        // 원격 화면과 같은 배율로(원격 화면 위 실제 크기), 너무 작아지지 않게 최소 1배
        var s = Math.Max(1, scale);
        var rect = new Rect(at.X - shape.Hot.X * s, at.Y - shape.Hot.Y * s, shape.Size.Width * s, shape.Size.Height * s);
        context.DrawImage(bitmap, rect);
    }

    private static void DrawCursor(DrawingContext context, Point tip)
    {
        // 화살표 커서(흰 바탕·검은 테두리) — 손가락에 가리지 않게 마우스 모드에서만 그린다
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(tip, true);
            g.LineTo(tip + new Vector(0, 18));
            g.LineTo(tip + new Vector(4.5, 13.5));
            g.LineTo(tip + new Vector(8, 20.5));
            g.LineTo(tip + new Vector(11, 19));
            g.LineTo(tip + new Vector(7.5, 12.5));
            g.LineTo(tip + new Vector(13, 12.5));
            g.EndFigure(true);
        }

        context.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.2), geometry);
    }

    // ------------------------------------------------------------- 입력

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        if (e.Pointer.Type == PointerType.Touch)
        {
            _touchSeen = true;
            SetMouseActive(false);
            e.Pointer.Capture(this);
            Gestures.Down(e.Pointer.Id, p, Environment.TickCount64); // 타이머(Tick)와 같은 시계로
            _longPressTimer.Interval = TimeSpan.FromMilliseconds(SlowTickMs);
            _longPressTimer.Start();
        }
        else
        {
            // 마우스·펜(데스크톱, 태블릿에 연결한 마우스) — 누른 버튼 그대로, 창 밖으로 끌어도 놓칠 때까지 받는다
            e.Pointer.Capture(this);
            SetMouseActive(true);
            _mouseButtons = Buttons(e.GetCurrentPoint(this).Properties);
            _mouseView = p;
            SendMouse(p);
            UpdateMouseEdge();
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (e.Pointer.Type == PointerType.Touch)
        {
            Gestures.Move(e.Pointer.Id, p, Environment.TickCount64);
            if (_longPressTimer.IsEnabled) UpdateTickRate(); // 확대한 채 끌기 시작 — 가장자리 이동을 부드럽게
        }
        else
        {
            SetMouseActive(true);
            _mouseButtons = Buttons(e.GetCurrentPoint(this).Properties);
            _mouseView = p;
            SendMouse(p);
            UpdateMouseEdge();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (e.Pointer.Type == PointerType.Touch || _mouseButtons != 0) return; // 누른 채 밖으로 끌면 계속 옮긴다

        _mouseView = null;
        _mouseEdgeTimer.Stop();
    }

    private void SetMouseActive(bool active)
    {
        if (_mouseActive == active) return;

        _mouseActive = active;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        if (e.Pointer.Type == PointerType.Touch)
        {
            Gestures.Up(e.Pointer.Id, p, Environment.TickCount64);
            if (!Gestures.NeedsTicks) _longPressTimer.Stop(); // 한 번 탭 뒤 두 번째 터치를 기다리는 동안은 계속
            InvalidateVisual();
        }
        else
        {
            _mouseButtons = Buttons(e.GetCurrentPoint(this).Properties);
            SendMouse(p);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // 손을 뗀 뒤에도 캡처 해제가 따로 온다(Android) — 그때 지우면 두 번 탭 뒤 기다리던 클릭이 사라진다(실기기 보고).
        // 손가락이 아직 닿아 있는데 캡처를 잃었을 때만(다른 컨트롤이 가져감) 손짓을 거둔다
        if (e.Pointer.Type == PointerType.Touch && Gestures.HasFingers) Gestures.Cancel();
    }

    /// <summary>아직 한 칸이 안 된 휠 움직임(터치패드·고해상도 휠은 소수로 온다).</summary>
    private Vector _wheelRemainder;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var remote = Viewport.ToRemote(e.GetPosition(this));
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            // Ctrl+휠 = 확대 — 움직인 만큼(한 칸 = 1.25배)
            var factor = Math.Pow(1.25, Math.Clamp(e.Delta.Y, -4, 4));
            if (factor != 1) Viewport.ZoomAt(factor, e.GetPosition(this));
        }
        else
        {
            foreach (var wheel in WheelClicks(e.Delta))
            {
                PointerSink?.Invoke(_mouseButtons | wheel, (int)remote.X, (int)remote.Y);
                PointerSink?.Invoke(_mouseButtons, (int)remote.X, (int)remote.Y);
            }
        }

        e.Handled = true;
    }

    /// <summary>
    ///     휠 움직임을 모아 한 칸(1.0)마다 RFB 휠 버튼 하나(8 위 · 16 아래 · 32 왼쪽 · 64 오른쪽)로 — 세로·가로 모두.
    ///     빠르게 굴린 3칸은 세 번, 터치패드의 0.1 은 열 번 모여야 한 번.
    /// </summary>
    public List<int> WheelClicks(Vector delta)
    {
        const int MaxClicks = 20; // 터무니없는 값에 이벤트가 쏟아지지 않게
        var clicks = new List<int>();
        _wheelRemainder += delta;
        var y = Math.Truncate(_wheelRemainder.Y);
        var x = Math.Truncate(_wheelRemainder.X);
        _wheelRemainder -= new Vector(x, y);
        for (var i = 0; i < Math.Min(Math.Abs(y), MaxClicks); i++) clicks.Add(y > 0 ? 8 : 16);
        for (var i = 0; i < Math.Min(Math.Abs(x), MaxClicks); i++) clicks.Add(x > 0 ? 64 : 32);
        return clicks;
    }

    private void SendMouse(Point view)
    {
        var remote = Viewport.ToRemote(view);
        Gestures.ResetCursor(remote);
        PointerSink?.Invoke(_mouseButtons, (int)remote.X, (int)remote.Y);
    }

    private static int Buttons(PointerPointProperties p)
    {
        return (p.IsLeftButtonPressed ? 1 : 0) | (p.IsMiddleButtonPressed ? 2 : 0) | (p.IsRightButtonPressed ? 4 : 0);
    }

    /// <summary>
    ///     물리 키보드(데스크톱·태블릿 USB/블루투스 키보드) — WPF 콘솔처럼 키 위치(스캔코드) + keysym 을 함께 보낸다.
    ///     눌린 키를 기억했다가 포커스를 잃으면 모두 뗀다(게스트에 키가 눌린 채 남지 않게).
    ///     소프트 키보드(IME)는 물리 키 정보가 없어 글자(TextInput)로 보낸다.
    /// </summary>
    public Action<int, int, bool>? ScanKeySink { get; set; }

    private readonly Dictionary<PhysicalKey, (int Xt, int Keysym)> _pressed = [];

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var xt = PhysicalKeys.ToXt(e.PhysicalKey);
        var keysym = PhysicalKeys.Keysym(e);
        if (xt == 0 && keysym == 0) return;

        if (e.PhysicalKey != PhysicalKey.None && xt != 0)
        {
            if (!_pressed.ContainsKey(e.PhysicalKey)) _pressed[e.PhysicalKey] = (xt, keysym);
            else keysym = _pressed[e.PhysicalKey].Keysym; // 자동 반복 — 처음 누를 때와 같은 keysym
            ScanKeySink?.Invoke(xt, keysym, true);
            e.Handled = true;
            return;
        }

        if (KeySyms.FromKey(e.Key) is var special and not 0)
        {
            KeySink?.Invoke(special, true);
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_pressed.Remove(e.PhysicalKey, out var pressed))
        {
            ScanKeySink?.Invoke(pressed.Xt, pressed.Keysym, false);
            e.Handled = true;
            return;
        }

        if (KeySyms.FromKey(e.Key) is var special and not 0)
        {
            KeySink?.Invoke(special, false);
            e.Handled = true;
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        ReleaseAllKeys();
    }

    /// <summary>눌려 있는 물리 키를 모두 뗀다(포커스 잃음·화면 떠남).</summary>
    public void ReleaseAllKeys()
    {
        foreach (var (xt, keysym) in _pressed.Values) ScanKeySink?.Invoke(xt, keysym, false);
        _pressed.Clear();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        // 물리 키로 이미 보낸 글자는 다시 보내지 않는다(키 위치로 보냈으므로 게스트가 글자를 만든다)
        if (string.IsNullOrEmpty(e.Text) || _pressed.Count > 0) return;

        foreach (var rune in e.Text.EnumerateRunes())
        {
            var keysym = KeySyms.FromChar(rune.Value);
            KeySink?.Invoke(keysym, true);
            KeySink?.Invoke(keysym, false);
        }

        e.Handled = true;
    }
}
