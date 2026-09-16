namespace ProxmoxClient.Core.Vnc;

/// <summary>
///     콘솔(VNC/터미널) 사용자 설정 — <see cref="ConsoleSettingsStore" /> 가 JSON 으로 저장한다.
///     불변 record: 변경은 <c>with</c> 로 새 인스턴스를 만들고, 외부 입력은 항상 <see cref="Normalize" /> 를 거친다.
/// </summary>
public sealed record ConsoleSettings
{
    public enum VncEncoding
    {
        Tight,
        Zlib,
        Raw
    }

    /// <summary>Tight 품질/압축 레벨 최솟값 (RFB 의사 인코딩 정의 범위).</summary>
    public const int MinLevel = 0;

    /// <summary>Tight 품질/압축 레벨 최댓값 (RFB 의사 인코딩 정의 범위 — 10단계).</summary>
    public const int MaxLevel = 9;

    public const int MinFontSize = 8;
    public const int MaxFontSize = 32;
    public const string DefaultFontFamily = "Cascadia Mono";

    /// <summary>선호 인코딩 (기본: Tight).</summary>
    public VncEncoding Encoding { get; init; } = VncEncoding.Tight;

    /// <summary>Tight JPEG 품질 레벨 0-9 (높을수록 고화질·대역폭 증가).</summary>
    public int QualityLevel { get; init; } = 6;

    /// <summary>zlib 압축 레벨 0-9 (높을수록 대역폭 감소·CPU 증가).</summary>
    public int CompressionLevel { get; init; } = 6;

    /// <summary>QEMU 확장 키 이벤트(스캔코드 전송) 사용 — 서버 미지원 시 keysym 으로 자동 대체.</summary>
    public bool UseQemuExtendedKeys { get; init; } = true;

    /// <summary>맞춤 모드 축소 시 Linear 보간 사용.</summary>
    public bool SmoothScaling { get; init; } = true;

    /// <summary>CT 터미널 글꼴.</summary>
    public string TerminalFontFamily { get; init; } = DefaultFontFamily;

    /// <summary>CT 터미널 글자 크기(pt).</summary>
    public int TerminalFontSize { get; init; } = 13;

    public static int ClampLevel(int level)
    {
        return Math.Clamp(level, MinLevel, MaxLevel);
    }

    /// <summary>범위를 벗어난 값·빈 글꼴·정의되지 않은 열거값을 교정한 새 인스턴스.</summary>
    public ConsoleSettings Normalize()
    {
        return this with
        {
            Encoding = Enum.IsDefined(Encoding) ? Encoding : VncEncoding.Tight,
            QualityLevel = ClampLevel(QualityLevel),
            CompressionLevel = ClampLevel(CompressionLevel),
            TerminalFontFamily = string.IsNullOrWhiteSpace(TerminalFontFamily)
                ? DefaultFontFamily
                : TerminalFontFamily.Trim(),
            TerminalFontSize = Math.Clamp(TerminalFontSize, MinFontSize, MaxFontSize)
        };
    }
}