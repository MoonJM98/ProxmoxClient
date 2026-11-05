using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     Windows 자격 증명 관리자(CredRead/CredWrite, 현재 사용자 범위) — WPF 앱의 WindowsCredentialStore 와 같은 방식.
///     항목 이름은 "ProxmoxClient/{key}".
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const uint Generic = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;

    public bool IsPersistent => true;

    public string? Get(string key)
    {
        if (!CredRead(Target(key), Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            try
            {
                return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2));
            }
            finally
            {
                for (var index = 0; index < credential.BlobSize; index++) Marshal.WriteByte(credential.Blob, index, 0);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Set(string key, string value)
    {
        if (value.Length > 1280) throw new ArgumentException("Secret exceeds the Windows credential size limit.");

        var blob = Marshal.StringToCoTaskMemUni(value);
        try
        {
            var credential = new Credential
            {
                Type = Generic,
                TargetName = Target(key),
                UserName = SecretStores.Service,
                BlobSize = checked((uint)value.Length * 2),
                Blob = blob,
                Persist = PersistLocalMachine
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    public void Remove(string key)
    {
        if (!CredDelete(Target(key), Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static string Target(string key)
    {
        return $"{SecretStores.Service}/{key}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
