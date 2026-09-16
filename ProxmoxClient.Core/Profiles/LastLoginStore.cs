using System.Text.Json;
using ProxmoxClient.Core.Settings;

namespace ProxmoxClient.Core.Profiles;

/// <summary>마지막 로그인 정보(서버 프로필 ID + 인증 영역). 자격 증명은 포함하지 않는다.</summary>
public sealed record LastLogin(Guid ProfileId, string Realm);

/// <summary>
///     마지막으로 로그인한 서버/인증 영역을 %APPDATA%\ProxmoxClient\last-login.json 에 기록한다.
///     읽기 실패는 null 로 처리하며 예외를 던지지 않는다(손상 파일은 사본 보존). 저장은 원자적(.bak 유지).
/// </summary>
public sealed class LastLoginStore
{
    private readonly string _path;

    public LastLoginStore(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProxmoxClient",
        "last-login.json");

    public async Task<LastLogin?> LoadAsync(CancellationToken ct = default)
    {
        string raw;
        try
        {
            if (!File.Exists(_path)) return null;

            raw = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LastLogin>(raw);
        }
        catch (JsonException)
        {
            AtomicFile.PreserveCorrupt(_path);
            return null;
        }
    }

    public Task SaveAsync(LastLogin value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AtomicFile.WriteAllTextAsync(_path, JsonSerializer.Serialize(value), ct: ct);
    }
}