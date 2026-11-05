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
    private PixelRect _dirty;
    private bool _copyQueued;
    private Func<byte[]?>? _framebuffer;
    private int _mouseButtons;

    public VncSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        Viewport.Changed += InvalidateVisual;
        Gestures = new GestureInterpreter(Viewport, this);
        _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _longPressTimer.Tick += (_, _) =>
        {
            if (Gestures.Tick(Environment.TickCount64)) InvalidateVisual();
        };
    }

    public Viewport Viewport { get; } = new();

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
    public void Attach(int width, int height, Func<byte[]?> framebuffer)
    {
        _framebuffer = framebuffer;
        _bitmap?.Dispose();
        _bitmap = new TiledFramebuffer(width, height);
        Viewport.SetSizes(Bounds.Size, new Size(width, height));
        Gestures.ResetCursor(new Point(width / 2.0, height / 2.0));
        MarkDirty(0, 0, width, height);
    }

    /// <summary>읽기 스레드에서 불러도 된다 — 바뀐 영역을 모아 UI 스레드에서 한 번에 복사한다.</summary>
    public void MarkDirty(int x, int y, int w, int h)
    {
        lock (_dirtyLock)
        {
            var rect = new PixelRect(x, y, w, h);
            _dirty = _dirty.Width == 0 ? rect : _dirty.Union(rect);
            if (_copyQueued) return;

            _copyQueued = true;
        }

        Dispatcher.UIThread.Post(CopyDirty, DispatcherPriority.Render);
    }

    private void CopyDirty()
    {
        PixelRect dirty;
        lock (_dirtyLock)
        {
            dirty = _dirty;
            _dirty = default;
            _copyQueued = false;
        }

        if (_bitmap is not { } bitmap || _framebuffer?.Invoke() is not { } source || dirty.Width == 0) return;

        bitmap.Update(source, dirty); // 바뀐 타일만 다시 올라간다
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Viewport.SetSizes(e.NewSize, Viewport.Remote);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_bitmap is not { } bitmap) return;

        var vp = Viewport;
        // 확대가 크면 픽셀을 또렷하게(최근접), 줄이거나 조금 늘리면 부드럽게
        using (context.PushRenderOptions(new RenderOptions
               {
                   BitmapInterpolationMode = vp.Scale >= 1.5 ? BitmapInterpolationMode.None : BitmapInterpolationMode.MediumQuality
               }))
            bitmap.Render(context, vp.Offset, vp.Scale, new Rect(Bounds.Size));

        // 커서는 서버 응답을 기다리지 않고 지금 손가락·커서 자리에 그린다(서버는 커서를 화면에 그리지 않는다 — 모양만 보냄)
        if (_shape is { } shape && _touchSeen)
            DrawShape(context, shape, vp.ToView(Gestures.Cursor), vp.Scale);
        else if (Gestures.Mode == TouchInputMode.Mouse) DrawCursor(context, vp.ToView(Gestures.Cursor));
        if (Gestures.LongPressArmed)
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#B0FFFFFF")), 3),
                vp.ToView(Gestures.Cursor), 26, 26);
    }

    /// <summary>게스트가 보낸 커서 모양(없으면 null — 대체 화살표). 크기 0 이면 게스트가 커서를 숨긴 것.</summary>
    private (WriteableBitmap? Bitmap, PixelPoint Hot, PixelSize Size)? _shape;

    /// <summary>터치로 조작한 적이 있다 — 모양 커서는 터치 입력 동안 그린다(마우스는 운영체제 커서로 바뀐다).</summary>
    private bool _touchSeen;

    /// <summary>지금 커서를 그릴 자리(화면 좌표) — 서버 응답 전에 바로 바뀐다(검사용).</summary>
    public Point LocalCursorView => Viewport.ToView(Gestures.Cursor);

    public bool HasCursorShape => _shape is not null;

    /// <summary>
    ///     게스트 커서 모양(RichCursor) — UI 스레드에서. 마우스는 이 모양으로 운영체제 커서를 만들어 지연 없이 움직이고,
    ///     터치는 손가락·가상 커서 자리에 그린다. 서버는 커서를 화면 그림에 넣지 않으므로 화면 갱신을 기다릴 필요가 없다.
    /// </summary>
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
            e.Pointer.Capture(this);
            Gestures.Down(e.Pointer.Id, p, (long)e.Timestamp);
            _longPressTimer.Start();
        }
        else
        {
            // 마우스·펜(데스크톱, 태블릿에 연결한 마우스) — 누른 버튼 그대로, 창 밖으로 끌어도 놓칠 때까지 받는다
            e.Pointer.Capture(this);
            _mouseButtons = Buttons(e.GetCurrentPoint(this).Properties);
            SendMouse(p);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (e.Pointer.Type == PointerType.Touch)
        {
            Gestures.Move(e.Pointer.Id, p, (long)e.Timestamp);
        }
        else
        {
            _mouseButtons = Buttons(e.GetCurrentPoint(this).Properties);
            SendMouse(p);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        if (e.Pointer.Type == PointerType.Touch)
        {
            Gestures.Up(e.Pointer.Id, p, (long)e.Timestamp);
            _longPressTimer.Stop();
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
        if (e.Pointer.Type == PointerType.Touch) Gestures.Cancel();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var remote = Viewport.ToRemote(e.GetPosition(this));
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            Viewport.ZoomAt(e.Delta.Y > 0 ? 1.25 : 0.8, e.GetPosition(this)); // Ctrl+휠 = 확대
        }
        else
        {
            var wheel = e.Delta.Y > 0 ? 8 : e.Delta.Y < 0 ? 16 : e.Delta.X > 0 ? 64 : 32;
            PointerSink?.Invoke(_mouseButtons | wheel, (int)remote.X, (int)remote.Y);
            PointerSink?.Invoke(_mouseButtons, (int)remote.X, (int)remote.Y);
        }

        e.Handled = true;
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
