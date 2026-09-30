using System.Text.Json.Serialization;

namespace ProxmoxClient.Core.Settings;

/// <summary>용량 표시 단위 — 값은 1024 거듭제곱 지수(KB=1 … PB=5), Auto 는 값 크기에 맞춰 선택.</summary>
public enum ByteDisplayUnit
{
    Auto = 0,
    KB = 1,
    MB = 2,
    GB = 3,
    TB = 4,
    PB = 5
}

/// <summary>앱 전역 설정 — 불변 record, 외부 입력은 항상 <see cref="Normalize" /> 를 거친다.</summary>
public sealed record AppSettings
{
    public const int MinRefreshIntervalSeconds = 1;
    public const int MaxRefreshIntervalSeconds = 60;
    public const int DefaultRefreshIntervalSeconds = 1;

    /// <summary>시스템 언어를 따르는 값.</summary>
    public const string AutoLanguage = "auto";

    /// <summary>자동 새로고침 간격(초).</summary>
    public int RefreshIntervalSeconds { get; init; } = DefaultRefreshIntervalSeconds;

    /// <summary>앱 시작(연결) 시 자동 새로고침을 켤지.</summary>
    public bool AutoRefreshOnStart { get; init; } = true;

    /// <summary>용량(메모리·디스크·IO) 표시 단위.</summary>
    public ByteDisplayUnit ByteUnit { get; init; } = ByteDisplayUnit.Auto;

    /// <summary>표시 언어 코드("auto" = Windows 표시 언어, 그 외 "ko"·"en" 등 문화권 이름).</summary>
    public string Language { get; init; } = AutoLanguage;

    /// <summary>게스트 목록에 템플릿을 보일지 — 숨기면 템플릿 열도 숨긴다.</summary>
    public bool ShowTemplates { get; init; } = true;

    /// <summary>메인 창 게스트 화면의 위쪽 상세 영역 높이(손잡이로 조절, px).</summary>
    public double GuestDetailHeight { get; init; } = DefaultGuestDetailHeight;

    /// <summary>메인 창 노드 화면의 왼쪽 노드 목록 너비(손잡이로 조절, px).</summary>
    public double NodeListWidth { get; init; } = DefaultNodeListWidth;

    public const double DefaultGuestDetailHeight = 300;
    public const double DefaultNodeListWidth = 290;

    [JsonIgnore] public TimeSpan RefreshInterval => TimeSpan.FromSeconds(RefreshIntervalSeconds);

    public AppSettings Normalize()
    {
        return this with
        {
            RefreshIntervalSeconds =
            Math.Clamp(RefreshIntervalSeconds, MinRefreshIntervalSeconds, MaxRefreshIntervalSeconds),
            ByteUnit = Enum.IsDefined(ByteUnit) ? ByteUnit : ByteDisplayUnit.Auto,
            Language = string.IsNullOrWhiteSpace(Language) ? AutoLanguage : Language.Trim(),
            // 잘못 저장된 값(0·음수·지나치게 큼)은 기본값으로
            GuestDetailHeight = GuestDetailHeight is >= 110 and <= 2000 ? GuestDetailHeight : DefaultGuestDetailHeight,
            NodeListWidth = NodeListWidth is >= 180 and <= 2000 ? NodeListWidth : DefaultNodeListWidth
        };
    }
}

/// <summary>%APPDATA%\ProxmoxClient\app-settings.json 저장소.</summary>
public sealed class AppSettingsStore(string? path = null)
    : JsonSettingsStore<AppSettings>(path ?? DefaultPath, settings => settings.Normalize())
{
    public static string DefaultPath => InAppData("app-settings.json");
}