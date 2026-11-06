using Devolutions.IronRdp;

namespace ProxmoxClient.Core.Rdp;

/// <summary>바뀐 영역(포함 좌표).</summary>
internal readonly record struct DirtyRect(int Left, int Top, int Right, int Bottom)
{
    public DirtyRect Union(DirtyRect other)
    {
        return new DirtyRect(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right),
            Math.Max(Bottom, other.Bottom));
    }

    /// <summary>화면 안으로 자른다 — 화면 밖이면 null.</summary>
    public DirtyRect? Clip(int width, int height)
    {
        var r = new DirtyRect(Math.Max(Left, 0), Math.Max(Top, 0), Math.Min(Right, width - 1),
            Math.Min(Bottom, height - 1));
        return r.Left > r.Right || r.Top > r.Bottom ? null : r;
    }
}

/// <summary>
///     IronRDP 디코딩 버퍼 → 프레임버퍼 복사. 바인딩은 전체 복사(Fill)만 있어서, 버퍼 조각(Rust <c>&amp;[u8]</c> =
///     포인터·길이)에서 포인터를 읽어 바뀐 줄만 옮긴다. 조각 모양이 예상과 다르면(길이가 맞지 않음) 전체 복사로 돌아간다.
/// </summary>
public sealed partial class ProxmoxRdpSession
{
    /// <summary>(잠금 안에서) 디코딩 버퍼의 <paramref name="rect" /> 영역을 프레임버퍼(BGRA, 행 간격 = 가로 × 4)로 옮긴다.</summary>
    private static unsafe void CopyFromImage(DecodedImage image, byte[] framebuffer, int width, DirtyRect rect)
    {
        using var data = image.GetData();
        var size = (nint)data.GetSize();
        if (size != framebuffer.Length || width <= 0) return;

        var source = SliceData(data, size);
        if (source is null || rect.Clip(width, framebuffer.Length / 4 / width) is not { } r)
        {
            data.Fill(framebuffer);
            return;
        }

        var stride = width * 4;
        fixed (byte* target = framebuffer)
        {
            if (r.Left == 0 && r.Right == width - 1)
            {
                // 가로 전체 — 연속된 한 덩어리
                var offset = (long)r.Top * stride;
                var length = (long)(r.Bottom - r.Top + 1) * stride;
                Buffer.MemoryCopy(source + offset, target + offset, length, length);
                return;
            }

            var rowBytes = (r.Right - r.Left + 1) * 4;
            for (var y = r.Top; y <= r.Bottom; y++)
            {
                var offset = (long)y * stride + r.Left * 4;
                Buffer.MemoryCopy(source + offset, target + offset, rowBytes, rowBytes);
            }
        }
    }

    /// <summary>바뀐 영역만 옮길 수 있는가(버퍼 조각 모양을 알아본다) — 검사·진단용.</summary>
    public static unsafe bool SupportsPartialCopy()
    {
        using var image = DecodedImage.New(PixelFormat.BgrA32, 16, 8);
        using var data = image.GetData();
        return SliceData(data, (nint)data.GetSize()) is not null;
    }

    /// <summary>
    ///     버퍼 조각의 데이터 포인터 — 조각 객체는 Rust 슬라이스(포인터, 길이 두 칸)를 감싼다. 한 칸이 길이와 같고
    ///     다른 칸이 0 이 아니면 그 칸을 포인터로 쓴다. 알아볼 수 없으면 null(전체 복사).
    /// </summary>
    private static unsafe byte* SliceData(BytesSlice data, nint size)
    {
        var words = (nint*)data.AsFFI();
        if (words is null) return null;

        if (words[1] == size && words[0] != 0 && words[0] != size) return (byte*)words[0];
        if (words[0] == size && words[1] != 0 && words[1] != size) return (byte*)words[1];
        return null;
    }
}
