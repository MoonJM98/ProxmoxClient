using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace ProxmoxClient.Core.Vnc;

/// <summary>
///     픽셀 변환 핫루프 — RGB(3바이트) → 프레임버퍼 픽셀(uint, 메모리 순서 B,G,R,0xFF).
///     SSSE3(x64)·NEON(ARM64) 바이트 셔플로 한 번에 4픽셀, 나머지와 미지원 CPU 는 스칼라로 처리한다.
/// </summary>
internal static class PixelConvert
{
    /// <summary>
    ///     16바이트를 읽어 앞 12바이트(4픽셀)를 BGRX 로 재배치하는 셔플 표. 0x80 은 결과 0(알파 자리 — 뒤에서 OR).
    /// </summary>
    private static readonly Vector128<byte> RgbToBgrxShuffle =
        Vector128.Create((byte)2, 1, 0, 0x80, 5, 4, 3, 0x80, 8, 7, 6, 0x80, 11, 10, 9, 0x80);

    private static readonly Vector128<byte> AlphaMask = Vector128.Create(0xFF000000u).AsByte();

    public static uint ToBgrx(byte r, byte g, byte b)
    {
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    /// <summary><paramref name="destination" />.Length 개 픽셀을 변환한다(<paramref name="rgb" /> 는 그 3배 이상).</summary>
    public static void RgbToBgrx(ReadOnlySpan<byte> rgb, Span<uint> destination)
    {
        var count = destination.Length;
        var px = 0;
        if (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported)
        {
            ref var src = ref MemoryMarshal.GetReference(rgb);
            ref var dst = ref Unsafe.As<uint, byte>(ref MemoryMarshal.GetReference(destination));
            // 16바이트를 읽으므로 읽기 끝(px*3+16)이 원본 안에 있을 때까지만 벡터로
            for (; px * 3 + 16 <= rgb.Length && px + 4 <= count; px += 4)
            {
                var input = Vector128.LoadUnsafe(ref src, (nuint)(px * 3));
                var shuffled = Ssse3.IsSupported
                    ? Ssse3.Shuffle(input, RgbToBgrxShuffle)
                    : AdvSimd.Arm64.VectorTableLookup(input, RgbToBgrxShuffle);
                (shuffled | AlphaMask).StoreUnsafe(ref dst, (nuint)(px * 4));
            }
        }

        for (; px < count; px++)
        {
            var s = px * 3;
            destination[px] = ToBgrx(rgb[s], rgb[s + 1], rgb[s + 2]);
        }
    }

    /// <summary>1비트 팔레트(2색) 한 행 — 바이트 하나를 읽어 8픽셀을 쓴다(픽셀마다 위치 계산 없음).</summary>
    public static void Expand1Bit(ReadOnlySpan<byte> bits, Span<uint> destination, uint color0, uint color1)
    {
        var full = destination.Length / 8;
        for (var i = 0; i < full; i++)
        {
            var b = bits[i];
            var d = destination.Slice(i * 8, 8);
            d[0] = (b & 0x80) != 0 ? color1 : color0;
            d[1] = (b & 0x40) != 0 ? color1 : color0;
            d[2] = (b & 0x20) != 0 ? color1 : color0;
            d[3] = (b & 0x10) != 0 ? color1 : color0;
            d[4] = (b & 0x08) != 0 ? color1 : color0;
            d[5] = (b & 0x04) != 0 ? color1 : color0;
            d[6] = (b & 0x02) != 0 ? color1 : color0;
            d[7] = (b & 0x01) != 0 ? color1 : color0;
        }

        for (var px = full * 8; px < destination.Length; px++)
            destination[px] = ((bits[px >> 3] >> (7 - (px & 7))) & 1) != 0 ? color1 : color0;
    }
}
