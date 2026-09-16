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

    [JsonIgnore] public TimeSpan RefreshInterval => TimeSpan.FromSeconds(RefreshIntervalSeconds);

    public AppSettings Normalize()
    {
        return this with
        {
            RefreshIntervalSeconds =
            Math.Clamp(RefreshIntervalSeconds, MinRefreshIntervalSeconds, MaxRefreshIntervalSeconds),
            ByteUnit = Enum.IsDefined(ByteUnit) ? ByteUnit : ByteDisplayUnit.Auto,
            Language = string.IsNullOrWhiteSpace(Language) ? AutoLanguage : Language.Trim()
        };
    }
}

/// <summary>%APPDATA%\ProxmoxClient\app-settings.json 저장소.</summary>
public sealed class AppSettingsStore(string? path = null)
    : JsonSettingsStore<AppSettings>(path ?? DefaultPath, settings => settings.Normalize())
{
    public static string DefaultPath => InAppData("app-settings.json");
}