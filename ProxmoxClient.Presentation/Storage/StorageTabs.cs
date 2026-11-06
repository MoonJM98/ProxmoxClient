using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Presentation.Shared;
using static ProxmoxClient.Presentation.Shared.ActionHelpers;
using static ProxmoxClient.Presentation.Shared.FormHelpers;

namespace ProxmoxClient.Presentation.Storage;

/// <summary>
///     저장소 창의 탭 — 웹 UI 에서 노드 아래 저장소를 골랐을 때의 요약·백업·ISO·CT 템플릿·디스크 이미지 화면.
///     저장소가 담을 수 있는 콘텐츠 종류의 탭만 보인다. 창은 앱마다(StorageNavigator), 그래프·파일 복원·올리기는
///     앱이 만든다(<see cref="IAppParts" />).
/// </summary>
public static class StorageTabs
{
    private static readonly IReadOnlyList<TableColumn> SummaryColumns =
    [
        new() { Key = "name", HeaderKey = "Table_Name", Width = 180 },
        new() { Key = "value", HeaderKey = "NodeSubscription_Value", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> FileColumns =
    [
        new() { Key = "volid", HeaderKey = "BackupList_File", Width = 0 },
        new() { Key = "format", HeaderKey = "StorageContent_Format", Width = 80 },
        new() { Key = "size", HeaderKey = "Table_Size", Width = 100, Format = TableFormats.Bytes },
        new() { Key = "ctime", HeaderKey = "BackupList_Created", Width = 140, Format = TableFormats.EpochDate }
    ];

    private static readonly IReadOnlyList<TableColumn> BackupColumns =
    [
        new() { Key = "volid", HeaderKey = "BackupList_File", Width = 0 },
        new() { Key = "vmid", HeaderKey = "Table_Guest", Width = 70 },
        new() { Key = "size", HeaderKey = "Table_Size", Width = 100, Format = TableFormats.Bytes },
        new() { Key = "ctime", HeaderKey = "BackupList_Created", Width = 140, Format = TableFormats.EpochDate },
        new() { Key = "protected", HeaderKey = "BackupList_Protected", Width = 60, Format = TableFormats.Flag },
        new() { Key = "notes", HeaderKey = "Table_Comment", Width = 160 }
    ];

    private static readonly IReadOnlyList<TableColumn> DiskColumns =
    [
        new() { Key = "volid", HeaderKey = "BackupList_File", Width = 0 },
        new() { Key = "vmid", HeaderKey = "Table_Guest", Width = 70 },
        new() { Key = "format", HeaderKey = "StorageContent_Format", Width = 80 },
        new() { Key = "size", HeaderKey = "Table_Size", Width = 100, Format = TableFormats.Bytes }
    ];

    private static readonly (string Key, string LabelKey, Func<string, string>? Format)[] StatusFields =
    [
        ("type", "Table_Type", null), ("active", "StorageSummary_Active", TableFormats.Flag),
        ("enabled", "Table_Enabled", TableFormats.Flag), ("shared", "Table_Shared", TableFormats.Flag),
        ("content", "Table_Content", null), ("total", "StorageSummary_Total", TableFormats.Bytes),
        ("used", "NodeDisks_Used", TableFormats.Bytes), ("avail", "NodeDisks_Free", TableFormats.Bytes)
    ];

    /// <summary>
    ///     탭 목록 — 저장소 창과 검증용 미리보기가 같이 쓴다. <paramref name="content" /> 에 없는 콘텐츠의 탭은 빠진다.
    ///     <paramref name="pluginType" /> 은 저장소 유형(pbs 면 백업 속 파일 복원) — 모르면 null(버튼을 두고 눌렀을 때 판단).
    /// </summary>
    public static List<NavTabSpec> Tabs(ProxmoxApiClient api, string node, string storage, string content,
        PermissionsInfo permissions, string? pluginType)
    {
        var kinds = content.Split(',', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var canTemplate = permissions.Has("Datastore.AllocateTemplate");
        var canDelete = permissions.Has("Datastore.Allocate") || canTemplate;
        var tabs = new List<NavTabSpec>();

        void Add(string id, string labelKey, string iconKey, bool visible, Func<ViewSpec> create)
        {
            if (visible) tabs.Add(new NavTabSpec(id, labelKey, iconKey, create));
        }

        Add("summary", "GuestTab_Summary", "IconList", true, () => Summary(api, node, storage));
        Add("graph", "StorageGraph_Tab", "IconSwitch", true, () => AppParts.Current.StorageGraph(api, node, storage));
        Add("backup", "GuestTab_Backup", "IconArchive", kinds.Contains("backup"),
            () => Content(api, node, storage, "backup", BackupColumns, "StorageContent_BackupHint",
                canDelete
                    ? [BackupConfigAction(api, node), ..RestoreActions(api, node, storage, pluginType),
                        ..DeleteActions(api, node, storage), PruneAction.Create(api, node, storage)]
                    : [BackupConfigAction(api, node), ..RestoreActions(api, node, storage, pluginType)]));
        Add("iso", "StorageContent_Iso", "IconDownload", kinds.Contains("iso"),
            () => Content(api, node, storage, "iso", FileColumns, "StorageContent_IsoHint",
                FileActions(api, node, storage, "iso", canTemplate, canDelete)));
        Add("vztmpl", "StorageContent_Templates", "IconBox", kinds.Contains("vztmpl"),
            () => Content(api, node, storage, "vztmpl", FileColumns, "StorageContent_TemplatesHint",
                FileActions(api, node, storage, "vztmpl", canTemplate, canDelete)));
        Add("import", "StorageContent_ImportTab", "IconDownload", kinds.Contains("import"),
            () => Content(api, node, storage, "import", FileColumns, "StorageContent_ImportHint",
                [ImportGuestAction.Create(api, node, storage)]));
        Add("images", "StorageContent_Images", "IconDatabase", kinds.Contains("images") || kinds.Contains("rootdir"),
            () => Content(api, node, storage, null, DiskColumns, "StorageContent_ImagesHint",
                canDelete ? [AllocateDiskAction.Create(api, node, storage), ..DeleteActions(api, node, storage)]
                    : null));

        return tabs;
    }

    private static TableSpec Summary(ProxmoxApiClient api, string node, string storage)
    {
        return new TableSpec(async () =>
        {
            var status = await api.Storage.StatusAsync(node, storage);
            return StatusFields
                .Where(f => Value(status, f.Key).Length > 0)
                .Select(f => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["name"] = UiText.T(f.LabelKey),
                    ["value"] = f.Format is { } format ? format(Value(status, f.Key)) : Value(status, f.Key)
                })
                .ToList();
        }, SummaryColumns, "StorageSummary_Hint");
    }

    /// <summary>콘텐츠 목록. content 가 null 이면 디스크 이미지(images·rootdir) 전체.</summary>
    private static TableSpec Content(ProxmoxApiClient api, string node, string storage, string? content,
        IReadOnlyList<TableColumn> columns, string hintKey, IReadOnlyList<TableAction>? actions)
    {
        return new TableSpec(async () =>
        {
            if (content is not null) return await api.Storage.ContentAsync(node, storage, content);

            var images = await api.Storage.ContentAsync(node, storage, "images");
            var rootdirs = await api.Storage.ContentAsync(node, storage, "rootdir");
            return images.Concat(rootdirs).ToList();
        }, columns, hintKey, actions);
    }

    /// <summary>PBS 백업 속 파일 복원 — PBS 가 아닌 것이 확실한 저장소에는 두지 않는다.</summary>
    private static IEnumerable<TableAction> RestoreActions(ProxmoxApiClient api, string node, string storage,
        string? pluginType)
    {
        if (pluginType is null or "pbs") yield return AppParts.Current.FileRestore(api, node, storage);
    }

    private static TableAction BackupConfigAction(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "BackupList_ShowConfig", IconKey = "IconList", NeedsSelection = true,
            Run = async (row, owner) =>
            {
                var text = await api.Guests.BackupConfigAsync(node, row!["volid"]);
                return AppUi.ShowText(owner, UiText.T("BackupList_ConfigTitle", row["volid"]), text);
            }
        };
    }

    private static IReadOnlyList<TableAction> DeleteActions(ProxmoxApiClient api, string node, string storage)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => UiText.T("BackupList_DeleteConfirm", row!["volid"]),
                Run = async (row, _) => await RunTaskAsync(api,
                    api.Storage.DeleteVolumeAsync(node, storage, row!["volid"]), "StorageContent_Deleted")
            }
        ];
    }

    /// <summary>URL 에서 받기 — 파일 이름은 URL 에서 찾아 채울 수 있다(7.1+), 체크섬은 고르면 검증한다.</summary>
    private static TableAction DownloadUrlAction(ProxmoxApiClient api, string node, string storage, string content)
    {
        var fileName = UrlFileName(api, node);
        return new TableAction
        {
            LabelKey = "StorageDownload_Button", IconKey = "IconExternal",
            Run = (_, owner) => SubmitTaskAsync(api, owner, UiText.T("StorageDownload_Button"),
            [
                new FormField { Key = "url", LabelKey = "StorageDownload_Url", Required = true },
                new FormField
                {
                    Key = "filename", LabelKey = "StorageDownload_FileName", Required = true,
                    Suggest = fileName, Hint = fileName is null ? null : UiText.T("StorageDownload_FileNameHint")
                },
                new FormField
                {
                    Key = "checksum-algorithm", LabelKey = "StorageDownload_ChecksumAlgorithm",
                    Kind = FormFieldKind.Choice, Initial = "",
                    Choices =
                    [
                        ("", "StorageDownload_NoChecksum"), ("sha256", "SHA-256"), ("sha512", "SHA-512"),
                        ("md5", "MD5")
                    ]
                },
                new FormField { Key = "checksum", LabelKey = "StorageDownload_Checksum" }
            ], values =>
            {
                var form = NonEmpty(values);
                form["content"] = content;
                return api.Storage.DownloadUrlAsync(node, storage, form);
            }, "StorageDownload_Done",
                values => values["url"].StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || values["url"].StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : UiText.T("StorageDownload_UrlInvalid"))
        };
    }

    /// <summary>URL 의 파일 이름을 서버에 물어 채운다(7.1+ — 그 전 서버는 직접 입력).</summary>
    private static Func<IReadOnlyDictionary<string, string>, Task<IReadOnlyList<(string, string)>>>? UrlFileName(
        ProxmoxApiClient api, string node)
    {
        if (!api.Storage.Feature(nameof(Core.Api.Domains.StorageApi.QueryUrlMetadataAsync)).IsAvailable) return null;

        return async values =>
        {
            var url = values.TryGetValue("url", out var u) ? u.Trim() : string.Empty;
            if (url.Length == 0) throw new InvalidOperationException(UiText.T("StorageScan_NeedField",
                UiText.T("StorageDownload_Url")));

            var meta = await api.Storage.QueryUrlMetadataAsync(node, url);
            var name = Value(meta, "filename");
            var size = Value(meta, "size") is { Length: > 0 } raw ? TableFormats.Bytes(raw) : "";
            return name.Length == 0 ? [] : [(name, $"{size} {Value(meta, "mimetype")}".Trim())];
        };
    }

    /// <summary>ISO·CT 템플릿 — 올리기, URL 에서 받기, (템플릿은) 공식 템플릿 받기, 삭제.</summary>
    private static IReadOnlyList<TableAction>? FileActions(ProxmoxApiClient api, string node, string storage,
        string content, bool canTemplate, bool canDelete)
    {
        var actions = new List<TableAction>();
        if (canTemplate)
        {
            actions.Add(new TableAction
            {
                LabelKey = "StorageUpload_Button", IconKey = "IconDownload",
                Run = (_, owner) => AppParts.Current.UploadAsync(api, node, storage, content, owner)
            });
            actions.Add(DownloadUrlAction(api, node, storage, content));
            if (content == "vztmpl")
            {
                actions.Add(new TableAction
                {
                    LabelKey = "StorageTemplates_Button", IconKey = "IconBox",
                    Run = (_, owner) => DownloadTemplateAsync(api, node, storage, owner)
                });
                actions.Add(OciPullAction.Create(api, node, storage));
            }
        }

        if (canDelete) actions.AddRange(DeleteActions(api, node, storage));
        return actions.Count > 0 ? actions : null;
    }

    /// <summary>Proxmox 가 제공하는 CT 템플릿 목록(aplinfo)에서 골라 저장소로 받는다.</summary>
    private static async Task<string?> DownloadTemplateAsync(ProxmoxApiClient api, string node, string storage,
        object? owner)
    {
        var templates = (await api.Storage.TemplatesAsync(node))
            .OrderBy(t => Value(t, "section"), StringComparer.Ordinal)
            .ThenBy(t => Value(t, "template"), StringComparer.Ordinal)
            .Select(t => (Value(t, "template"), $"{Value(t, "template")}  {Value(t, "headline")}".Trim()))
            .ToList();

        return await SubmitTaskAsync(api, owner, UiText.T("StorageTemplates_Button"),
        [
            new FormField
            {
                Key = "template", LabelKey = "StorageTemplates_Template", Kind = FormFieldKind.Choice,
                Choices = templates, Required = true
            }
        ], values => api.Storage.DownloadTemplateAsync(node, storage, values["template"]), "StorageTemplates_Done");
    }
}
