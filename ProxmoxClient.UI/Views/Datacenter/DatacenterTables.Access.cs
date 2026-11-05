using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Datacenter;

/// <summary>데이터센터 접근 제어 화면의 표 — 사용자·그룹·역할·2단계 인증·권한·인증 영역·풀.</summary>
internal static partial class DatacenterTables
{
    private static readonly IReadOnlyList<TableColumn> UserColumns =
    [
        new() { Key = "userid", HeaderKey = "Table_UserId", Width = 160 },
        new() { Key = "enable", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.Flag },
        new() { Key = "expire", HeaderKey = "Table_Expires", Width = 130, Format = FormatExpire },
        new() { Key = "firstname", HeaderKey = "Table_FirstName", Width = 100 },
        new() { Key = "lastname", HeaderKey = "Table_LastName", Width = 100 },
        new() { Key = "email", HeaderKey = "Table_Email", Width = 160 },
        new() { Key = "groups", HeaderKey = "DcTab_Groups", Width = 120 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> GroupColumns =
    [
        new() { Key = "groupid", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "users", HeaderKey = "DcTab_Users", Width = 0 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 200 }
    ];

    private static readonly IReadOnlyList<TableColumn> RoleColumns =
    [
        new() { Key = "roleid", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "special", HeaderKey = "Table_BuiltIn", Width = 70, Format = TableFormats.Flag },
        new() { Key = "privs", HeaderKey = "Table_Privileges", Width = 0 }
    ];

    /// <summary>게스트 창의 권한 탭도 같은 열을 쓴다.</summary>
    internal static readonly IReadOnlyList<TableColumn> AclColumns =
    [
        new() { Key = "path", HeaderKey = "Table_Path", Width = 200 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 80 },
        new() { Key = "ugid", HeaderKey = "Table_UserOrGroup", Width = 180 },
        new() { Key = "roleid", HeaderKey = "Table_Role", Width = 0 },
        new() { Key = "propagate", HeaderKey = "Table_Propagate", Width = 70, Format = TableFormats.Flag }
    ];

    private static readonly IReadOnlyList<TableColumn> RealmColumns =
    [
        new() { Key = "realm", HeaderKey = "Table_Name", Width = 140 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "tfa", HeaderKey = "Table_Tfa", Width = 90 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> PoolColumns =
    [
        new() { Key = "poolid", HeaderKey = "Table_Name", Width = 200 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    public static TableTab Users(ProxmoxApiClient api, bool canEdit)
    {
        // full=1 — 그룹·토큰까지 받는다(웹 UI 와 같다). 버튼은 권한이 없어도 자기 암호·토큰용으로 보인다
        return new TableTab(() => api.Users.ListAsync(full: true), UserColumns, "DcUsers_Hint",
            UserActions.Actions(api, canEdit));
    }

    public static TableTab Groups(ProxmoxApiClient api, bool canEdit)
    {
        return new TableTab(() => api.Access.ListGroupsAsync(), GroupColumns, "DcGroups_Hint",
            canEdit ? AccessActions.Groups(api) : null);
    }

    public static TableTab Roles(ProxmoxApiClient api, bool canEdit)
    {
        return new TableTab(() => api.Access.ListRolesAsync(), RoleColumns, "DcRoles_Hint",
            canEdit ? AccessExtras.RoleActions(api) : null);
    }

    public static TableTab Tfa(ProxmoxApiClient api, bool canEdit)
    {
        // 누구나 자기 2단계 인증을 등록·관리한다(서버가 다른 사용자 항목은 권한으로 막는다)
        return new TableTab(() => api.Tfa.ListAsync(), AccessExtras.TfaColumns,
            "DcTfa_Hint", [..TfaRegistration.Actions(api), ..AccessExtras.TfaActions(api)]);
    }

    public static TableTab Acl(ProxmoxApiClient api, bool canEdit)
    {
        return new TableTab(() => api.Access.ListAclAsync(), AclColumns, "DcAcl_Hint",
            canEdit ? AccessActions.Acl(api) : null);
    }

    public static TableTab Realms(ProxmoxApiClient api, bool canEdit)
    {
        return new TableTab(() => api.Realms.ListAsync(), RealmColumns, "DcRealms_Hint",
            canEdit ? RealmActions.Actions(api) : null);
    }

    public static TableTab Pools(ProxmoxApiClient api, bool canEdit)
    {
        return new TableTab(() => api.Pools.ListAsync(), PoolColumns, "DcPools_Hint",
            canEdit ? AccessActions.Pools(api) : null);
    }

    /// <summary>사용자 만료일 — 0 은 '만료 없음'이라 빈칸으로 둔다.</summary>
    private static string FormatExpire(string raw)
    {
        return raw is "" or "0" ? string.Empty : TableFormats.EpochDate(raw);
    }
}
