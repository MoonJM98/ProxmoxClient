namespace ProxmoxClient.Mobile.Services;

/// <summary>
///     기기에 보관하는 비밀값(프로필 비밀번호·API 토큰). 플랫폼이 안전한 저장소로 구현한다 —
///     Android 는 키스토어 키로 암호화해 앱 전용 저장소에 둔다.
/// </summary>
public interface ISecretStore
{
    string? Get(string key);

    void Set(string key, string value);

    void Remove(string key);
}

/// <summary>저장하지 않는 구현 — 플랫폼 저장소가 없을 때(디자이너·테스트). 앱을 끄면 사라진다.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key)
    {
        lock (_values) return _values.GetValueOrDefault(key);
    }

    public void Set(string key, string value)
    {
        lock (_values) _values[key] = value;
    }

    public void Remove(string key)
    {
        lock (_values) _values.Remove(key);
    }
}
