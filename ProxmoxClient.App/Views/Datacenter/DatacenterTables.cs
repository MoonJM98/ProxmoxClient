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
        new() { Key = "vmid", HeaderKey = "Table_Guests", Width = 110 },
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
        return Create(api, "cluster/status", StatusColumns, "DcSummary_Hint");
    }

    public static TableTab Storage(ProxmoxApiClient api, bool canEdit)
    {
        return Create(api, "storage", StorageColumns, "DcStorage_Hint",
            canEdit ? ClusterActions.Storage(api) : null);
    }

    public static TableTab BackupJobs(ProxmoxApiClient api, bool canEdit)
    {
        return Create(api, "cluster/backup", BackupJobColumns, "DcBackup_Hint",
            canEdit ? ClusterActions.BackupJobs(api) : null);
    }

    public static TableTab Replication(ProxmoxApiClient api, bool canEdit)
    {
        return Create(api, "cluster/replication", ReplicationColumns, "DcReplication_Hint",
            canEdit ? ClusterActions.Replication(api) : null);
    }

    public static TableTab HaResources(ProxmoxApiClient api, bool canEdit)
    {
        return Create(api, "cluster/ha/resources", HaColumns, "DcHa_Hint",
            canEdit ? ClusterActions.HaResources(api) : null);
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

    /// <summary>웹 UI 의 HA 화면 — 상태·리소스·규칙 하위 탭.</summary>
    public static SubTabsView Ha(ProxmoxApiClient api, bool canEdit)
    {
        return new SubTabsView(
        [
            ("DcHa_Status", () => Create(api, "cluster/ha/status/current", HaStatusColumns, "DcHa_StatusHint")),
            ("DcHa_Resources", () => HaResources(api, canEdit)),
            ("DcTab_HaRules", () => HaRules(api, canEdit)),
            ("DcHaGroups_Tab", () => new TableTab(() => api.GetTableAsync("cluster/ha/groups"), HaGroupActions.Columns,
                "DcHaGroups_Hint", canEdit ? HaGroupActions.Actions(api) : null))
        ]);
    }

    public static TableTab HaRules(ProxmoxApiClient api, bool canEdit)
    {
        return Create(api, "cluster/ha/rules", HaRuleActions.Columns, "DcHaRules_Hint",
            canEdit ? HaRuleActions.Actions(api) : null);
    }

    public static TableTab Users(ProxmoxApiClient api, bool canEdit)
    {
        return Create(api, "access/users", UserColumns, "DcUsers_Hint", canEdit ? AccessActions.Users(api) : null);
    }

    public static TableTab Groups(ProxmoxApiClient api, bool canEdit)
    {
        return Create(api, "access/groups", GroupColumns, "DcGroups_Hint",
            canEdit ? AccessActions.Groups(api) : null);
    }

    public static TableTab Roles(ProxmoxApiClient api, bool canEdit)
    {
        return Create(api, "access/roles", RoleColumns, "DcRoles_Hint",
            canEdit ? AccessExtras.RoleActions(api) : null);
    }

    public static TableTab Tfa(ProxmoxApiClient api, bool canEdit)
    {
        return new TableTab(() => api.GetFlattenedTableAsync("access/tfa", "entries"), AccessExtras.TfaColumns,
            "DcTfa_Hint", canEdit ? [..TfaRegistration.Actions(api), ..AccessExtras.TfaActions(api)] : null);
    }

    public static TableTab Acl(ProxmoxApiClient api, bool canEdit)
    {
        return Create(api, "access/acl", AclColumns, "DcAcl_Hint", canEdit ? AccessActions.Acl(api) : null);
    }

    public static TableTab Realms(ProxmoxApiClient api, bool canEdit)
    {
        return Create(api, "access/domains", RealmColumns, "DcRealms_Hint",
            canEdit ? AccessExtras.RealmActions(api) : null);
    }

    public static TableTab Pools(ProxmoxApiClient api, bool canEdit)
    {
        return Create(api, "pools", PoolColumns, "DcPools_Hint", canEdit ? AccessActions.Pools(api) : null);
    }

    private static TableTab Create(ProxmoxApiClient api, string path, IReadOnlyList<TableColumn> columns,
        string hintKey, IReadOnlyList<TableAction>? actions = null)
    {
        return new TableTab(() => api.GetTableAsync(path), columns, hintKey, actions);
    }

    /// <summary>사용자 만료일 — 0 은 '만료 없음'이라 빈칸으로 둔다.</summary>
    private static string FormatExpire(string raw)
    {
        return raw is "" or "0" ? string.Empty : TableFormats.EpochDate(raw);
    }
}
