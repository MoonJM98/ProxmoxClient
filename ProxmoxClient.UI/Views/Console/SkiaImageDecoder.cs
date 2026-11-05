using SkiaSharp;

namespace ProxmoxClient.UI.Views.Console;

/// <summary>Tight JPEG/PNG 영역 디코더(Skia) — 데스크톱·모바일 공용. 프레임버퍼(BGRX)에 바로 써 넣는다.</summary>
internal static class SkiaImageDecoder
{
    public static void Decode(byte[] data, int length, byte[] framebuffer, int framebufferWidth, int x, int y,
        int width, int height)
    {
        using var bitmap = SKBitmap.Decode(data.AsSpan(0, length));
        if (bitmap is null) return;

        using var bgra = bitmap.ColorType == SKColorType.Bgra8888
            ? null
            : bitmap.Copy(SKColorType.Bgra8888);
        var source = bgra ?? bitmap;
        var pixels = source.GetPixelSpan();
        var rows = Math.Min(height, source.Height);
        var rowBytes = Math.Min(width, source.Width) * 4;
        for (var row = 0; row < rows; row++)
        {
            var dst = ((y + row) * framebufferWidth + x) * 4;
            if (dst + rowBytes > framebuffer.Length) break;

            pixels.Slice(row * source.RowBytes, rowBytes).CopyTo(framebuffer.AsSpan(dst, rowBytes));
        }
    }
}
