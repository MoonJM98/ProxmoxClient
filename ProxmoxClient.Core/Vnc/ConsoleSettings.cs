namespace ProxmoxClient.Core.Vnc;

/// <summary>
///     콘솔(VNC/RDP/터미널) 사용자 설정 — <see cref="ConsoleSettingsStore" /> 가 JSON 으로 저장한다.
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

    /// <summary>
    ///     콘솔 위 커서 표시 방식.
    ///     <list type="bullet">
    ///         <item><see cref="Both" /> — PC 화살표와 게스트 커서를 함께 보인다(게스트가 보낸 모양은 겹쳐 그림).</item>
    ///         <item><see cref="Arrow" />·<see cref="Dot" /> — PC 커서 하나만: 게스트가 모양을 보내면 그 모양,
    ///             안 보내면(표준 VGA 등 — 게스트가 화면에 직접 그림) 화살표·점.</item>
    ///         <item><see cref="Hidden" /> — 게스트 커서만: 모양을 보내면 그 모양을 PC 커서로, 아니면 PC 커서 숨김.</item>
    ///     </list>
    /// </summary>
    public enum LocalCursorMode
    {
        Both,
        Arrow,
        Dot,
        Hidden
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

    /// <summary>커서 표시 방식(기본: 둘 다 — PC 화살표 + 게스트 커서).</summary>
    public LocalCursorMode LocalCursor { get; init; } = LocalCursorMode.Both;

    /// <summary>맞춤 모드 축소 시 Linear 보간 사용.</summary>
    public bool SmoothScaling { get; init; } = true;

    /// <summary>
    ///     클립보드 자동 동기화(VM 이 clipboard=vnc 일 때) — 콘솔 창으로 돌아오면 PC → 게스트, 떠나면 게스트 → PC.
    /// </summary>
    public bool AutoClipboardSync { get; init; } = true;

    /// <summary>
    ///     RDP 커서 표시 방식(기본: PC 커서만) — RDP 서버는 커서 모양을 늘 보내므로 게스트가 화면에 커서를 그리지 않는다.
    /// </summary>
    public LocalCursorMode RdpLocalCursor { get; init; } = LocalCursorMode.Arrow;

    /// <summary>RDP 맞춤 모드 축소 시 Linear 보간 사용.</summary>
    public bool RdpSmoothScaling { get; init; } = true;

    /// <summary>RDP 클립보드 공유(cliprdr) — PC 와 게스트 클립보드를 자동으로 주고받는다. 연결 시 적용.</summary>
    public bool RdpClipboard { get; init; } = true;

    /// <summary>RDP 게스트 해상도를 콘솔 창 크기에 맞춘다(디스플레이 제어 채널).</summary>
    public bool RdpDynamicResolution { get; init; } = true;

    /// <summary>CT 터미널 글꼴.</summary>
    public string TerminalFontFamily { get; init; } = DefaultFontFamily;

    /// <summary>CT 터미널 글자 크기(pt).</summary>
    public int TerminalFontSize { get; init; } = 13;

    public static int ClampLevel(int level)
    {
        return Math.Clamp(level, MinLevel, MaxLevel);
    }

    /// <summary>게스트 파일 창에서 숨김·시스템 항목을 보인다(흐리게).</summary>
    public bool ShowHiddenGuestFiles { get; init; } = true;

    /// <summary>범위를 벗어난 값·빈 글꼴·정의되지 않은 열거값을 교정한 새 인스턴스.</summary>
    public ConsoleSettings Normalize()
    {
        return this with
        {
            Encoding = Enum.IsDefined(Encoding) ? Encoding : VncEncoding.Tight,
            LocalCursor = Enum.IsDefined(LocalCursor) ? LocalCursor : LocalCursorMode.Both,
            RdpLocalCursor = Enum.IsDefined(RdpLocalCursor) ? RdpLocalCursor : LocalCursorMode.Arrow,
            QualityLevel = ClampLevel(QualityLevel),
            CompressionLevel = ClampLevel(CompressionLevel),
            TerminalFontFamily = string.IsNullOrWhiteSpace(TerminalFontFamily)
                ? DefaultFontFamily
                : TerminalFontFamily.Trim(),
            TerminalFontSize = Math.Clamp(TerminalFontSize, MinFontSize, MaxFontSize)
        };
    }
}