using System.Buffers;
using System.Runtime.InteropServices;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Vnc;

/// <summary>Tight 인코딩 해석 — 채우기·JPEG·팔레트·그라디언트 필터.</summary>
public sealed partial class RfbClient
{
    /// <summary>압축 조각을 풀 버퍼로 읽어 해제기에 소유권째 넘긴다(해제기가 소비 후 반환) — 중간 복사 없음.</summary>
    private async ValueTask ReadCompressedChunkAsync(ZlibContinuousInflate inflate, int length, CancellationToken ct)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactlyAsync(chunk, 0, length, ct).ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(chunk); // 소유권 이전 전 실패 — 여기서 반환 후 예외 전파
            throw;
        }

        inflate.AddCompressedChunk(chunk, length);
    }
    private async ValueTask<int> ReadTightLengthAsync(CancellationToken ct)
    {
        var b0 = await ReadByteAsync(ct).ConfigureAwait(false);
        if ((b0 & 0x80) == 0) return b0;

        var b1 = await ReadByteAsync(ct).ConfigureAwait(false);
        if ((b1 & 0x80) == 0) return ((b1 & 0x7F) << 7) | (b0 & 0x7F);

        var b2 = await ReadByteAsync(ct).ConfigureAwait(false);
        return (b2 << 14) | ((b1 & 0x7F) << 7) | (b0 & 0x7F);
    }
    private async Task HandleTightFillAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        await ReadExactlyAsync(_scratch, 0, 3, ct).ConfigureAwait(false); // RGB — 읽기 루프 전용 스크래치 재사용
        EnsureFramebuffer(FramebufferWidth, FramebufferHeight);
        FillRect(Framebuffer, FramebufferWidth, x, y, w, h, _scratch[0], _scratch[1], _scratch[2]);
    }
    /// <summary>단색 사각형 — BGRX 픽셀 하나를 uint 로 만들어 행마다 Span.Fill(임시 행 배열 없음, SIMD 가속).</summary>
    private static void FillRect(byte[] framebuffer, int framebufferWidth, int x, int y, int w, int h, byte r, byte g,
        byte b)
    {
        var pixel = ToBgrx(r, g, b);
        var pixels = MemoryMarshal.Cast<byte, uint>(framebuffer.AsSpan());
        for (var row = 0; row < h; row++) pixels.Slice((y + row) * framebufferWidth + x, w).Fill(pixel);
    }
    private async Task HandleTightImageAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        var length = await ReadTightLengthAsync(ct).ConfigureAwait(false);
        var data = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactlyAsync(data, 0, length, ct).ConfigureAwait(false);
            EnsureFramebuffer(FramebufferWidth, FramebufferHeight);
            var decoder = ImageDecoder ?? throw new IOException(Res.T("RfbClient_16"));

            // 풀에서 빌린 버퍼를 복사 없이 넘기고, 디코더가 프레임버퍼 위치에 직접 기록한다
            decoder(data, length, Framebuffer, FramebufferWidth, x, y, w, h);
            Interlocked.Increment(ref _tightImageRects);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }
    private async Task HandleTightBasicAsync(int streamId, int filter, int x, int y, int w, int h, CancellationToken ct)
    {
        var size = w * h * 3;
        if (size == 0) return;

        byte[]? palette = null;
        var numColors = 0;
        var bitsPerPixel = 0;
        try
        {
            if (filter == 1)
            {
                numColors = await ReadByteAsync(ct).ConfigureAwait(false) + 1;
                palette = ArrayPool<byte>.Shared.Rent(numColors * 3); // 최대 256색×3 — 풀 재사용
                await ReadExactlyAsync(palette, 0, numColors * 3, ct).ConfigureAwait(false);
                bitsPerPixel = numColors <= 2 ? 1 : 8;
                var rowSize = (w * bitsPerPixel + 7) / 8;
                size = rowSize * h;
            }

            var data = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                if (size < 12)
                {
                    await ReadExactlyAsync(data, 0, size, ct).ConfigureAwait(false);
                }
                else
                {
                    var length = await ReadTightLengthAsync(ct).ConfigureAwait(false);
                    await ReadCompressedChunkAsync(_tightInflates[streamId], length, ct).ConfigureAwait(false);
                    var produced = _tightInflates[streamId].Decompress(data, 0, size);
                    if (produced < size) throw new IOException(Res.T("RfbClient_17", produced, size));
                }

                ApplyTightFilter(filter, data, palette, numColors, bitsPerPixel, x, y, w, h);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }
        finally
        {
            if (palette is not null) ArrayPool<byte>.Shared.Return(palette);
        }
    }
    private void ApplyTightFilter(int filter, byte[] data, byte[]? palette, int paletteColors, int bitsPerPixel, int x,
        int y, int w, int h)
    {
        EnsureFramebuffer(FramebufferWidth, FramebufferHeight);

        switch (filter)
        {
            case 0:
                WriteRgbToFramebuffer(data, x, y, w, h);
                break;
            case 1:
                WritePaletteToFramebuffer(data, palette!, paletteColors, bitsPerPixel, x, y, w, h);
                break;
            case 2:
                ReverseGradientFilter(data.AsSpan(0, w * h * 3), w, h);
                WriteRgbToFramebuffer(data, x, y, w, h);
                break;
            default:
                throw new IOException(Res.T("RfbClient_18", filter));
        }
    }
    private static uint ToBgrx(byte r, byte g, byte b)
    {
        return PixelConvert.ToBgrx(r, g, b);
    }
    /// <summary>
    ///     팔레트 인덱스 → 픽셀. 팔레트를 한 번만 uint 색으로 변환(스택)해 픽셀당 바이트 4회 쓰기를 uint 1회로 줄인다.
    ///     잘못된 인덱스가 와도 256 칸 표(0 초기화) 안이므로 범위를 벗어나지 않는다.
    /// </summary>
    private void WritePaletteToFramebuffer(
        byte[] data, byte[] palette, int paletteColors, int bitsPerPixel, int x, int y, int w, int h)
    {
        const int MaxPaletteColors = 256;
        Span<uint> colors = stackalloc uint[MaxPaletteColors];
        var colorCount = Math.Min(paletteColors, MaxPaletteColors);
        for (var i = 0; i < colorCount; i++) colors[i] = ToBgrx(palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2]);

        var pixels = MemoryMarshal.Cast<byte, uint>(Framebuffer.AsSpan());
        var rowSize = (w * bitsPerPixel + 7) / 8;
        for (var r = 0; r < h; r++)
        {
            var src = data.AsSpan(r * rowSize, rowSize);
            var dst = pixels.Slice((y + r) * FramebufferWidth + x, w);
            if (bitsPerPixel == 1)
                PixelConvert.Expand1Bit(src, dst, colors[0], colors[1]);
            else
                for (var px = 0; px < dst.Length; px++)
                    dst[px] = colors[src[px]];
        }
    }
    /// <summary>
    ///     Gradient 역필터(Tight): 예측 = clamp(left + up - upLeft, 0, 255), 경계 밖 이웃은 0. 제자리 복원.
    ///     첫 행·첫 픽셀 경계 처리를 루프 밖으로 빼 픽셀·채널마다 있던 분기 3개를 제거했다.
    /// </summary>
    private static void ReverseGradientFilter(Span<byte> data, int w, int h)
    {
        const int Channels = 3;
        var stride = w * Channels;

        // 첫 행: up = upLeft = 0 → 예측 = left
        var first = data[..stride];
        for (var i = Channels; i < first.Length; i++) first[i] = (byte)(first[i] + first[i - Channels]);

        for (var r = 1; r < h; r++)
        {
            var row = data.Slice(r * stride, stride);
            var prev = data.Slice((r - 1) * stride, stride);

            // 첫 픽셀: left = upLeft = 0 → 예측 = up
            for (var c = 0; c < Channels; c++) row[c] = (byte)(row[c] + prev[c]);

            for (var i = Channels; i < row.Length; i++)
            {
                var prediction = row[i - Channels] + prev[i] - prev[i - Channels];
                row[i] = (byte)(row[i] + (prediction < 0 ? 0 : prediction > 255 ? 255 : prediction));
            }
        }
    }
    /// <summary>RGB(3바이트/px) → 프레임버퍼 행에 uint 단위로 기록.</summary>
    private void WriteRgbToFramebuffer(byte[] rgb, int x, int y, int w, int h)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(Framebuffer.AsSpan());
        var srcStride = w * 3;
        for (var r = 0; r < h; r++)
        {
            // 행 끝까지 넘겨 벡터 경로가 다음 행 앞 바이트까지 읽을 수 있게 한다(쓰기는 w 픽셀만)
            var src = rgb.AsSpan(r * srcStride);
            PixelConvert.RgbToBgrx(src, pixels.Slice((y + r) * FramebufferWidth + x, w));
        }
    }
}
