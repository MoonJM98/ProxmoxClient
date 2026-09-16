namespace ProxmoxClient.Core.Models;

/// <summary>
///     현재 사용자의 권한 요약(GET /access/permissions 파싱).
///     관리자(Administrator 역할 또는 * 권한)는 모든 기능 허용으로 간주한다.
/// </summary>
public sealed class PermissionsInfo
{
    public static PermissionsInfo Admin { get; } = new()
    {
        IsAdmin = true,
        Privileges = new HashSet<string>(["*"], StringComparer.OrdinalIgnoreCase)
    };

    public bool IsAdmin { get; init; }

    /// <summary>사용자가 어딘가에서 부여받은 권한 전체(경로 무시 합집합).</summary>
    public IReadOnlySet<string> Privileges { get; init; } = new HashSet<string>();

    // 데스크톱 클라이언트 기능별 편의 플래그
    public bool CanPowerMgmt => Has("VM.PowerMgmt");
    public bool CanConsole => Has("VM.Console");
    public bool CanSnapshot => Has("VM.Snapshot");
    public bool CanClone => Has("VM.Clone");
    public bool CanBackup => Has("VM.Backup");
    public bool CanMigrate => Has("VM.Migrate");

    public bool CanConfigure => HasAny("VM.Config.Options", "VM.Config.HWType", "VM.Config.Disk", "VM.Config.Network",
        "VM.Config.CPU", "VM.Config.Memory");

    public bool CanAllocate => Has("VM.Allocate");

    public bool Has(string privilege)
    {
        return IsAdmin
               || Privileges.Contains("*")
               || Privileges.Contains(privilege);
    }

    public bool HasAny(params string[] privileges)
    {
        return privileges.Any(Has);
    }
}