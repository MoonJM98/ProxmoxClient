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
    private WriteableBitmap? _bitmap;
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
        _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
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

        var size = bitmap.PixelSize;
        dirty = dirty.Intersect(new PixelRect(size));
        if (dirty.Width <= 0 || dirty.Height <= 0) return;

        using (var locked = bitmap.Lock())
        {
            var rowBytes = dirty.Width * 4;
            for (var row = dirty.Y; row < dirty.Bottom; row++)
            {
                var offset = (row * size.Width + dirty.X) * 4;
                if (offset + rowBytes > source.Length) break;

                System.Runtime.InteropServices.Marshal.Copy(source, offset,
                    locked.Address + row * locked.RowBytes + dirty.X * 4, rowBytes);
            }
        }

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
        var dest = new Rect(vp.Offset, new Size(vp.Remote.Width * vp.Scale, vp.Remote.Height * vp.Scale));
        using (context.PushRenderOptions(new RenderOptions
               {
                   BitmapInterpolationMode = vp.Scale >= 1.5 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality
               }))
            context.DrawImage(bitmap, new Rect(bitmap.Size), dest);

        if (Gestures.Mode == TouchInputMode.Mouse) DrawCursor(context, vp.ToView(Gestures.Cursor));
        if (Gestures.LongPressArmed)
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#B0FFFFFF")), 3),
                vp.ToView(Gestures.Cursor), 26, 26);
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
            e.Pointer.Capture(this);
            Gestures.Down(e.Pointer.Id, p, (long)e.Timestamp);
            _longPressTimer.Start();
        }
        else
        {
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var keysym = KeySyms.FromKey(e.Key);
        if (keysym == 0 && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
            keysym = KeySyms.LetterOrDigit(e.Key);
        if (keysym == 0) return;

        KeySink?.Invoke(keysym, true);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        var keysym = KeySyms.FromKey(e.Key);
        if (keysym == 0 && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
            keysym = KeySyms.LetterOrDigit(e.Key);
        if (keysym == 0) return;

        KeySink?.Invoke(keysym, false);
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text)) return;

        foreach (var rune in e.Text.EnumerateRunes())
        {
            var keysym = KeySyms.FromChar(rune.Value);
            KeySink?.Invoke(keysym, true);
            KeySink?.Invoke(keysym, false);
        }

        e.Handled = true;
    }
}
