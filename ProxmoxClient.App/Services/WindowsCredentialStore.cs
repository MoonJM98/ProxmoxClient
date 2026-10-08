using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ProxmoxClient.App.Services;

/// <summary>현재 Windows 사용자·현재 PC에만 보관하는 일반 자격 증명.</summary>
internal static class WindowsCredentialStore
{
    private const uint Generic = 1;
    private const int NotFound = 1168;

    public static string? Read(string target)
    {
        if (!CredRead(target, Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            try { return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2)); }
            finally
            {
                for (var index = 0; index < credential.BlobSize; index++)
                    Marshal.WriteByte(credential.Blob, index, 0);
            }
        }
        finally { CredFree(pointer); }
    }

    public static void Write(string target, string userName, string password)
    {
        if (password.Length > 1280) throw new ArgumentException("Password exceeds the Windows credential size limit.");
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new Credential
            {
                Type = Generic, TargetName = target, UserName = userName,
                BlobSize = checked((uint)password.Length * 2), Blob = blob, Persist = 2
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    public static void Delete(string target)
    {
        if (!CredDelete(target, Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error());
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
