using System.Text.RegularExpressions;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     저장소 추가·수정(웹 UI storage/Base.js + 유형별 편집기) — ID·유형 칸, 콘텐츠(유형별 목록에서 여러 개),
///     노드 제한, 사용·공유, 사전 할당, 백업 보존(prune-backups). 수정은 서버 설정 전체를 읽어 채우고,
///     만든 뒤 바꿀 수 없는 칸은 보여 주기만 한다. 비밀(암호·키링)은 비우면 그대로 둔다.
/// </summary>
internal static partial class StorageActions
{
    private static readonly string[] KeepKeys =
        ["keep-last", "keep-hourly", "keep-daily", "keep-weekly", "keep-monthly", "keep-yearly"];

    private static readonly (string, string)[] Preallocations =
    [
        ("", "StorageField_Default"), ("off", "off"), ("metadata", "metadata"), ("falloc", "falloc"),
        ("full", "full")
    ];

    [GeneratedRegex("^[a-z][a-z0-9_.-]*[a-z0-9]$", RegexOptions.IgnoreCase)]
    private static partial Regex StorageIdPattern();

    public static IReadOnlyList<TableAction> Actions(ProxmoxApiClient api)
    {
        return
        [
            new TableAction { LabelKey = "Action_Add", IconKey = "IconPlus", Run = (_, owner) => AddAsync(api, owner) },
            new TableAction
            {
                LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => EditAsync(api, row!["storage"], owner)
            },
            DeleteAction(row => Loc.T("DcStorage_DeleteConfirm", row["storage"]),
                row => api.Storage.DeleteAsync(row["storage"]), "DcStorage_Deleted")
        ];
    }

    private static async Task<string?> AddAsync(ProxmoxApiClient api, Window? owner)
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
        var nodes = await NodeChoicesAsync(api);
        return await SubmitAsync(owner, Loc.T("DcStorage_AddTitle", Loc.T(type.LabelKey)),
            Fields(type, null, nodes, api), values => api.Storage.CreateAsync(CreateForm(type, values)),
            "DcStorage_Added", titleIsKey: false, validate: values => Validate(type, values, isCreate: true));
    }

    private static async Task<string?> EditAsync(ProxmoxApiClient api, string storage, Window? owner)
    {
        var config = (await api.Storage.GetAsync(storage))
            .ToDictionary(kv => kv.Key,
                kv => kv.Key == "prune-backups" ? PropertyString.FromJsonObject(kv.Value) : kv.Value,
                StringComparer.Ordinal);
        var typeName = config.TryGetValue("type", out var t) ? t : string.Empty;
        if (StorageType.All.FirstOrDefault(s => s.Type == typeName) is not { } type)
            return Loc.T("DcStorage_UnsupportedType", typeName);

        var nodes = await NodeChoicesAsync(api);
        return await SubmitAsync(owner, Loc.T("DcStorage_EditTitle", storage),
            Fields(type, config, nodes),
            values => api.Storage.UpdateAsync(storage, EditForm(type, values)), "DcStorage_Updated",
            titleIsKey: false, validate: values => Validate(type, values, isCreate: false));
    }

    private static async Task<IReadOnlyList<(string, string)>> NodeChoicesAsync(ProxmoxApiClient api)
    {
        return (await api.GetNodesAsync()).Select(n => (n.Node, n.Node))
            .OrderBy(n => n.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------ 입력 칸

    /// <summary>
    ///     <paramref name="config" /> 가 null 이면 추가, 아니면 수정(서버 설정으로 채움). api 를 주면 추가 창의
    ///     서버·풀 칸을 서버에서 찾아볼 수 있다(<see cref="StorageScans" />).
    /// </summary>
    internal static List<FormField> Fields(StorageType type, IReadOnlyDictionary<string, string>? config,
        IReadOnlyList<(string, string)> nodes, ProxmoxApiClient? api = null)
    {
        var isCreate = config is null;
        string V(string key) => config is not null && config.TryGetValue(key, out var v) ? v : string.Empty;

        var fields = new List<FormField>();
        if (isCreate)
            fields.Add(new FormField { Key = "storage", LabelKey = "Table_Id", Required = true, Trim = true,
                Hint = Loc.T("StorageHint_Id") });

        foreach (var (field, createOnly) in type.Fields)
        {
            if (!isCreate && createOnly)
            {
                // 만든 뒤에는 바꿀 수 없다 — 값만 보여 준다(웹 UI 의 표시 칸)
                fields.Add(new FormField { Key = "_" + field.Key, Kind = FormFieldKind.Section,
                    LabelKey = $"{Loc.T(field.LabelKey)}: {V(field.Key)}" });
                continue;
            }

            var secret = field.Kind == FormFieldKind.Password || field.Key == "keyring";
            fields.Add(Localized(field, isCreate ? field.Initial : secret ? "" : V(field.Key),
                required: field.Required && (isCreate || !secret),
                hint: !isCreate && secret ? "StorageHint_KeepSecret" : field.Hint,
                suggest: isCreate && api is not null ? StorageScans.For(api, type, type.Fields, field.Key) : null));
        }

        fields.AddRange(ContentFields(type, isCreate ? type.DefaultContent : V("content")));
        fields.Add(new FormField { Key = "nodes", LabelKey = "DcStorage_Nodes", Kind = FormFieldKind.MultiChoice,
            Choices = nodes, Initial = V("nodes"), Hint = Loc.T("StorageHint_Nodes") });
        fields.Add(new FormField { Key = "enable", LabelKey = "Table_Enabled", Kind = FormFieldKind.Bool,
            Initial = V("disable") is "1" ? "0" : "1" });
        if (type.HasShared)
            fields.Add(new FormField { Key = "shared", LabelKey = "Table_Shared", Kind = FormFieldKind.Bool,
                Initial = V("shared") is "1" ? "1" : "0", Hint = Loc.T("StorageHint_Shared") });
        if (type.HasPreallocation)
            fields.Add(new FormField { Key = "preallocation", LabelKey = "StorageField_Preallocation",
                Kind = FormFieldKind.Choice, Choices = Preallocations, Initial = V("preallocation"), Advanced = true });
        if (type.HasRetention)
            fields.AddRange(RetentionFields(isCreate ? "keep-all=1" : V("prune-backups"),
                V("max-protected-backups")));
        return fields;
    }

    /// <summary>힌트는 리소스 키로 두었다가 창을 만들 때 번역한다(정적 초기화에서 번역하지 않도록).</summary>
    private static FormField Localized(FormField f, string initial, bool required, string? hint,
        Func<IReadOnlyDictionary<string, string>, Task<IReadOnlyList<(string, string)>>>? suggest = null)
    {
        var text = hint is null ? null : Loc.T(hint);
        if (suggest is not null)
            text = text is null ? Loc.T("StorageScan_Hint") : $"{text} {Loc.T("StorageScan_Hint")}";
        return new FormField
        {
            Key = f.Key, LabelKey = f.LabelKey, Kind = f.Kind, Choices = f.Choices, Required = required,
            Initial = initial, Advanced = f.Advanced, Trim = f.Trim, Hint = text, Suggest = suggest
        };
    }

    private static IEnumerable<FormField> ContentFields(StorageType type, string current)
    {
        if (type.FixedContent is not null) yield break;
        if (type.Type == "iscsi")
        {
            // iSCSI 는 LUN 을 디스크로 바로 쓸지(images) 말지(none) 하나만 고른다
            yield return new FormField { Key = "content", LabelKey = "StorageField_UseLuns", Kind = FormFieldKind.Bool,
                Initial = current.Contains("images") ? "1" : "0" };
            yield break;
        }

        yield return new FormField
        {
            Key = "content", LabelKey = "Table_Content", Kind = FormFieldKind.MultiChoice, Initial = current,
            Choices = type.Contents.Select(c => (c, ContentLabel(c))).ToList()
        };
    }

    /// <summary>백업 정리 창의 보존 규칙 칸 — 저장소 설정 창과 같은 칸(보호 백업 최대 수는 빼고).</summary>
    internal static IReadOnlyList<FormField> PruneFields(string pruneBackups)
    {
        return RetentionFields(pruneBackups, string.Empty).Where(f => f.Key != "max-protected-backups").ToList();
    }

    private static IEnumerable<FormField> RetentionFields(string pruneBackups, string maxProtected)
    {
        var p = PropertyString.Parse(pruneBackups);
        var keepAll = p.IsOn("keep-all") || KeepKeys.All(k => !p.Has(k));
        yield return new FormField { Key = "_retention", LabelKey = "StorageField_Retention",
            Kind = FormFieldKind.Section };
        yield return new FormField { Key = "keep-all", LabelKey = "StorageField_KeepAll", Kind = FormFieldKind.Bool,
            Initial = keepAll ? "1" : "0", Hint = Loc.T("StorageHint_KeepAll") };
        foreach (var key in KeepKeys)
            yield return new FormField { Key = key, LabelKey = KeepLabel(key),
                Initial = p.Get(key), Trim = true };
        yield return new FormField { Key = "max-protected-backups", LabelKey = "StorageField_MaxProtected",
            Initial = maxProtected, Trim = true, Advanced = true, Hint = Loc.T("StorageHint_MaxProtected") };
    }

    private static string ContentLabel(string content)
    {
        return content switch
        {
            "images" => "StorageContent_Images",
            "rootdir" => "StorageContent_rootdir",
            "vztmpl" => "StorageContent_vztmpl",
            "iso" => "StorageContent_Iso",
            "backup" => "StorageContent_backup",
            "snippets" => "StorageContent_snippets",
            "import" => "StorageContent_import",
            _ => content
        };
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
}
