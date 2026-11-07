using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Core.Settings;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.Core;

// 설정·프로필 파일의 JSON 직렬화 코드를 빌드 때 만들어 둔다(리플렉션 없이 — 트리밍·AOT 에도 안전).
// 옵션은 각 저장소가 쓰던 JsonSerializerOptions 와 같게 맞춰, 파일 모양이 바뀌지 않는다.

/// <summary>JsonSettingsStore 설정 파일(app-settings.json·console-settings.json) — 열거형은 문자열.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ConsoleSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>연결 프로필 파일(profiles/*.json) — 열거형은 숫자(기존 파일과 같게).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ConnectionProfile))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;

/// <summary>마지막 로그인(last-login.json) — 기본 옵션.</summary>
[JsonSerializable(typeof(LastLogin))]
internal sealed partial class LastLoginJsonContext : JsonSerializerContext;

internal static class CoreJson
{
    /// <summary>
    ///     설정 형식 T 의 생성된 직렬화 정보. 목록(<see cref="SettingsJsonContext" />)에 없는 새 설정 형식이면
    ///     기존처럼 리플렉션으로 처리한다 — 그 경우 SettingsJsonContext 에 형식을 더해야 트리밍에 안전하다.
    /// </summary>
    public static JsonTypeInfo<T> Settings<T>(JsonSerializerOptions fallback)
    {
        return SettingsJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T> ?? Reflection<T>(fallback);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "목록에 없는 형식만 — 위 설명 참조")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "목록에 없는 형식만 — 위 설명 참조")]
    private static JsonTypeInfo<T> Reflection<T>(JsonSerializerOptions options)
    {
        options.MakeReadOnly(true);
        return (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
    }
}
