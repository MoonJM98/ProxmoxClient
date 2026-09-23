using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>데이터센터 백업 일정·복제·HA·저장소 화면의 추가/편집/삭제 버튼.</summary>
internal static class ClusterActions
{
    private static readonly IReadOnlyList<(string, string)> BackupModes =
        [("snapshot", "BackupMode_Snapshot"), ("suspend", "BackupMode_Suspend"), ("stop", "BackupMode_Stop")];

    private static readonly IReadOnlyList<(string, string)> CompressTypes =
        [("zstd", "ZSTD"), ("lzo", "LZO"), ("gzip", "GZIP"), ("0", "Compress_None")];

    private static readonly IReadOnlyList<(string, string)> HaStates =
    [
        ("started", "HaState_Started"), ("stopped", "HaState_Stopped"),
        ("disabled", "HaState_Disabled"), ("ignored", "HaState_Ignored")
    ];

    // ------------------------------------------------------------ 백업 일정

    public static IReadOnlyList<TableAction> BackupJobs(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => EditBackupJobAsync(api, null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditBackupJobAsync(api, row, owner)
            },
            DeleteAction(row => Loc.T("DcBackup_DeleteConfirm", row["id"]),
                row => api.DeleteActionAsync($"cluster/backup/{Seg(row["id"])}"), "DcBackup_Deleted"),
            new TableAction
            {
                LabelKey = "DcBackup_RunNow", IconKey = "IconPlay", NeedsSelection = true,
                Confirm = row => Loc.T("DcBackup_RunNowConfirm", row!["id"]),
                Run = async (row, _) =>
                {
                    var online = (await api.GetNodesAsync())
                        .Where(n => string.Equals(n.Status, "online", StringComparison.OrdinalIgnoreCase))
                        .Select(n => n.Node)
                        .ToList();
                    var started = await api.RunBackupJobNowAsync(row!, online);
                    return Loc.T("DcBackup_RunNowStarted", started.Count);
                }
            },
            new TableAction
            {
                LabelKey = "DcBackup_Included", IconKey = "IconList", NeedsSelection = true,
                Run = (row, owner) => Task.FromResult(TableWindow.ShowModal(owner,
                    Loc.T("DcBackup_IncludedTitle", row!["id"]),
                    new TableTab(() => api.GetBackupJobVolumesAsync(row["id"]), IncludedColumns,
                        "DcBackup_IncludedHint")))
            },
            new TableAction
            {
                LabelKey = "DcBackup_NotBackedUp", IconKey = "IconSearch",
                Run = (_, owner) => Task.FromResult(TableWindow.ShowModal(owner, Loc.T("DcBackup_NotBackedUp"),
                    new TableTab(() => api.GetTableAsync("cluster/backup-info/not-backed-up"), NotBackedUpColumns,
                        "DcBackup_NotBackedUpHint")))
            }
        ];
    }

    private static readonly IReadOnlyList<TableColumn> IncludedColumns =
    [
        new() { Key = "vmid", HeaderKey = "Table_Guest", Width = 70 },
        new() { Key = "name", HeaderKey = "Table_Name", Width = 150 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 60 },
        new() { Key = "volume-id", HeaderKey = "DcBackup_VolumeKey", Width = 80 },
        new() { Key = "volume-name", HeaderKey = "DcBackup_Volume", Width = 0 },
        new()
        {
            Key = "volume-included", HeaderKey = "DcBackup_VolumeIncluded", Width = 60, Format = TableFormats.Flag
        },
        new() { Key = "volume-reason", HeaderKey = "DcBackup_VolumeReason", Width = 120 }
    ];

    private static readonly IReadOnlyList<TableColumn> NotBackedUpColumns =
    [
        new() { Key = "vmid", HeaderKey = "Table_Guest", Width = 80 },
        new() { Key = "name", HeaderKey = "Table_Name", Width = 0 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 80 }
    ];

    /// <summary>row 가 null 이면 새 일정, 아니면 그 일정을 고친다. 대상 게스트를 비우면 모든 게스트.</summary>
    private static async Task<string?> EditBackupJobAsync(ProxmoxApiClient api,
        IReadOnlyDictionary<string, string>? row, Window? owner)
    {
        var storages = (await api.GetTableAsync("storage"))
            .Where(s => Value(s, "content").Split(',').Contains("backup"))
            .Select(s => (s["storage"], s["storage"]))
            .ToList();
        var nodes = await NodeChoicesAsync(api, "DcBackup_AllNodes");
        string Initial(string key, string fallback = "") => row is null ? fallback : Value(row, key);

        var pools = (await api.GetTableAsync("pools"))
            .Select(p => (Value(p, "poolid"), Value(p, "poolid")))
            .ToList();
        // 대상 고르는 방식: 전체(+제외 목록) · 지정한 게스트 · 풀 — 서버는 이 셋을 섞어 쓰면 거절한다
        var selection = Initial("pool").Length > 0 ? "pool" : Initial("vmid").Length > 0 ? "vmid" : "all";

        var title = row is null ? Loc.T("DcBackup_AddTitle") : Loc.T("DcBackup_EditTitle", row["id"]);

        return await SubmitAsync(owner, title,
        [
            new FormField
            {
                Key = "schedule", LabelKey = "Table_Schedule", Required = true, Initial = Initial("schedule", "21:00")
            },
            new FormField
            {
                Key = "storage", LabelKey = "Table_Storage", Kind = FormFieldKind.Choice, Choices = storages,
                Initial = Initial("storage")
            },
            new FormField
            {
                Key = "node", LabelKey = "Table_Node", Kind = FormFieldKind.Choice, Choices = nodes,
                Initial = Initial("node")
            },
            new FormField
            {
                Key = "selection", LabelKey = "DcBackup_Selection", Kind = FormFieldKind.Choice, Initial = selection,
                Choices =
                [
                    ("all", "DcBackup_SelectAll"), ("vmid", "DcBackup_SelectGuests"), ("pool", "DcBackup_SelectPool")
                ]
            },
            new FormField { Key = "vmid", LabelKey = "DcBackup_Guests", Initial = Initial("vmid") },
            new FormField
            {
                Key = "pool", LabelKey = "DcTab_Pools", Kind = FormFieldKind.Choice, Initial = Initial("pool"),
                Choices = [("", "GuestOptions_NotSet"), ..pools]
            },
            new FormField { Key = "exclude", LabelKey = "DcBackup_Exclude", Initial = Initial("exclude") },
            new FormField
            {
                Key = "mode", LabelKey = "Table_Mode", Kind = FormFieldKind.Choice, Choices = BackupModes,
                Initial = Initial("mode", "snapshot")
            },
            new FormField
            {
                Key = "compress", LabelKey = "DcBackup_Compress", Kind = FormFieldKind.Choice, Choices = CompressTypes,
                Initial = Initial("compress", "zstd")
            },
            new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Initial("comment") },
            new FormField
            {
                Key = "enabled", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
                Initial = Initial("enabled", "1") is "0" ? "0" : "1"
            }
        ], values =>
        {
            var edited = BackupSelection(values);
            if (row is null) return api.PostActionAsync("cluster/backup", NonEmpty(edited));

            // 고치기에서는 고른 방식에 해당하지 않는 칸을 빈 값으로 넘겨 서버 설정에서 지운다(UpdateForm)
            return api.PutActionAsync($"cluster/backup/{Seg(row["id"])}", UpdateForm(edited));
        }, row is null ? "DcBackup_Added" : "DcBackup_Updated", titleIsKey: false,
            validate: values => values["selection"] switch
            {
                "vmid" when values["vmid"].Length == 0 => Loc.T("DcBackup_GuestsRequired"),
                "pool" when values["pool"].Length == 0 => Loc.T("DcBackup_PoolRequired"),
                _ => null
            });
    }

    /// <summary>
    ///     대상 방식에 맞춰 all·vmid·pool·exclude 를 정리한다 — 쓰지 않는 칸은 빈 값(고치기에서는 지우기)으로 만든다.
    ///     제외 목록은 '전체'일 때만 뜻이 있다.
    /// </summary>
    internal static Dictionary<string, string> BackupSelection(IReadOnlyDictionary<string, string> values)
    {
        var edited = new Dictionary<string, string>(values, StringComparer.Ordinal);
        var selection = edited["selection"];
        edited.Remove("selection");

        edited["all"] = selection == "all" ? "1" : "0";
        if (selection != "vmid") edited["vmid"] = string.Empty;
        if (selection != "pool") edited["pool"] = string.Empty;
        if (selection != "all") edited["exclude"] = string.Empty;
        return edited;
    }

    // ------------------------------------------------------------ 복제

    public static IReadOnlyList<TableAction> Replication(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddReplicationAsync(api, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditReplicationAsync(api, row!, owner)
            },
            DeleteAction(row => Loc.T("DcReplication_DeleteConfirm", row["id"]),
                row => api.DeleteActionAsync($"cluster/replication/{Seg(row["id"])}"), "DcReplication_Deleted")
        ];
    }

    private static async Task<string?> AddReplicationAsync(ProxmoxApiClient api, Window? owner)
    {
        var nodes = await NodeChoicesAsync(api, null);
        var existing = await api.GetTableAsync("cluster/replication");

        return await SubmitAsync(owner, "DcReplication_AddTitle",
        [
            new FormField { Key = "guest", LabelKey = "Table_Guest", Required = true },
            new FormField { Key = "target", LabelKey = "Table_Target", Kind = FormFieldKind.Choice, Choices = nodes },
            new FormField { Key = "schedule", LabelKey = "Table_Schedule", Initial = "*/15" },
            new FormField { Key = "rate", LabelKey = "DcReplication_Rate" },
            new FormField { Key = "comment", LabelKey = "Table_Comment" }
        ], values =>
        {
            var form = NonEmpty(values);
            form.Remove("guest");
            form["id"] = NextReplicationId(existing, values["guest"]);
            form["type"] = "local";
            return api.PostActionAsync("cluster/replication", form);
        }, "DcReplication_Added");
    }

    /// <summary>복제 작업 ID 는 "게스트-번호" — 그 게스트가 아직 쓰지 않은 가장 작은 번호를 고른다.</summary>
    internal static string NextReplicationId(IEnumerable<IReadOnlyDictionary<string, string>> existing, string guest)
    {
        var used = existing
            .Select(job => Value(job, "id"))
            .Where(id => id.StartsWith(guest + "-", StringComparison.Ordinal))
            .Select(id => int.TryParse(id[(guest.Length + 1)..], out var n) ? n : -1)
            .ToHashSet();
        var next = 0;
        while (used.Contains(next)) next++;
        return $"{guest}-{next}";
    }

    private static Task<string?> EditReplicationAsync(ProxmoxApiClient api, IReadOnlyDictionary<string, string> row,
        Window? owner)
    {
        return SubmitAsync(owner, Loc.T("DcReplication_EditTitle", row["id"]),
        [
            new FormField { Key = "schedule", LabelKey = "Table_Schedule", Initial = Value(row, "schedule") },
            new FormField { Key = "rate", LabelKey = "DcReplication_Rate", Initial = Value(row, "rate") },
            new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Value(row, "comment") },
            new FormField
            {
                Key = "enabled", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
                Initial = Value(row, "disable") is "1" ? "0" : "1"
            }
        ], values =>
        {
            var edited = values.Where(kv => kv.Key != "enabled")
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            edited["disable"] = values["enabled"] == "1" ? "0" : "1";
            return api.PutActionAsync($"cluster/replication/{Seg(row["id"])}", UpdateForm(edited));
        }, "DcReplication_Updated", titleIsKey: false);
    }

    // ------------------------------------------------------------ HA

    public static IReadOnlyList<TableAction> HaResources(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => SubmitAsync(owner, "DcHa_AddTitle",
                [
                    new FormField
                    {
                        Key = "type", LabelKey = "Table_Type", Kind = FormFieldKind.Choice, Initial = "vm",
                        Choices = [("vm", "VM"), ("ct", "CT")]
                    },
                    new FormField { Key = "vmid", LabelKey = "Table_Guest", Required = true },
                    ..HaFields(null)
                ], values =>
                {
                    var form = NonEmpty(values);
                    form.Remove("type");
                    form.Remove("vmid");
                    form["sid"] = $"{values["type"]}:{values["vmid"]}";
                    return api.PostActionAsync("cluster/ha/resources", form);
                }, "DcHa_Added")
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcHa_EditTitle", row!["sid"]), HaFields(row),
                    values => api.PutActionAsync($"cluster/ha/resources/{Seg(row["sid"])}", UpdateForm(values)),
                    "DcHa_Updated", titleIsKey: false)
            },
            DeleteAction(row => Loc.T("DcHa_DeleteConfirm", row["sid"]),
                row => api.DeleteActionAsync($"cluster/ha/resources/{Seg(row["sid"])}"), "DcHa_Deleted")
        ];
    }

    private static List<FormField> HaFields(IReadOnlyDictionary<string, string>? row)
    {
        string Initial(string key, string fallback = "") => row is null ? fallback : Value(row, key);

        return
        [
            new FormField
            {
                Key = "state", LabelKey = "Table_State", Kind = FormFieldKind.Choice, Choices = HaStates,
                Initial = Initial("state", "started")
            },
            new FormField { Key = "group", LabelKey = "Table_Group", Initial = Initial("group") },
            new FormField { Key = "max_restart", LabelKey = "Table_MaxRestart", Initial = Initial("max_restart", "1") },
            new FormField
            {
                Key = "max_relocate", LabelKey = "Table_MaxRelocate", Initial = Initial("max_relocate", "1")
            },
            new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Initial("comment") }
        ];
    }

    // ------------------------------------------------------------ 저장소

    public static IReadOnlyList<TableAction> Storage(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddStorageAsync(api, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("DcStorage_EditTitle", row!["storage"]),
                [
                    new FormField { Key = "content", LabelKey = "Table_Content", Initial = Value(row, "content") },
                    new FormField { Key = "nodes", LabelKey = "DcStorage_Nodes", Initial = Value(row, "nodes") },
                    new FormField
                    {
                        Key = "enabled", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
                        Initial = Value(row, "disable") is "1" ? "0" : "1"
                    }
                ], values =>
                {
                    var edited = values.Where(kv => kv.Key != "enabled")
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                    edited["disable"] = values["enabled"] == "1" ? "0" : "1";
                    return api.PutActionAsync($"storage/{Seg(row["storage"])}", UpdateForm(edited));
                }, "DcStorage_Updated", titleIsKey: false)
            },
            DeleteAction(row => Loc.T("DcStorage_DeleteConfirm", row["storage"]),
                row => api.DeleteActionAsync($"storage/{Seg(row["storage"])}"), "DcStorage_Deleted")
        ];
    }

    /// <summary>유형을 먼저 고른 뒤, 그 유형에 필요한 칸만 받는다.</summary>
    private static async Task<string?> AddStorageAsync(ProxmoxApiClient api, Window? owner)
    {
        var choose = new FormDialog(Loc.T("DcStorage_ChooseType"),
        [
            new FormField
            {
                Key = "type", LabelKey = "Table_Type", Kind = FormFieldKind.Choice, Initial = "dir",
                Choices = StorageType.All.Select(t => (t.Type, t.LabelKey)).ToList()
            }
        ]) { Owner = owner };
        if (choose.ShowDialog() != true || choose.Result is not { } picked) return null;

        var type = StorageType.All.First(t => t.Type == picked["type"]);
        return await SubmitAsync(owner, Loc.T("DcStorage_AddTitle", Loc.T(type.LabelKey)),
        [
            new FormField { Key = "storage", LabelKey = "Table_Id", Required = true },
            ..type.Fields,
            new FormField { Key = "content", LabelKey = "Table_Content", Initial = type.DefaultContent },
            new FormField { Key = "nodes", LabelKey = "DcStorage_Nodes" }
        ], values =>
        {
            var form = NonEmpty(values);
            form["type"] = type.Type;
            return api.PostActionAsync("storage", form);
        }, "DcStorage_Added", titleIsKey: false);
    }

    /// <summary>노드 선택지. allLabelKey 가 있으면 맨 앞에 '모든 노드'(빈 값)를 둔다.</summary>
    private static async Task<List<(string, string)>> NodeChoicesAsync(ProxmoxApiClient api, string? allLabelKey)
    {
        var nodes = (await api.GetNodesAsync()).Select(n => (n.Node, n.Node)).OrderBy(n => n.Item1).ToList();
        if (allLabelKey is not null) nodes.Insert(0, (string.Empty, allLabelKey));
        return nodes;
    }
}
