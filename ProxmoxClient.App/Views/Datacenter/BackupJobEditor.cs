using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     백업 일정 편집(웹 UI dc/Backup.js + BackupJobPrune·BackupAdvancedOptions) — 일반(노드·저장소·일정·대상·방식),
///     보존(비우면 저장소 설정을 따름), 알림, 메모 템플릿, 고급(대역폭·zstd·작업자·플리싱·놓친 일정·PBS 변경 감지).
///     서버는 보존·성능·플리싱을 GET 에서 객체로 주므로 입력 형식(key=value)으로 되돌려 채운다.
/// </summary>
internal static class BackupJobEditor
{
    private static readonly string[] KeepKeys =
        ["keep-last", "keep-hourly", "keep-daily", "keep-weekly", "keep-monthly", "keep-yearly"];

    public static async Task<string?> EditAsync(ProxmoxApiClient api, string? id, Window? owner)
    {
        var job = id is null
            ? new Dictionary<string, string>()
            : (await api.Jobs.GetBackupJobRawAsync(id))
            .ToDictionary(kv => kv.Key,
                kv => kv.Key is "prune-backups" or "performance" or "fleecing"
                    ? PropertyString.FromJsonObject(kv.Value)
                    : kv.Value, StringComparer.Ordinal);
        var fields = await FieldsAsync(api, id is null ? null : job);
        var title = id is null ? Loc.T("DcBackup_AddTitle") : Loc.T("DcBackup_EditTitle", id);

        return await SubmitAsync(owner, title, fields, values =>
        {
            var form = Collect(values, job);
            return id is null
                ? api.Jobs.CreateBackupJobAsync(NonEmpty(form))
                // 고치기에서는 고른 방식에 해당하지 않는 칸을 빈 값으로 넘겨 서버 설정에서 지운다
                : api.Jobs.UpdateBackupJobAsync(id, UpdateForm(form));
        }, id is null ? "DcBackup_Added" : "DcBackup_Updated", titleIsKey: false, validate: Validate,
            target: api.Jobs.Feature(nameof(JobsApi.CreateBackupJobAsync)));
    }

    private static async Task<List<FormField>> FieldsAsync(ProxmoxApiClient api,
        IReadOnlyDictionary<string, string>? job)
    {
        string V(string key, string fallback = "") =>
            job is null ? fallback : job.TryGetValue(key, out var v) ? v : string.Empty;
        FormField Text(string key, string labelKey, string fallback = "", string? hint = null, bool adv = false) =>
            new() { Key = key, LabelKey = labelKey, Initial = V(key, fallback), Trim = true, Advanced = adv,
                Hint = hint is null ? null : Loc.T(hint) };

        // 백업을 담을 수 있고 꺼지지 않은 저장소만(웹 UI 와 같다)
        var storages = (await api.Storage.ListAsync())
            .Where(s => Value(s, "content").Split(',').Contains("backup") && Value(s, "disable") is not "1")
            .Select(s => (s["storage"], s["storage"])).ToList();
        var nodes = (await api.GetNodesAsync()).Select(n => (n.Node, n.Node)).OrderBy(n => n.Item1).ToList();
        var pools = (await api.Pools.ListAsync()).Select(p => (Value(p, "poolid"), Value(p, "poolid"))).ToList();
        var selection = V("pool").Length > 0 ? "pool" : V("vmid").Length > 0 ? "vmid" : "all";
        var prune = PropertyString.Parse(V("prune-backups"));
        var performance = PropertyString.Parse(V("performance"));
        var fleecing = PropertyString.Parse(V("fleecing"), "enabled");

        var fields = new List<FormField>();
        if (job is null)
            fields.Add(Text("id", "DcBackup_JobId", hint: "DcBackup_JobIdHint", adv: true));
        fields.AddRange(
        [
            new FormField { Key = "node", LabelKey = "Table_Node", Kind = FormFieldKind.Choice,
                Choices = [("", "DcBackup_AllNodes"), ..nodes], Initial = V("node") },
            new FormField { Key = "storage", LabelKey = "Table_Storage", Kind = FormFieldKind.Choice,
                Choices = storages, Initial = V("storage"), Required = true },
            new FormField { Key = "schedule", LabelKey = "Table_Schedule", Required = true, Trim = true,
                Initial = V("schedule", "21:00"), Hint = Loc.T("DcBackup_ScheduleHint") },
            new FormField { Key = "selection", LabelKey = "DcBackup_Selection", Kind = FormFieldKind.Choice,
                Initial = selection,
                Choices = [("all", "DcBackup_SelectAll"), ("vmid", "DcBackup_SelectGuests"),
                    ("pool", "DcBackup_SelectPool")] },
            Text("vmid", "DcBackup_Guests", hint: "DcBackup_GuestsHint"),
            new FormField { Key = "pool", LabelKey = "DcTab_Pools", Kind = FormFieldKind.Choice, Initial = V("pool"),
                Choices = [("", "GuestOptions_NotSet"), ..pools] },
            Text("exclude", "DcBackup_Exclude", hint: "DcBackup_GuestsHint"),
            new FormField { Key = "mode", LabelKey = "Table_Mode", Kind = FormFieldKind.Choice,
                Choices = ClusterActions.BackupModes,
                Initial = V("mode", "snapshot") },
            new FormField { Key = "compress", LabelKey = "DcBackup_Compress", Kind = FormFieldKind.Choice,
                Choices = ClusterActions.CompressTypes, Initial = V("compress", "zstd") },
            new FormField { Key = "enabled", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
                Initial = V("enabled", "1") is "0" ? "0" : "1" },
            Text("comment", "Table_Comment"),

            // 알림
            new FormField { Key = "_notify", LabelKey = "DcBackup_NotifySection", Kind = FormFieldKind.Section },
            new FormField { Key = "notification-mode", LabelKey = "DcBackup_NotifyMode", Kind = FormFieldKind.Choice,
                Initial = V("notification-mode"),
                Choices = [("", "DcBackup_NotifyAuto"), ("notification-system", "DcBackup_NotifySystem"),
                    ("legacy-sendmail", "DcBackup_NotifyEmail")] },
            Text("mailto", "DcNotify_MailTo", hint: "DcBackup_MailtoHint"),
            new FormField { Key = "mailnotification", LabelKey = "DcBackup_MailWhen", Kind = FormFieldKind.Choice,
                Initial = V("mailnotification"),
                Choices = [("", "StorageField_Default"), ("always", "DcBackup_MailAlways"),
                    ("failure", "DcBackup_MailFailure")] },

            // 보존 — 비우면 저장소의 보존 설정을 따른다
            new FormField { Key = "_retention", LabelKey = "StorageField_Retention", Kind = FormFieldKind.Section },
            new FormField { Key = "keep-all", LabelKey = "StorageField_KeepAll", Kind = FormFieldKind.Bool,
                Initial = prune.IsOn("keep-all") ? "1" : "0", Hint = Loc.T("DcBackup_RetentionHint") },
            ..KeepKeys.Select(k => new FormField { Key = k, LabelKey = KeepLabel(k), Initial = prune.Get(k),
                Trim = true }),

            // 메모 템플릿
            new FormField { Key = "notes-template", LabelKey = "DcBackup_NotesTemplate", Initial = V("notes-template"),
                Hint = Loc.T("DcBackup_NotesHint") },

            // 고급
            Text("bwlimit", "DcBackup_Bwlimit", hint: "DcBackup_BwlimitHint", adv: true),
            Text("zstd", "DcBackup_ZstdThreads", hint: "DcBackup_ZstdHint", adv: true),
            // 성능·플리싱은 한 파라미터 안의 하위 값이라 칸 이름과 달라 Requires 로 알린다(7.2+·8.2+)
            new FormField { Key = "max-workers", LabelKey = "DcBackup_MaxWorkers",
                Initial = performance.Get("max-workers"),
                Trim = true, Advanced = true, Hint = Loc.T("DcBackup_MaxWorkersHint"),
                Requires = api.Jobs.Feature(nameof(JobsApi.CreateBackupJobAsync), "performance") },
            new FormField { Key = "fleecing-enabled", LabelKey = "DcBackup_Fleecing", Kind = FormFieldKind.Bool,
                Initial = fleecing.IsOn("enabled") ? "1" : "0", Advanced = true,
                Hint = Loc.T("DcBackup_FleecingHint"),
                Requires = api.Jobs.Feature(nameof(JobsApi.CreateBackupJobAsync), "fleecing") },
            new FormField { Key = "fleecing-storage", LabelKey = "DcBackup_FleecingStorage",
                Initial = fleecing.Get("storage"), Trim = true, Advanced = true,
                Requires = api.Jobs.Feature(nameof(JobsApi.CreateBackupJobAsync), "fleecing") },
            new FormField { Key = "repeat-missed", LabelKey = "DcBackup_RepeatMissed", Kind = FormFieldKind.Bool,
                Initial = V("repeat-missed") is "1" ? "1" : "0", Advanced = true },
            new FormField { Key = "pbs-change-detection-mode", LabelKey = "DcBackup_PbsDetection",
                Kind = FormFieldKind.Choice, Initial = V("pbs-change-detection-mode"), Advanced = true,
                Choices = [("", "StorageField_Default"), ("legacy", "legacy"), ("data", "data"),
                    ("metadata", "metadata")] },
            new FormField { Key = "protected", LabelKey = "DcBackup_Protected", Kind = FormFieldKind.Bool,
                Initial = V("protected") is "1" ? "1" : "0", Advanced = true },
            Text("ionice", "DcBackup_Ionice", hint: "DcBackup_IoniceHint", adv: true),
            Text("lockwait", "DcBackup_Lockwait", hint: "DcBackup_LockwaitHint", adv: true)
        ]);
        return fields;
    }

    private static string KeepLabel(string key)
    {
        return key switch
        {
            "keep-last" => "StorageField_keep_last",
            "keep-hourly" => "StorageField_keep_hourly",
            "keep-daily" => "StorageField_keep_daily",
            "keep-weekly" => "StorageField_keep_weekly",
            "keep-monthly" => "StorageField_keep_monthly",
            _ => "StorageField_keep_yearly"
        };
    }

    /// <summary>
    ///     화면 칸 → 서버 값: 대상 방식 정리, 보존(prune-backups)·성능(performance)·플리싱(fleecing) 조립.
    ///     보존을 모두 비우면 빈 값(저장소 설정 따름), 끈 체크 칸은 빈 값(기본값).
    /// </summary>
    internal static Dictionary<string, string> Collect(IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? original = null)
    {
        string O(string key) => original is not null && original.TryGetValue(key, out var v) ? v : string.Empty;
        var form = ClusterActions.BackupSelection(values.Where(kv => !kv.Key.StartsWith('_')
                && !kv.Key.StartsWith("keep-")
                && kv.Key is not ("max-workers" or "fleecing-enabled" or "fleecing-storage"))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
        form["prune-backups"] = values["keep-all"] == "1"
            ? "keep-all=1"
            : string.Join(',', KeepKeys.Where(k => values[k].Trim().Length > 0).Select(k => $"{k}={values[k].Trim()}"));
        form["performance"] = PropertyString.Parse(O("performance"))
            .With("max-workers", values.GetValueOrDefault("max-workers", "").Trim()).Format();
        form["fleecing"] = values.GetValueOrDefault("fleecing-enabled") == "1"
            ? PropertyString.Parse(O("fleecing"), "enabled").With("enabled", "1")
                .With("storage", values.GetValueOrDefault("fleecing-storage", "").Trim()).Format()
            : string.Empty;
        foreach (var flag in new[] { "repeat-missed", "protected" })
            if (form.TryGetValue(flag, out var on) && on != "1") form[flag] = string.Empty;
        return form;
    }

    internal static string? Validate(IReadOnlyDictionary<string, string> values)
    {
        var problem = values["selection"] switch
        {
            "vmid" when values["vmid"].Length == 0 => Loc.T("DcBackup_GuestsRequired"),
            "pool" when values["pool"].Length == 0 => Loc.T("DcBackup_PoolRequired"),
            _ => null
        };
        if (problem is not null) return problem;
        if (values["storage"].Length == 0) return Loc.T("DcBackup_PickStorage");
        foreach (var key in new[] { "vmid", "exclude" })
            if (values[key].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(id => !int.TryParse(id, out var n) || n < 100))
                return Loc.T("DcBackup_BadGuests");
        foreach (var key in KeepKeys.Concat(["bwlimit", "zstd", "max-workers", "ionice", "lockwait"]))
            if (values.GetValueOrDefault(key, "").Trim() is { Length: > 0 } text
                && !(int.TryParse(text, out var n) && n >= 0))
                return Loc.T("DcBackup_BadNumber");
        return null;
    }
}
