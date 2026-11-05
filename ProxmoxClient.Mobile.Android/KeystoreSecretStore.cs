using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using ProxmoxClient.Mobile.Services;

namespace ProxmoxClient.Mobile.Android;

/// <summary>
///     비밀값 저장소 — AES-256-GCM 키를 Android 키스토어(하드웨어 보호, 앱 밖으로 꺼낼 수 없음)에 만들고,
///     그 키로 암호화한 값만 앱 전용 SharedPreferences 에 둔다. 앱을 지우면 키와 값이 함께 사라진다.
///     복호화에 실패하면(키가 사라짐 등) 저장값이 없는 것으로 보고 다시 묻는다.
/// </summary>
internal sealed class KeystoreSecretStore(Context context) : ISecretStore
{
    private const string KeyStoreName = "AndroidKeyStore";
    private const string KeyAlias = "ProxmoxClient.ProfileSecrets.v1";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int TagBits = 128;
    private const int IvLength = 12;

    private readonly ISharedPreferences _prefs =
        context.GetSharedPreferences("proxmoxclient.secrets", FileCreationMode.Private)!;

    private readonly object _gate = new();

    public string? Get(string key)
    {
        lock (_gate)
        {
            var stored = _prefs.GetString(key, null);
            if (string.IsNullOrEmpty(stored)) return null;

            try
            {
                var data = Convert.FromBase64String(stored);
                var cipher = Cipher.GetInstance(Transformation)!;
                cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(TagBits, data, 0, IvLength));
                var plain = cipher.DoFinal(data, IvLength, data.Length - IvLength)!;
                return System.Text.Encoding.UTF8.GetString(plain);
            }
            catch (Exception)
            {
                Remove(key);
                return null;
            }
        }
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, GetOrCreateKey()); // 키스토어가 IV 를 매번 새로 만든다
            var iv = cipher.GetIV()!;
            var encrypted = cipher.DoFinal(System.Text.Encoding.UTF8.GetBytes(value))!;
            var data = new byte[iv.Length + encrypted.Length];
            iv.CopyTo(data, 0);
            encrypted.CopyTo(data, iv.Length);
            _prefs.Edit()!.PutString(key, Convert.ToBase64String(data))!.Apply();
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            _prefs.Edit()!.Remove(key)!.Apply();
        }
    }

    private static IKey GetOrCreateKey()
    {
        var keyStore = KeyStore.GetInstance(KeyStoreName)!;
        keyStore.Load(null);
        if (keyStore.GetKey(KeyAlias, null) is { } existing) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreName)!;
        generator.Init(new KeyGenParameterSpec.Builder(KeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build());
        return generator.GenerateKey()!;
    }
}
