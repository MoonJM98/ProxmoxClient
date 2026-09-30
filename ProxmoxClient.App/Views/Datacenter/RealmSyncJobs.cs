using System.Text.RegularExpressions;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     영역 동기화 작업(웹 UI RealmSyncJobView, PVE 8+) — cluster/jobs/realm-sync. LDAP·AD 영역을 일정에 맞춰
///     동기화한다. 범위·새 사용자 사용·사라진 항목 제거는 비우면 영역의 동기화 기본값을 따른다.
/// </summary>
internal static partial class RealmSyncJobs
{
    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "id", HeaderKey = "Table_Id", Width = 130 },
        new() { Key = "realm", HeaderKey = "DcRealmJob_Realm", Width = 110 },
        new() { Key = "enabled", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.Flag },
        new() { Key = "schedule", HeaderKey = "Table_Schedule", Width = 120 },
        new() { Key = "next-run", HeaderKey = "Table_NextRun", Width = 140, Format = TableFormats.EpochDate },
        new() { Key = "scope", HeaderKey = "DcRealms_SyncScope", Width = 80 },
        new() { Key = "comment", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly (string, string)[] Scopes =
    [
        ("", "StorageField_Default"), ("both", "DcRealms_SyncBoth"), ("users", "DcTab_Users"),
        ("groups", "DcTab_Groups")
    ];

    private static readonly (string, string)[] EnableNewChoices =
        [("", "StorageField_Default"), ("1", "GuestOptions_Yes"), ("0", "GuestOptions_No")];

    private static readonly (string, string)[] Vanished =
    [
        ("none", "DcRealmJob_VanishedNone"), ("acl", "DcRealms_VanishedAcl"), ("entry", "DcRealms_VanishedEntry"),
        ("properties", "DcRealms_VanishedProps")
    ];

    /// <summary>인증 영역 화면 — 영역 목록과 동기화 작업(8.0+, 낮은 서버엔 탭이 없다)을 하위 탭으로 나눈다.</summary>
    public static SubTabsView View(ProxmoxApiClient api, bool canEdit, Func<UIElement> realms)
    {
        return new SubTabsView(new List<SubTab>
        {
            new("DcTab_Realms", realms),
            new("DcRealmJob_Tab", () => new TableTab(() => api.Realms.ListSyncJobsAsync(), Columns,
                    "DcRealmJob_Hint", canEdit ? Actions(api) : null),
                api.Realms.Feature(nameof(RealmsApi.ListSyncJobsAsync)))
        });
    }

    private static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction { LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddAsync(api, owner) },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditAsync(api, row!["id"], owner)
            },
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("DcRealmJob_DeleteConfirm", row!["id"]),
                Run = async (row, _) =>
                {
                    await api.Realms.DeleteSyncJobAsync(row!["id"]);
                    return Loc.T("DcRealmJob_Deleted");
                }
            }
        ];
    }

    private static async Task<string?> AddAsync(ProxmoxApiClient api, Window? owner)
    {
        // 동기화는 LDAP·AD 만 된다 — 고를 영역이 없으면 창을 열지 않는다
        var realms = (await api.Realms.ListAsync())
            .Where(r => Value(r, "type") is "ldap" or "ad")
            .Select(r => (r["realm"], r["realm"]))
            .ToList();
        if (realms.Count == 0) return Loc.T("DcRealmJob_NoRealm");

        IReadOnlyList<FormField> fields =
        [
            new FormField { Key = "id", LabelKey = "Table_Id", Required = true, Trim = true },
            new FormField { Key = "realm", LabelKey = "DcRealmJob_Realm", Kind = FormFieldKind.Choice,
                Choices = realms, Initial = realms[0].Item1 },
            ..CommonFields(new Dictionary<string, string>())
        ];
        return await SubmitAsync(owner, "DcRealmJob_AddTitle", fields,
            values => api.Realms.CreateSyncJobAsync(values["id"], CreateForm(values)),
            "DcRealmJob_Added", validate: values => Validate(values, true));
    }

    private static async Task<string?> EditAsync(ProxmoxApiClient api, string id, Window? owner)
    {
        var job = await api.Realms.GetSyncJobAsync(id);
        // 영역은 만든 뒤 바꿀 수 없다 — 제목에만 보인다
        return await SubmitAsync(owner, Loc.T("DcRealmJob_EditTitle", id, Value(job, "realm")),
            CommonFields(job).ToList(),
            values => api.Realms.UpdateSyncJobAsync(id, EditForm(values)),
            "DcRealmJob_Updated", false, values => Validate(values, false));
    }

    private static IEnumerable<FormField> CommonFields(IReadOnlyDictionary<string, string> job)
    {
        yield return new FormField { Key = "schedule", LabelKey = "Table_Schedule", Required = true, Trim = true,
            Initial = job.TryGetValue("schedule", out var s) ? s : "sat 01:00", Hint = Loc.T("DcBackup_ScheduleHint") };
        yield return new FormField { Key = "enabled", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
            Initial = Flag(Value(job, "enabled")) is "0" ? "0" : "1" };
        yield return new FormField { Key = "scope", LabelKey = "DcRealms_SyncScope", Kind = FormFieldKind.Choice,
            Choices = Scopes, Initial = Value(job, "scope") };
        yield return new FormField { Key = "enable-new", LabelKey = "DcRealms_EnableNew", Kind = FormFieldKind.Choice,
            Choices = EnableNewChoices, Initial = Flag(Value(job, "enable-new")) };
        yield return new FormField { Key = "remove-vanished", LabelKey = "DcRealms_RemoveVanished",
            Kind = FormFieldKind.MultiChoice, Choices = Vanished,
            Initial = Value(job, "remove-vanished").Replace(';', ','),
            Hint = Loc.T("DcRealmJob_VanishedHint") };
        yield return new FormField { Key = "comment", LabelKey = "Table_Comment", Initial = Value(job, "comment") };
    }

    /// <summary>화면 값 → 서버 값: 제거 항목은 ';' 로 잇는다. ID 는 경로에 들어간다.</summary>
    private static Dictionary<string, string> Collect(IReadOnlyDictionary<string, string> values)
    {
        return values.Where(kv => kv.Key != "id")
            .ToDictionary(kv => kv.Key, kv => kv.Key == "remove-vanished" ? kv.Value.Replace(',', ';') : kv.Value,
                StringComparer.Ordinal);
    }

    internal static Dictionary<string, string> CreateForm(IReadOnlyDictionary<string, string> values)
    {
        return NonEmpty(Collect(values));
    }

    /// <summary>수정 — 비운 칸은 delete 로 영역 기본값에 맡긴다.</summary>
    internal static Dictionary<string, string> EditForm(IReadOnlyDictionary<string, string> values)
    {
        return UpdateForm(Collect(values));
    }

    internal static string? Validate(IReadOnlyDictionary<string, string> values, bool isCreate)
    {
        if (isCreate && !IdPattern().IsMatch(Value(values, "id"))) return Loc.T("DcRealmJob_BadId");
        var vanished = Value(values, "remove-vanished").Split(',', StringSplitOptions.RemoveEmptyEntries);
        return vanished.Contains("none") && vanished.Length > 1 ? Loc.T("DcRealmJob_NoneAlone") : null;
    }

    /// <summary>서버 참·거짓(1/0, true/false) → "1"/"0". 없으면 빈 값(기본값).</summary>
    private static string Flag(string raw)
    {
        return raw switch { "1" or "true" => "1", "0" or "false" => "0", _ => string.Empty };
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]+$")]
    private static partial Regex IdPattern();
}
