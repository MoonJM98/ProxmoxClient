using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>데이터센터 백업 일정·복제·HA·저장소 화면의 추가/편집/삭제 버튼.</summary>
internal static class ClusterActions
{
    internal static readonly IReadOnlyList<(string, string)> BackupModes =
        [("snapshot", "BackupMode_Snapshot"), ("suspend", "BackupMode_Suspend"), ("stop", "BackupMode_Stop")];

    internal static readonly IReadOnlyList<(string, string)> CompressTypes =
        [("zstd", "ZSTD"), ("lzo", "LZO"), ("gzip", "GZIP"), ("0", "Compress_None")];

    // ------------------------------------------------------------ 백업 일정

    public static IReadOnlyList<TableAction> BackupJobs(ProxmoxApiClient api)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Add", IconKey = "IconPlus",
                Run = (_, owner) => BackupJobEditor.EditAsync(api, null, owner)
            },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => BackupJobEditor.EditAsync(api, row!["id"], owner)
            },
            DeleteAction(row => Loc.T("DcBackup_DeleteConfirm", row["id"]),
                row => api.Jobs.DeleteBackupJobAsync(row["id"]), "DcBackup_Deleted"),
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
                    // 표 행은 배열을 ", " 로 이어 경로 안의 쉼표와 구분할 수 없다 — 설정을 다시 읽는다
                    var job = await api.Jobs.GetBackupJobAsync(row!["id"]);
                    var started = await api.RunBackupJobNowAsync(job, online);
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
                LabelKey = "DcBackup_Simulate", IconKey = "IconRefresh",
                Run = (row, owner) => SimulateScheduleAsync(api, row is null ? "21:00" : Value(row, "schedule"), owner)
            },
            new TableAction
            {
                LabelKey = "DcBackup_NotBackedUp", IconKey = "IconSearch",
                Run = (_, owner) => Task.FromResult(TableWindow.ShowModal(owner, Loc.T("DcBackup_NotBackedUp"),
                    new TableTab(() => api.Jobs.NotBackedUpAsync(), NotBackedUpColumns,
                        "DcBackup_NotBackedUpHint")))
            }
        ];
    }

    private static readonly IReadOnlyList<TableColumn> ScheduleColumns =
    [
        new() { Key = "timestamp", HeaderKey = "Table_NextRun", Width = 0, Format = TableFormats.EpochDate }
    ];

    /// <summary>
    ///     일정 확인(웹 UI 의 Schedule Simulator) — 일정 식을 서버에 물어 다음 실행 시각 10개를 보여 준다.
    /// </summary>
    private static async Task<string?> SimulateScheduleAsync(ProxmoxApiClient api, string schedule, Window? owner)
    {
        var dialog = new FormDialog(Loc.T("DcBackup_Simulate"),
        [
            new FormField { Key = "schedule", LabelKey = "Table_Schedule", Required = true, Initial = schedule,
                Trim = true, Hint = Loc.T("DcBackup_ScheduleHint") }
        ]) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } values) return null;

        return TableWindow.ShowModal(owner, Loc.T("DcBackup_SimulateTitle", values["schedule"]),
            new TableTab(() => api.Jobs.AnalyzeScheduleAsync(values["schedule"]), ScheduleColumns,
                "DcBackup_SimulateHint"));
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
                row => api.Jobs.DeleteReplicationAsync(row["id"]), "DcReplication_Deleted")
        ];
    }

    private static async Task<string?> AddReplicationAsync(ProxmoxApiClient api, Window? owner)
    {
        var nodes = await NodeChoicesAsync(api, null);
        var existing = await api.Jobs.ListReplicationAsync();
        // 게스트는 목록에서 — 그 게스트가 있는 노드는 대상이 될 수 없다(웹 UI 도 막는다)
        var guests = (await api.Cluster.ResourcesAsync("vm"))
            .Where(r => Value(r, "vmid").Length > 0 && Value(r, "template") is not "1")
            .OrderBy(r => int.TryParse(Value(r, "vmid"), out var id) ? id : int.MaxValue)
            .ToList();
        var guestNode = guests.ToDictionary(r => Value(r, "vmid"), r => Value(r, "node"));

        return await SubmitAsync(owner, "DcReplication_AddTitle",
        [
            new FormField
            {
                Key = "guest", LabelKey = "Table_Guest", Kind = FormFieldKind.Choice, Required = true,
                Choices = guests.Select(r => (Value(r, "vmid"),
                    $"{Value(r, "vmid")} ({Value(r, "name")}) — {Value(r, "node")}")).ToList()
            },
            new FormField { Key = "target", LabelKey = "Table_Target", Kind = FormFieldKind.Choice, Choices = nodes,
                Required = true },
            new FormField { Key = "schedule", LabelKey = "Table_Schedule", Initial = "*/15", Trim = true },
            new FormField { Key = "rate", LabelKey = "DcReplication_Rate", Trim = true,
                Hint = Loc.T("DcReplication_RateHint") },
            new FormField { Key = "comment", LabelKey = "Table_Comment" },
            new FormField { Key = "enabled", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool, Initial = "1" }
        ], values =>
        {
            var form = NonEmpty(values);
            form.Remove("guest");
            form.Remove("enabled");
            form["id"] = NextReplicationId(existing, values["guest"]);
            form["type"] = "local";
            if (values["enabled"] != "1") form["disable"] = "1";
            return api.Jobs.CreateReplicationAsync(form);
        }, "DcReplication_Added", validate: values =>
        {
            if (values["guest"].Length == 0) return Loc.T("DcReplication_PickGuest");
            if (values["target"].Length == 0) return Loc.T("DcReplication_PickTarget");
            if (guestNode.TryGetValue(values["guest"], out var source) && source == values["target"])
                return Loc.T("DcReplication_SameNode", source);
            return RateProblem(values["rate"]);
        });
    }

    /// <summary>속도 제한(MB/s) — 비우면 제한 없음, 있으면 1 이상의 숫자.</summary>
    private static string? RateProblem(string rate)
    {
        return rate.Trim().Length == 0
               || double.TryParse(rate, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var r) && r >= 1
            ? null
            : Loc.T("DcReplication_BadRate");
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
            new FormField { Key = "rate", LabelKey = "DcReplication_Rate", Initial = Value(row, "rate"), Trim = true },
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
            return api.Jobs.UpdateReplicationAsync(row["id"], UpdateForm(edited));
        }, "DcReplication_Updated", titleIsKey: false, validate: values => RateProblem(values["rate"]));
    }

    // ------------------------------------------------------------ HA

    /// <summary>노드 선택지. allLabelKey 가 있으면 맨 앞에 '모든 노드'(빈 값)를 둔다.</summary>
    private static async Task<List<(string, string)>> NodeChoicesAsync(ProxmoxApiClient api, string? allLabelKey)
    {
        var nodes = (await api.GetNodesAsync()).Select(n => (n.Node, n.Node)).OrderBy(n => n.Item1).ToList();
        if (allLabelKey is not null) nodes.Insert(0, (string.Empty, allLabelKey));
        return nodes;
    }
}
