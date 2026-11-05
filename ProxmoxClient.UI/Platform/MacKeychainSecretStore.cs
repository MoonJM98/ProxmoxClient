using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     macOS 로그인 키체인 — 서비스 "ProxmoxClient", 계정 이름 = 키. Security.framework 를 직접 호출한다
///     (명령줄 도구에 비밀값을 인자로 넘기면 프로세스 목록에 드러나므로 쓰지 않는다).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychainSecretStore : ISecretStore
{
    private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ItemNotFound = -25300;
    private static readonly byte[] ServiceBytes = Encoding.UTF8.GetBytes(SecretStores.Service);

    public bool IsPersistent => true;

    public string? Get(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes,
            (uint)account.Length, account, out var length, out var data, out var item);
        if (status == ItemNotFound) return null;
        Check(status);

        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public void Set(string key, string value)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var secret = Encoding.UTF8.GetBytes(value);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes,
            (uint)account.Length, account, out _, out var data, out var item);
        if (status == 0)
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            try
            {
                Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secret));
            }
            finally
            {
                CFRelease(item);
            }

            return;
        }

        if (status != ItemNotFound) Check(status);
        Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes,
            (uint)account.Length, account, (uint)secret.Length, secret, out var added));
        if (added != IntPtr.Zero) CFRelease(added);
    }

    public void Remove(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes,
            (uint)account.Length, account, out _, out var data, out var item);
        if (status == ItemNotFound) return;
        Check(status);

        SecKeychainItemFreeContent(IntPtr.Zero, data);
        try
        {
            Check(SecKeychainItemDelete(item));
        }
        finally
        {
            CFRelease(item);
        }
    }

    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"Keychain error {status}");
    }

    [DllImport(SecurityLib)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength,
        byte[] serviceName, uint accountNameLength, byte[] accountName, out uint passwordLength,
        out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength,
        byte[] serviceName, uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length,
        byte[] data);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundationLib)]
    private static extern void CFRelease(IntPtr cf);
}
