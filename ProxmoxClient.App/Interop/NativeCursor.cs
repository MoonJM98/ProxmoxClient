using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace ProxmoxClient.App.Interop;

/// <summary>
///     BGRA 픽셀로 진짜 Windows 커서를 만든다 — 운영체제가 하드웨어 커서로 그려 화면 갱신·렌더 루프와 무관하게
///     지연 없이 움직인다. 알파가 0 인 픽셀은 투명(색도 0 으로 맞춰 가장자리 번짐 없음).
/// </summary>
internal static class NativeCursor
{
    private const int MaxSize = 256;

    /// <summary>커서와 그 핸들 — 다른 커서로 바꾼 뒤 <see cref="Handle" /> 를 해제한다.</summary>
    public sealed record Created(Cursor Cursor, SafeHandle Handle);

    /// <summary>
    ///     <paramref name="scale" /> 배(화면 배율 × DPI)로 최근접 확대·축소한 커서. 만들 수 없으면 null.
    /// </summary>
    public static Created? Create(byte[] bgra, int width, int height, int hotX, int hotY, double scale)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;

        var w = Math.Clamp((int)Math.Round(width * scale), 1, MaxSize);
        var h = Math.Clamp((int)Math.Round(height * scale), 1, MaxSize);
        var pixels = Resample(bgra, width, height, w, h);
        var hx = Math.Clamp((int)(hotX * (double)w / width), 0, w - 1);
        var hy = Math.Clamp((int)(hotY * (double)h / height), 0, h - 1);

        var handle = CreateHandle(pixels, w, h, hx, hy);
        if (handle.IsInvalid)
        {
            App.Log($"[콘솔] 커서 생성 실패({w}×{h}): {Marshal.GetLastWin32Error()}");
            handle.Dispose();
            return null;
        }

        return new Created(CursorInteropHelper.Create(handle), handle);
    }

    /// <summary>최근접 표본 — 커서 마스크가 0/255 라 번짐 없이 경계가 유지된다. 투명 픽셀은 색도 0.</summary>
    private static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var sy = Math.Min(sh - 1, y * sh / dh);
            for (var x = 0; x < dw; x++)
            {
                var sx = Math.Min(sw - 1, x * sw / dw);
                var s = (sy * sw + sx) * 4;
                var d = (y * dw + x) * 4;
                if (src[s + 3] == 0) continue; // 투명 — 0 그대로
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = src[s + 3];
            }
        }

        return dst;
    }

    private static CursorHandle CreateHandle(byte[] pixels, int w, int h, int hotX, int hotY)
    {
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = w, Height = -h, // 음수 = 위에서 아래로
            Planes = 1, BitCount = 32
        };
        var color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        // 단색 마스크(행은 16비트 정렬) — 알파 커서에서는 쓰이지 않지만 반드시 있어야 한다
        var mask = CreateBitmap(w, h, 1, 1, new byte[(w + 15) / 16 * 2 * h]);
        try
        {
            if (color == IntPtr.Zero || mask == IntPtr.Zero) return new CursorHandle();
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var info = new IconInfo { IsIcon = 0, HotspotX = hotX, HotspotY = hotY, Mask = mask, Color = color };
            var cursor = new CursorHandle();
            cursor.Set(CreateIconIndirect(ref info)); // 비트맵은 복사되므로 아래에서 지워도 된다
            return cursor;
        }
        finally
        {
            if (color != IntPtr.Zero) DeleteObject(color);
            if (mask != IntPtr.Zero) DeleteObject(mask);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon; // 0 = 커서
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    /// <summary>CreateIconIndirect 핸들 — 해제는 DestroyIcon.</summary>
    private sealed class CursorHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        public void Set(IntPtr value)
        {
            SetHandle(value);
        }

        protected override bool ReleaseHandle()
        {
            return DestroyIcon(handle);
        }
    }
}
