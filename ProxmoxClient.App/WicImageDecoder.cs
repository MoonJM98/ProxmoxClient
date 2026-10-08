using System.IO;
using System.Runtime.InteropServices;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App;

/// <summary>
///     Tight JPEG/PNG 조각 디코더 — WIC(Windows Imaging Component)를 COM 으로 바로 부른다.
///     WPF BitmapDecoder 는 조각마다 DispatcherObject·관리 래퍼를 만들고 내부 잠금으로 스레드끼리 기다려,
///     작은 조각(QEMU 압축 2 는 240×24 정도)이 많으면 느리다. WIC 팩터리는 여러 스레드에서 함께 쓸 수 있다.
///     결과는 프레임버퍼의 (x, y) 위치에 행 간격 framebufferWidth*4 로 바로 기록한다(중간 배열 없음).
/// </summary>
internal static class WicImageDecoder
{
    private const int DecodeCacheOnLoad = 1;
    private static readonly Guid FactoryClsid = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid PixelFormatBgr32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90e");
    private static readonly Lazy<IWicImagingFactory> Factory = new(() =>
        (IWicImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(FactoryClsid, true)!)!);

    public static void Decode(
        byte[] data, int length, byte[] framebuffer, int framebufferWidth, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        var rowBytes = width * 4;
        var lastRowEnd = ((long)(y + height - 1) * framebufferWidth + x) * 4 + rowBytes;
        if (x < 0 || y < 0 || x + width > framebufferWidth || lastRowEnd > framebuffer.Length)
            throw new IOException(Loc.T("ConsoleWindow_TightOutOfRange"));

        var source = GCHandle.Alloc(data, GCHandleType.Pinned);
        var target = GCHandle.Alloc(framebuffer, GCHandleType.Pinned);
        IWicStream? stream = null;
        IWicBitmapDecoder? decoder = null;
        IWicBitmapSource? frame = null;
        IWicBitmapSource? converted = null;
        try
        {
            var factory = Factory.Value;
            factory.CreateStream(out stream);
            stream.InitializeFromMemory(source.AddrOfPinnedObject(), (uint)length);
            factory.CreateDecoderFromStream(stream, IntPtr.Zero, DecodeCacheOnLoad, out decoder); // 공급자 무관(NULL)
            decoder.GetFrameCount(out var frames);
            if (frames == 0) throw new IOException(Loc.T("ConsoleWindow_TightNoFrame"));

            decoder.GetFrame(0, out var decoded);
            frame = decoded;
            var format = PixelFormatBgr32;
            Marshal.ThrowExceptionForHR(WICConvertBitmapSource(ref format, frame, out converted));
            converted.GetSize(out var imageWidth, out var imageHeight);
            var rect = new WicRect(0, 0, Math.Min(width, (int)imageWidth), Math.Min(height, (int)imageHeight));
            if (rect.Width <= 0 || rect.Height <= 0) return;

            var start = ((long)y * framebufferWidth + x) * 4;
            converted.CopyPixels(ref rect, (uint)(framebufferWidth * 4), (uint)(lastRowEnd - start),
                target.AddrOfPinnedObject() + (nint)start);
        }
        catch (COMException ex)
        {
            throw new IOException(ex.Message, ex); // 깨진 이미지 — 다른 프로토콜 오류처럼 연결을 끊는다
        }
        finally
        {
            Release(converted);
            Release(frame);
            Release(decoder);
            Release(stream);
            target.Free();
            source.Free();
        }
    }

    private static void Release(object? com)
    {
        if (com is not null) Marshal.ReleaseComObject(com);
    }

    [DllImport("windowscodecs.dll")]
    private static extern int WICConvertBitmapSource(ref Guid dstFormat, IWicBitmapSource source,
        out IWicBitmapSource converted);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct WicRect(int X, int Y, int Width, int Height);

    // 아래 COM 인터페이스는 vtable 순서만 맞추면 되므로 쓰지 않는 메서드는 인자 없이 자리만 둔다

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicImagingFactory
    {
        void CreateDecoderFromFilename();
        void CreateDecoderFromStream(IWicStream stream, IntPtr vendor, int options, out IWicBitmapDecoder decoder);
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder();
        void CreatePalette();
        void CreateFormatConverter();
        void CreateBitmapScaler();
        void CreateBitmapClipper();
        void CreateBitmapFlipRotator();
        void CreateStream(out IWicStream stream);
    }

    [ComImport, Guid("135ff860-22b7-4ddf-b0f6-218f4f299a43"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicStream
    {
        void Read();
        void Write();
        void Seek();
        void SetSize();
        void CopyTo();
        void Commit();
        void Revert();
        void LockRegion();
        void UnlockRegion();
        void Stat();
        void Clone();
        void InitializeFromIStream();
        void InitializeFromFilename();
        void InitializeFromMemory(IntPtr buffer, uint size);
    }

    [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicBitmapDecoder
    {
        void QueryCapability();
        void Initialize();
        void GetContainerFormat();
        void GetDecoderInfo();
        void CopyPalette();
        void GetMetadataQueryReader();
        void GetPreview();
        void GetColorContexts();
        void GetThumbnail();
        void GetFrameCount(out uint count);
        void GetFrame(uint index, out IWicBitmapSource frame);
    }

    [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicBitmapSource
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat(out Guid format);
        void GetResolution(out double dpiX, out double dpiY);
        void CopyPalette(IntPtr palette);
        void CopyPixels(ref WicRect rect, uint stride, uint bufferSize, IntPtr buffer);
    }
}
