namespace ProxmoxClient.Core.Vnc;

/// <summary>
///     Tight JPEG/PNG rect 디코더 — 압축 이미지 <paramref name="data" />[0..length] 를 프레임버퍼의 (x, y) 위치에
///     BGRX(4바이트/px, 행 간격 framebufferWidth*4) 로 직접 기록한다. 중간 픽셀 배열을 만들지 않는다.
/// </summary>
public delegate void TightImageDecoder(
    byte[] data, int length, byte[] framebuffer, int framebufferWidth, int x, int y, int width, int height);

/// <summary>RFB 의사 인코딩 번호와 값 변환 헬퍼 (rfbproto / noVNC encodings.js 기준).</summary>
public static class RfbEncodings
{
    public const int QualityLevel0 = -32;
    public const int CompressLevel0 = -256;
    public const int QemuExtendedKeyEvent = -258;

    /// <summary>품질 레벨(0-9, 범위 밖은 보정) → Tight 품질 의사 인코딩.</summary>
    public static int QualityLevel(int level)
    {
        return QualityLevel0 + ConsoleSettings.ClampLevel(level);
    }

    /// <summary>압축 레벨(0-9, 범위 밖은 보정) → 압축 레벨 의사 인코딩.</summary>
    public static int CompressLevel(int level)
    {
        return CompressLevel0 + ConsoleSettings.ClampLevel(level);
    }

    /// <summary>
    ///     XT 스캔코드(확장 키는 0xE0nn) → QEMU 확장 키 이벤트 keycode.
    ///     0xE0 접두 키는 하위 7비트에 0x80 을 더한 한 바이트로 표현한다.
    /// </summary>
    public static int ToQemuKeycode(int xtScanCode)
    {
        return (xtScanCode & 0xFF00) == 0xE000 ? (xtScanCode & 0x7F) | 0x80 : xtScanCode & 0xFF;
    }
}