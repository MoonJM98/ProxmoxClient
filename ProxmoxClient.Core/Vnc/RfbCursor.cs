namespace ProxmoxClient.Core.Vnc;

/// <summary>
///     서버가 보낸 커서 모양(RichCursor 의사 인코딩) — BGRA 픽셀(마스크를 알파로 적용), 크기, 핫스팟.
///     크기가 0 이면 게스트가 커서를 숨긴 것이다.
/// </summary>
public sealed record RfbCursor(byte[] Pixels, int Width, int Height, int HotX, int HotY)
{
    public bool IsEmpty => Width == 0 || Height == 0;
}
