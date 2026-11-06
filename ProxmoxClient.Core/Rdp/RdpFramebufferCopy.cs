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
}

/// <summary>
///     IronRDP 디코딩 버퍼 → 프레임버퍼 복사. 화면 갱신이 올 때마다(수신 스레드, 잠금 안에서) 그 영역만 라이브러리의
///     <c>CopyRectInto</c> 로 옮긴다 — 창(UI 스레드)은 옮겨진 프레임버퍼를 그대로 읽는다.
/// </summary>
public sealed partial class ProxmoxRdpSession
{
    /// <summary>(잠금 안에서) 디코딩 버퍼의 <paramref name="rect" /> 영역을 프레임버퍼(BGRA, 행 간격 = 가로 × 4)로 옮긴다.</summary>
    private void CopyRect(InclusiveRectangle rect)
    {
        if (_image is not { } image || _framebuffer is not { } framebuffer || _width <= 0) return;

        try
        {
            image.CopyRectInto(rect, framebuffer, (nuint)(_width * 4));
        }
        catch (IronRdpException)
        {
            // 영역이 화면 밖 등 — 전체를 옮긴다
            if ((long)image.DataSize == framebuffer.Length) image.FillData(framebuffer);
        }
    }
}
