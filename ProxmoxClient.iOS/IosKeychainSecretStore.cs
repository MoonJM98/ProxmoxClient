using Foundation;
using ProxmoxClient.UI.Platform;
using Security;

namespace ProxmoxClient.iOS;

/// <summary>
///     iOS 키체인 저장소(일반 암호 항목) — 서비스 이름은 앱 ID, 계정은 비밀값 키. 이 기기에서만, 처음 잠금 해제 뒤 읽을 수
///     있다(백업으로 다른 기기에 옮겨지지 않는다).
/// </summary>
internal sealed class IosKeychainSecretStore : ISecretStore
{
    private const string Service = "io.github.moonjm98.proxmoxclient";

    public bool IsPersistent => true;

    public string? Get(string key)
    {
        var match = SecKeyChain.QueryAsRecord(Query(key), out var status);
        return status == SecStatusCode.Success && match?.ValueData is { } data
            ? data.ToString(NSStringEncoding.UTF8)
            : null;
    }

    public void Set(string key, string value)
    {
        Remove(key); // 같은 키가 있으면 추가가 실패(DuplicateItem)하므로 먼저 지운다
        var record = Query(key);
        record.ValueData = NSData.FromString(value, NSStringEncoding.UTF8);
        record.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
        var status = SecKeyChain.Add(record);
        if (status != SecStatusCode.Success)
            throw new InvalidOperationException($"Keychain: {status}");
    }

    public void Remove(string key)
    {
        SecKeyChain.Remove(Query(key));
    }

    private static SecRecord Query(string key)
    {
        return new SecRecord(SecKind.GenericPassword) { Service = Service, Account = key };
    }
}
