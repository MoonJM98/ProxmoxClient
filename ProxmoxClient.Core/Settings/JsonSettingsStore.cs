using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxmoxClient.Core.Settings;

/// <summary>
///     %APPDATA%\ProxmoxClient 아래 JSON 설정 파일 공통 저장소.
///     읽기 실패(없음·권한) 시 기본값을 돌려주며 예외를 던지지 않는다. 손상 파일은 사본을 남긴 뒤 기본값을 돌려주고,
///     원본은 사용자가 저장할 때까지 건드리지 않는다. 저장은 <see cref="AtomicFile" /> 로 원자적으로(.bak 백업 유지) 수행한다.
///     읽기·저장 모두 <c>normalize</c> 를 거쳐 범위를 벗어난 값이 앱으로 들어오거나 디스크에 남지 않는다.
/// </summary>
public abstract class JsonSettingsStore<T> where T : class, new()
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Func<T, T> _normalize;

    private readonly string _path;

    protected JsonSettingsStore(string path, Func<T, T> normalize)
    {
        _path = path;
        _normalize = normalize;
    }

    /// <summary>%APPDATA%\ProxmoxClient\{fileName}</summary>
    public static string InAppData(string fileName)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ProxmoxClient",
            fileName);
    }

    public async Task<T> LoadAsync(CancellationToken ct = default)
    {
        string raw;
        try
        {
            if (!File.Exists(_path)) return _normalize(new T());

            raw = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return _normalize(new T()); // 일시적 읽기 실패 — 파일은 그대로 둔다
        }

        try
        {
            return _normalize(JsonSerializer.Deserialize<T>(raw, SerializerOptions) ?? new T());
        }
        catch (JsonException)
        {
            // 손상 파일 사본 보존 — 사용자가 기본값 화면에서 저장해 원본이 교체돼도 내용을 복구할 수 있다
            AtomicFile.PreserveCorrupt(_path);
            return _normalize(new T());
        }
    }

    public Task SaveAsync(T settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(_normalize(settings), SerializerOptions);
        return AtomicFile.WriteAllTextAsync(_path, json, ct: ct);
    }
}