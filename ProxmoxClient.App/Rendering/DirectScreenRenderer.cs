using System.Drawing;
using ProxmoxClient.Core.Vnc;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using InterpolationMode = Vortice.Direct2D1.InterpolationMode;

namespace ProxmoxClient.App.Rendering;

/// <summary>
///     콘솔 화면을 WPF 를 거치지 않고 자식 창에 바로 그린다 — Direct3D 11 + DXGI flip-model 스왑체인 위의 Direct2D.
///     프레임이 오면 렌더 스레드가 바뀐 영역만 GPU 비트맵에 올리고 곧바로 Present 한다(WPF 렌더 틱·합성 단계를
///     기다리지 않아 화면이 한 프레임쯤 빨리 바뀐다). 장치를 잃으면 한 번 다시 만들고, 그래도 안 되면 Failed 로 알린다.
///     공개 메서드는 아무 스레드에서나 불러도 된다 — 상태만 바꾸고 렌더 스레드를 깨운다.
/// </summary>
internal sealed class DirectScreenRenderer : IDisposable
{
    private const int MaxRecoveries = 3;
    private static readonly Color4 Background = new(0, 0, 0, 1);
    private static readonly FeatureLevel[] FeatureLevels =
        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

    private readonly IntPtr _hwnd;
    private readonly object _lock = new();
    private readonly DirtyRegion _dirty = new();
    private readonly DirtyRegion.Rect[] _dirtyCopy = new DirtyRegion.Rect[DirtyRegion.Capacity];
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private Func<(byte[]? Buffer, int Width, int Height)> _source = () => (null, 0, 0);

    // 상태(잠금 보호) — UI·읽기 스레드가 바꾸고 렌더 스레드가 그릴 때 한 번 복사한다
    private int _width, _height;
    private bool _resize = true, _fullFrame = true, _disposed;
    private RectangleF _destination;
    private bool _smooth;
    private RfbCursor? _cursorShape;
    private bool _cursorChanged;
    private PointF? _cursorAt;
    private float _cursorScale = 1;

    // 렌더 스레드 전용
    private ID3D11Device? _device;
    private IDXGISwapChain1? _swapChain;
    private ID2D1Factory1? _factory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _context;
    private ID2D1Bitmap1? _target;
    private ID2D1Bitmap1? _frame;
    private ID2D1Bitmap1? _cursor;
    private int _frameWidth, _frameHeight;

    public DirectScreenRenderer(IntPtr hwnd, int width, int height)
    {
        _hwnd = hwnd;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "Console D3D render" };
    }

    /// <summary>렌더 스레드 시작 — <see cref="Failed" /> 를 구독한 뒤에 부른다(장치 생성 실패를 놓치지 않게).</summary>
    public void Start() => _thread.Start();

    /// <summary>GPU 렌더링을 시작하지 못했거나 되살리지 못했다(렌더 스레드에서 한 번) — WPF 로 돌아가야 한다.</summary>
    public event Action<Exception>? Failed;

    /// <summary>그릴 프레임버퍼(BGRX)를 주는 함수 — 렌더 스레드에서 부른다.</summary>
    public void SetSource(Func<(byte[]? Buffer, int Width, int Height)> source)
    {
        lock (_lock)
        {
            _source = source;
            _fullFrame = true;
        }

        _wake.Set();
    }

    /// <summary>프레임버퍼의 이 영역이 바뀌었다(읽기 스레드에서) — 곧바로 그린다.</summary>
    public void Invalidate(int x, int y, int w, int h)
    {
        lock (_lock) _dirty.Add(x, y, w, h);
        _wake.Set();
    }

    public void InvalidateAll()
    {
        lock (_lock) _fullFrame = true;
        _wake.Set();
    }

    /// <summary>창(클라이언트 영역) 크기 — 물리 픽셀.</summary>
    public void Resize(int width, int height)
    {
        lock (_lock)
        {
            if (_width == width && _height == height) return;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _resize = true;
        }

        _wake.Set();
    }

    /// <summary>프레임버퍼를 그릴 자리(창 안 물리 픽셀)와 축소 보간.</summary>
    public void SetLayout(RectangleF destination, bool smooth)
    {
        lock (_lock)
        {
            if (_destination == destination && _smooth == smooth) return;
            _destination = destination;
            _smooth = smooth;
        }

        _wake.Set();
    }

    /// <summary>겹쳐 그릴 게스트 커서 모양(없으면 null).</summary>
    public void SetCursorShape(RfbCursor? shape)
    {
        lock (_lock)
        {
            _cursorShape = shape is { IsEmpty: false } ? shape : null;
            _cursorChanged = true;
        }

        _wake.Set();
    }

    /// <summary>커서 핫스팟 위치(창 안 물리 픽셀, 숨기면 null)와 크기 배율.</summary>
    public void MoveCursor(PointF? position, float scale)
    {
        lock (_lock)
        {
            if (_cursorAt == position && Math.Abs(_cursorScale - scale) < 0.001f) return;
            _cursorAt = position;
            _cursorScale = scale;
        }

        _wake.Set();
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        _wake.Set();
        // 장치 해제는 렌더 스레드가 한다 — 잠깐만 기다리고, 늦으면 백그라운드 스레드에 맡긴다
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void RenderLoop()
    {
        var failures = 0;
        try
        {
            CreateDevice();
            while (true)
            {
                _wake.WaitOne();
                if (Volatile.Read(ref _disposed)) break;

                try
                {
                    Draw();
                    failures = 0;
                }
                catch (SharpGenException ex) when (IsDeviceLost(ex.ResultCode) && ++failures <= MaxRecoveries)
                {
                    App.Log($"[GPU 화면] 장치를 다시 만든다: {ex.Message}");
                    ReleaseDevice();
                    CreateDevice();
                    InvalidateAll();
                }
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _disposed)) return; // 닫는 중(창을 먼저 지운 경우)의 실패는 알리지 않는다
            App.Log($"[GPU 화면] 사용할 수 없어 WPF 로 그린다: {ex}");
            Failed?.Invoke(ex);
        }
        finally
        {
            // 깨우기 신호(_wake)는 다른 스레드가 아직 부를 수 있어 닫지 않는다
            try
            {
                ReleaseDevice();
            }
            catch (Exception ex)
            {
                App.Log($"[GPU 화면] 장치 해제 실패: {ex.Message}"); // 백그라운드 스레드 예외로 앱이 끝나지 않게
            }
        }
    }

    private static bool IsDeviceLost(Result result) =>
        result == Vortice.DXGI.ResultCode.DeviceRemoved || result == Vortice.DXGI.ResultCode.DeviceReset
                                                       || result == Vortice.DXGI.ResultCode.DeviceHung
                                                       || result == Vortice.Direct2D1.ResultCode.RecreateTarget;

    private void CreateDevice()
    {
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, FeatureLevels,
            out _device).CheckError();
        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice1>();
        dxgiDevice.SetMaximumFrameLatency(1).CheckError(); // 앞선 프레임이 줄 서지 않게
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        int width, height;
        lock (_lock) (width, height, _resize) = (_width, _height, false);
        var description = new SwapChainDescription1
        {
            Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore
        };
        _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, description);
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll); // Alt+Enter 전체 화면 전환 막기
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        _d2dDevice = _factory.CreateDevice(dxgiDevice);
        _context = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        CreateTarget();
        lock (_lock) (_fullFrame, _cursorChanged) = (true, true);
    }

    private void CreateTarget()
    {
        using var surface = _swapChain!.GetBuffer<IDXGISurface>(0);
        _target = _context!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Ignore), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw));
        _context.Target = _target;
    }

    private void Draw()
    {
        Func<(byte[]? Buffer, int Width, int Height)> source;
        bool resize, full;
        int width, height, dirtyCount;
        RectangleF destination;
        bool smooth, cursorChanged;
        RfbCursor? cursorShape;
        PointF? cursorAt;
        float cursorScale;
        lock (_lock)
        {
            (source, resize, full, width, height) = (_source, _resize, _fullFrame, _width, _height);
            (destination, smooth, cursorChanged, cursorShape) = (_destination, _smooth, _cursorChanged, _cursorShape);
            (cursorAt, cursorScale) = (_cursorAt, _cursorScale);
            dirtyCount = _dirty.CopyTo(_dirtyCopy);
            _dirty.Clear();
            (_resize, _fullFrame, _cursorChanged) = (false, false, false);
        }

        if (resize) ResizeTarget(width, height);
        if (cursorChanged) UpdateCursorBitmap(cursorShape);
        var (buffer, frameWidth, frameHeight) = source();
        var hasFrame = buffer is not null && frameWidth > 0 && frameHeight > 0
                       && buffer.Length >= frameWidth * frameHeight * 4;
        if (hasFrame) UploadFrame(buffer!, frameWidth, frameHeight, full, dirtyCount);

        var context = _context!;
        context.BeginDraw();
        context.Clear(Background);
        if (hasFrame && _frame is not null && destination.Width > 0 && destination.Height > 0)
            context.DrawBitmap(_frame, ToRect(destination), 1,
                smooth ? InterpolationMode.Linear : InterpolationMode.NearestNeighbor, null, null);
        if (_cursor is not null && cursorShape is not null && cursorAt is { } at)
        {
            var left = at.X - cursorShape.HotX * cursorScale;
            var top = at.Y - cursorShape.HotY * cursorScale;
            context.DrawBitmap(_cursor, new RawRectF(left, top, left + cursorShape.Width * cursorScale,
                top + cursorShape.Height * cursorScale), 1, InterpolationMode.NearestNeighbor, null, null);
        }

        context.EndDraw().CheckError();
        // 다음 수직 동기에 화면에 — 그동안 들어온 갱신은 한 번에 그린다(주사율보다 자주 그리지 않게).
        // 최대 프레임 지연 1 이라 앞선 프레임이 줄 서지 않는다
        _swapChain!.Present(1, PresentFlags.None).CheckError();
    }

    private static RawRectF ToRect(RectangleF r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private void ResizeTarget(int width, int height)
    {
        _context!.Target = null;
        _target?.Dispose();
        _target = null;
        _swapChain!.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        CreateTarget();
    }

    /// <summary>바뀐 영역만 GPU 비트맵으로 — 크기가 바뀌었거나 처음이면 통째로.</summary>
    private void UploadFrame(byte[] buffer, int width, int height, bool full, int dirtyCount)
    {
        if (_frame is null || _frameWidth != width || _frameHeight != height)
        {
            _frame?.Dispose();
            _frame = _context!.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Ignore)));
            (_frameWidth, _frameHeight) = (width, height);
            full = true;
        }

        var pitch = (uint)(width * 4);
        if (full)
        {
            _frame.CopyFromMemory(new Rectangle(0, 0, width, height), ref buffer[0], pitch).CheckError();
            return;
        }

        for (var i = 0; i < dirtyCount; i++)
        {
            var r = _dirtyCopy[i];
            var x = Math.Clamp(r.X1, 0, width);
            var y = Math.Clamp(r.Y1, 0, height);
            var w = Math.Min(r.X2, width) - x;
            var h = Math.Min(r.Y2, height) - y;
            if (w <= 0 || h <= 0) continue;

            _frame.CopyFromMemory(new Rectangle(x, y, w, h), ref buffer[(y * width + x) * 4], pitch).CheckError();
        }
    }

    /// <summary>게스트 커서(BGRA, 알파 0/255) → 미리 곱한 알파 비트맵.</summary>
    private void UpdateCursorBitmap(RfbCursor? shape)
    {
        _cursor?.Dispose();
        _cursor = null;
        if (shape is null) return;

        var pixels = (byte[])shape.Pixels.Clone();
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] == 0) pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
        _cursor = _context!.CreateBitmap(new SizeI(shape.Width, shape.Height), IntPtr.Zero, 0,
            new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied)));
        _cursor.CopyFromMemory(pixels, (uint)(shape.Width * 4)).CheckError();
    }

    private void ReleaseDevice()
    {
        if (_context is not null) _context.Target = null;
        _cursor?.Dispose();
        _frame?.Dispose();
        _target?.Dispose();
        _context?.Dispose();
        _d2dDevice?.Dispose();
        _factory?.Dispose();
        _swapChain?.Dispose();
        _device?.Dispose();
        (_cursor, _frame, _target, _context, _d2dDevice, _factory, _swapChain, _device) =
            (null, null, null, null, null, null, null, null);
        (_frameWidth, _frameHeight) = (0, 0);
    }
}
