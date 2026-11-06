using System.Runtime.InteropServices;
using SkiaSharp;

namespace ProxmoxClient.UI.Views.Console;

/// <summary>
///     Tight JPEG/PNG 영역 디코더(Skia) — 데스크톱·모바일 공용. 프레임버퍼(BGRX)의 그 자리에 BGRA 로 바로 풀어 넣는다
///     (중간 비트맵·형식 변환 복사 없음 — 행 간격을 프레임버퍼 가로로 주면 Skia 가 행마다 제자리에 쓴다).
/// </summary>
internal static class SkiaImageDecoder
{
    public static void Decode(byte[] data, int length, byte[] framebuffer, int framebufferWidth, int x, int y,
        int width, int height)
    {
        if (length <= 0 || width <= 0 || height <= 0) return;

        // 압축 데이터도 복사하지 않고 감싸기만 한다(디코드가 끝날 때까지 고정)
        var source = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            using var skData = SKData.Create(source.AddrOfPinnedObject(), length, null);
            using var codec = skData is null ? null : SKCodec.Create(skData);
            if (codec is null) return;

            var info = codec.Info;
            var w = Math.Min(width, info.Width);
            var h = Math.Min(height, info.Height);
            var stride = framebufferWidth * 4;
            var target = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888,
                info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul);

            // 영역과 크기가 같고 프레임버퍼 안이면 바로 그 자리에 — Tight 는 늘 이 경우다
            if (info.Width == width && info.Height == height && x >= 0 && y >= 0 && x + width <= framebufferWidth
                && (long)(y + height - 1) * stride + (long)(x + width) * 4 <= framebuffer.Length)
            {
                var fb = GCHandle.Alloc(framebuffer, GCHandleType.Pinned);
                try
                {
                    codec.GetPixels(target, fb.AddrOfPinnedObject() + (nint)((long)y * stride + x * 4), stride, SKCodecOptions.Default);
                }
                finally
                {
                    fb.Free();
                }

                return;
            }

            // 크기가 다른 이상한 영역 — 따로 풀어 겹치는 부분만 복사
            var rowBytes = info.Width * 4;
            var pixels = new byte[rowBytes * info.Height];
            var result = codec.GetPixels(target, pixels);
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return;

            for (var row = 0; row < h; row++)
            {
                var dst = ((y + row) * framebufferWidth + x) * 4;
                if (dst < 0 || dst + w * 4 > framebuffer.Length) break;

                pixels.AsSpan(row * rowBytes, w * 4).CopyTo(framebuffer.AsSpan(dst, w * 4));
            }
        }
        finally
        {
            source.Free();
        }
    }
}
