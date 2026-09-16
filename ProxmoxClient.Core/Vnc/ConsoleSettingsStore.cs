using ProxmoxClient.Core.Settings;

namespace ProxmoxClient.Core.Vnc;

/// <summary>%APPDATA%\ProxmoxClient\console-settings.json 저장소. 읽기 실패 시 기본값을 돌려주며 예외를 던지지 않는다.</summary>
public sealed class ConsoleSettingsStore(string? path = null)
    : JsonSettingsStore<ConsoleSettings>(path ?? DefaultPath, settings => settings.Normalize())
{
    public static string DefaultPath => InAppData("console-settings.json");
}