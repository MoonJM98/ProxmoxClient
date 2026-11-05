namespace ProxmoxClient.UI.Platform;

/// <summary>
///     기기에 보관하는 비밀값(프로필 비밀번호·API 토큰). 운영체제의 보안 저장소로 구현한다 —
///     <see cref="SecretStores.CreateDefault" /> 가 OS 별 구현을 고르고, Android 는 헤드 프로젝트가 키스토어 구현을 넣는다.
/// </summary>
public interface ISecretStore
{
    /// <summary>앱을 다시 켜도 남는지. false 면 "저장" 선택지를 숨긴다.</summary>
    bool IsPersistent { get; }

    string? Get(string key);

    void Set(string key, string value);

    void Remove(string key);
}

/// <summary>저장하지 않는 구현 — OS 보안 저장소를 쓸 수 없을 때. 앱을 끄면 사라진다.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public bool IsPersistent => false;

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
