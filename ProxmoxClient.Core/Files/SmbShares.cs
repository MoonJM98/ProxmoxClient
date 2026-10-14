using System.ComponentModel;
using System.Runtime.InteropServices;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Files;

/// <summary>
///     SMB 서버의 공유 목록(NetShareEnum) — 설정 창의 "공유 이름" 고르기용. 계정을 주면 \\주소\IPC$ 에 그 계정으로
///     잠깐 붙어서 묻는다(계정을 비우면 지금 Windows 로그인으로). 디스크 공유만, 관리 공유는 C$ 같은 드라이브만 둔다.
/// </summary>
public static class SmbShares
{
    private const int Level1 = 1;
    private const int MaxPreferredLength = -1;
    private const int ErrorMoreData = 234;
    private const uint TypeMask = 0xFF;
    private const uint DiskTree = 0;
    private const uint Special = 0x80000000;

    /// <summary>(공유 이름, 설명) — 이름 순, 일반 공유 먼저. 서버에 닿지 않거나 거부되면 GuestFileException.</summary>
    public static Task<IReadOnlyList<(string Name, string Remark)>> ListAsync(string host, string? userName,
        string? password, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var server = @"\\" + SmbFileSystem.UncHost(host);
            using var ipc = SmbConnection.Open(server + @"\IPC$", userName, password);
            return Enumerate(server, userName);
        }, ct);

    private static IReadOnlyList<(string Name, string Remark)> Enumerate(string server, string? userName)
    {
        var resume = 0;
        var result = NetShareEnum(server, Level1, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
        try
        {
            if (result is not (0 or ErrorMoreData)) throw Error(result, server, userName);

            var size = Marshal.SizeOf<ShareInfo1>();
            var shares = new List<(string Name, string Remark, bool Special)>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<ShareInfo1>(buffer + i * size);
                if (info.NetName is not { Length: > 0 } name || (info.Type & TypeMask) != DiskTree) continue;

                var special = (info.Type & Special) != 0;
                if (special && !IsDriveShare(name)) continue; // ADMIN$·print$ 등은 빼고 C$ 같은 드라이브만
                shares.Add((name, info.Remark ?? string.Empty, special));
            }

            return shares.OrderBy(s => s.Special).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => (s.Name, s.Remark)).ToList();
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    private static bool IsDriveShare(string name) => name.Length == 2 && char.IsAsciiLetter(name[0]) && name[1] == '$';

    private static GuestFileException Error(int code, string server, string? userName) => code switch
    {
        5 => SmbFileSystem.AccessDenied(userName, new Win32Exception(code).Message),
        53 or 1203 => new GuestFileException(Res.T("Smb_Unreachable", server)),
        _ => new GuestFileException(new Win32Exception(code).Message)
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        public string? NetName;
        public uint Type;
        public string? Remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string server, int level, out IntPtr buffer, int preferredMaxLength,
        out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
