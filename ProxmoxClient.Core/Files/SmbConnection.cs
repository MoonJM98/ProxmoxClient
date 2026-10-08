using System.ComponentModel;
using System.Runtime.InteropServices;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Files;

/// <summary>SMB 로그인 실패 — 비밀번호를 다시 물을 수 있다.</summary>
public sealed class SmbAuthenticationException(string message) : GuestFileException(message);

/// <summary>
///     Windows 의 SMB 연결(WNetAddConnection2) — 계정을 주면 \\주소\공유 에 그 계정으로 붙고, 닫을 때 끊는다.
///     계정을 비우면 연결하지 않고 지금 Windows 로그인으로 접근한다(도메인·저장된 자격 증명).
///     Windows 는 한 서버에 한 계정만 붙일 수 있어, 다른 계정으로 이미 붙어 있으면(탐색기 등) 알린다.
///     같은 공유·계정을 여러 창이 열면 연결 하나를 같이 쓴다(참조 수) — 마지막 창이 닫힐 때만 끊는다.
/// </summary>
internal sealed class SmbConnection : IDisposable
{
    private const int ResourceTypeDisk = 1;
    private const int ConnectTemporary = 4;
    private const int ErrorAccessDenied = 5;
    private const int ErrorBadNetPath = 53;
    private const int ErrorBadNetName = 67;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorAccountRestriction = 1327;
    private const int ErrorPasswordExpired = 1330;
    private const int ErrorAccountDisabled = 1331;
    private const int ErrorBadNetworkPath = 1203;

    /// <summary>이 앱이 붙인 연결(공유 경로 → 붙인 계정, 쓰는 창 수) — 대소문자 무시.</summary>
    private static readonly Dictionary<string, (string User, int Count)> Shared =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string? _root;
    private bool _disposed;

    private SmbConnection(string? root)
    {
        _root = root;
    }

    /// <summary>\\주소\공유 에 붙는다(계정이 없으면 붙지 않고 지금 로그인 그대로).</summary>
    public static SmbConnection Open(string root, string? userName, string? password)
    {
        if (string.IsNullOrEmpty(userName)) return new SmbConnection(null);

        lock (Shared)
        {
            if (Shared.TryGetValue(root, out var shared))
            {
                // 이미 이 앱이 붙여 둔 공유 — 같은 계정이면 같이 쓰고, 다른 계정이면 Windows 처럼 거부한다
                if (!string.Equals(shared.User, userName, StringComparison.OrdinalIgnoreCase))
                    throw new GuestFileException(Res.T("Smb_CredentialConflict"));
                Shared[root] = shared with { Count = shared.Count + 1 };
                return new SmbConnection(root);
            }

            var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = root };
            var result = WNetAddConnection2(ref resource, password ?? string.Empty, userName, ConnectTemporary);
            if (result != 0) throw Error(result, root);

            Shared[root] = (userName, 1);
            return new SmbConnection(root);
        }
    }

    public void Dispose()
    {
        if (_root is null || _disposed) return;

        _disposed = true;
        lock (Shared)
        {
            if (!Shared.TryGetValue(_root, out var shared)) return;
            if (shared.Count > 1)
            {
                Shared[_root] = shared with { Count = shared.Count - 1 };
                return;
            }

            Shared.Remove(_root);
            WNetCancelConnection2(_root, 0, false); // 다른 프로그램이 쓰는 중이면 끊지 않는다
        }
    }

    private static GuestFileException Error(int code, string root) => code switch
    {
        ErrorLogonFailure or ErrorAccountRestriction or ErrorPasswordExpired or ErrorAccountDisabled
            or ErrorAccessDenied => new SmbAuthenticationException(Res.T("Smb_LogonFailed", new Win32Exception(code)
                .Message)),
        ErrorSessionCredentialConflict => new GuestFileException(Res.T("Smb_CredentialConflict")),
        ErrorBadNetName => new GuestFileException(Res.T("Smb_ShareNotFound", root)),
        ErrorBadNetPath or ErrorBadNetworkPath => new GuestFileException(Res.T("Smb_Unreachable", root)),
        _ => new GuestFileException(new Win32Exception(code).Message)
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string password, string userName,
        int flags);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
