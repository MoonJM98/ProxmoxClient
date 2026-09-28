using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>데이터센터 창의 표 화면들 — 서버 경로와 보여 줄 열만 정의한다.</summary>
internal static class DatacenterTables
{
    private static readonly IReadOnlyList<TableColumn> StatusColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 160 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "online", HeaderKey = "Table_Online", Width = 70, Format = TableFormats.Flag },
        new() { Key = "ip", HeaderKey = "Table_Address", Width = 140 },
        new() { Key = "nodeid", HeaderKey = "Table_NodeId", Width = 80 },
        new() { Key = "quorate", HeaderKey = "Table_Quorate", Width = 80, Format = TableFormats.Flag },
        new() { Key = "nodes", HeaderKey = "Table_Nodes", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> StorageColumns =
    [
        new() { Key = "storage", HeaderKey = "Table_Name", Width = 140 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "content", HeaderKey = "Table_Content", Width = 0 },
        new() { Key = "_target", HeaderKey = "DcStorage_PathTarget", Width = 180 },
        new() { Key = "shared", HeaderKey = "Table_Shared", Width = 60, Format = TableFormats.Flag },
        new() { Key = "disable", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.InverseFlag },
        new() { Key = "nodes", HeaderKey = "Table_Nodes", Width = 140 }
    ];

    private static readonly IReadOnlyList<TableColumn> BackupJobColumns =
    [
        new() { Key = "id", HeaderKey = "Table_Id", Width = 150 },
        new() { Key = "enabled", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.Flag },
        new() { Key = "schedule", HeaderKey = "Table_Schedule", Width = 130 },
        new() { Key = "next-run", HeaderKey = "Table_NextRun", Width = 140, Format = TableFormats.EpochDate },
        new() { Key = "storage", HeaderKey = "Table_Storage", Width = 110 },
        new() { Key = "node", HeaderKey = "Table_Node", Width = 90 },
        new() { Key = "_selection", HeaderKey = "Table_Guests", Width = 150 },
        new() { Key = "mode", HeaderKey = "Table_Mode", Width = 80 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> ReplicationColumns =
    [
        new() { Key = "id", HeaderKey = "Table_Id", Width = 110 },
        new() { Key = "guest", HeaderKey = "Table_Guest", Width = 80 },
        new() { Key = "source", HeaderKey = "Table_Source", Width = 100 },
        new() { Key = "target", HeaderKey = "Table_Target", Width = 100 },
        new() { Key = "schedule", HeaderKey = "Table_Schedule", Width = 110 },
        new() { Key = "disable", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.InverseFlag },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> HaColumns =
    [
        new() { Key = "sid", HeaderKey = "Table_Id", Width = 110 },
        new() { Key = "state", HeaderKey = "Table_State", Width = 100 },
        new() { Key = "group", HeaderKey = "Table_Group", Width = 120 },
        new() { Key = "max_restart", HeaderKey = "Table_MaxRestart", Width = 100 },
        new() { Key = "max_relocate", HeaderKey = "Table_MaxRelocate", Width = 100 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

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

    public static TableTab Summary(ProxmoxApiClient api)
    {
        return Create(() => api.Cluster.StatusAsync(), StatusColumns, "DcSummary_Hint");
    }

    public static TableTab Storage(ProxmoxApiClient api, bool canEdit)
    {
        return new TableTab(async () => (await api.Storage.ListAsync()).Select(WithTarget).ToList(),
            StorageColumns, "DcStorage_Hint", canEdit ? StorageActions.Actions(api) : null);
    }

    public static TableTab BackupJobs(ProxmoxApiClient api, bool canEdit)
    {
        return new TableTab(async () => (await api.Jobs.ListBackupJobsAsync()).Select(WithSelection).ToList(),
            BackupJobColumns, "DcBackup_Hint", canEdit ? ClusterActions.BackupJobs(api) : null);
    }

    /// <summary>웹 UI 의 Path/Target 열 — 유형마다 다른 위치 값(경로·서버:내보내기·풀·대상·데이터스토어).</summary>
    internal static IReadOnlyDictionary<string, string> WithTarget(IReadOnlyDictionary<string, string> row)
    {
        string V(string key) => row.TryGetValue(key, out var v) ? v : string.Empty;
        var target = V("path") is { Length: > 0 } path ? path
            : V("export") is { Length: > 0 } export ? $"{V("server")}:{export}"
            : V("share") is { Length: > 0 } share ? $"//{V("server")}/{share}"
            : V("datastore") is { Length: > 0 } store ? $"{V("server")}:{store}"
            : V("thinpool") is { Length: > 0 } thin ? $"{V("vgname")}/{thin}"
            : V("vgname") is { Length: > 0 } vg ? vg
            : V("pool") is { Length: > 0 } pool ? pool
            : V("target");
        return new Dictionary<string, string>(row) { ["_target"] = target };
    }

    /// <summary>웹 UI 의 Selection 열 — 전체(제외 목록)·풀·지정 게스트.</summary>
    internal static IReadOnlyDictionary<string, string> WithSelection(IReadOnlyDictionary<string, string> row)
    {
        string V(string key) => row.TryGetValue(key, out var v) ? v : string.Empty;
        var selection = V("all") is "1" or "true"
            ? V("exclude") is { Length: > 0 } excluded
                ? Loc.T("DcBackup_AllExcept", excluded)
                : Loc.T("DcBackup_SelectAll")
            : V("pool") is { Length: > 0 } pool ? Loc.T("DcBackup_PoolSelection", pool)
            : V("vmid");
        return new Dictionary<string, string>(row) { ["_selection"] = selection };
    }

    public static TableTab Replication(ProxmoxApiClient api, bool canEdit)
    {
        return Create(() => api.Jobs.ListReplicationAsync(), ReplicationColumns, "DcReplication_Hint",
            canEdit ? ClusterActions.Replication(api) : null);
    }

    /// <param name="rules">PVE 9 이상(규칙) — 리소스 편집 칸이 버전마다 다르다.</param>
    public static TableTab HaResources(ProxmoxApiClient api, bool canEdit, bool rules)
    {
        return Create(() => api.Ha.ListResourcesAsync(), HaColumns, "DcHa_Hint",
            canEdit ? HaActions.Resources(api, rules) : null);
    }

    public static TableTab HaStatus(ProxmoxApiClient api, IReadOnlyList<TableAction>? actions = null)
    {
        return Create(() => api.Ha.StatusAsync(), HaStatusColumns, "DcHa_StatusHint", actions);
    }

    private static readonly IReadOnlyList<TableColumn> HaStatusColumns =
    [
        new() { Key = "type", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "id", HeaderKey = "Table_Id", Width = 160 },
        new() { Key = "node", HeaderKey = "Table_Node", Width = 110 },
        new() { Key = "status", HeaderKey = "Table_State", Width = 0 },
        new() { Key = "request_state", HeaderKey = "DcHa_Requested", Width = 110 },
        new() { Key = "quorate", HeaderKey = "Table_Quorate", Width = 70, Format = TableFormats.Flag }
    ];

    /// <summary>웹 UI 의 HA 화면 — 상태·리소스와, 서버 버전에 따라 규칙(PVE 9) 또는 그룹(PVE 8).</summary>
    public static UIElement Ha(ProxmoxApiClient api, bool canEdit)
    {
        return HaActions.View(api, canEdit, HaResources);
    }

    public static TableTab HaRules(ProxmoxApiClient api, bool canEdit)
    {
        return Create(() => api.Ha.ListRulesAsync(), HaRuleActions.Columns, "DcHaRules_Hint",
            canEdit ? HaRuleActions.Actions(api) : null);
    }

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

    private static TableTab Create(Func<Task<IReadOnlyList<IReadOnlyDictionary<string, string>>>> load,
        IReadOnlyList<TableColumn> columns, string hintKey, IReadOnlyList<TableAction>? actions = null)
    {
        return new TableTab(load, columns, hintKey, actions);
    }

    /// <summary>사용자 만료일 — 0 은 '만료 없음'이라 빈칸으로 둔다.</summary>
    private static string FormatExpire(string raw)
    {
        return raw is "" or "0" ? string.Empty : TableFormats.EpochDate(raw);
    }
}
