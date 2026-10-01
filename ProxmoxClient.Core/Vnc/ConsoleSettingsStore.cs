using ProxmoxClient.Core.Settings;

namespace ProxmoxClient.Core.Vnc;

/// <summary>%APPDATA%\ProxmoxClient\console-settings.json 저장소. 읽기 실패 시 기본값을 돌려주며 예외를 던지지 않는다.</summary>
public sealed class ConsoleSettingsStore(string? path = null)
    : JsonSettingsStore<ConsoleSettings>(path ?? DefaultPath, settings => settings.Normalize())
{
    private static readonly SemaphoreSlim Updates = new(1, 1);

    /// <summary>여러 콘솔 창이 서로의 게스트 매핑·서버 키·표시 설정을 덮어쓰지 않게 수정한다.</summary>
    public async Task<ConsoleSettings> UpdateAsync(Func<ConsoleSettings, ConsoleSettings> update,
        CancellationToken ct = default)
    {
        await Updates.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = update(await LoadAsync(ct).ConfigureAwait(false)).Normalize();
            await SaveAsync(settings, ct).ConfigureAwait(false);
            return settings;
        }
        finally { Updates.Release(); }
    }

    public static string DefaultPath => InAppData("console-settings.json");
}
