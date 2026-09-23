using System.IO;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Storage;

/// <summary>
///     저장소 창 — 웹 UI 에서 노드 아래 저장소를 골랐을 때의 요약·백업·ISO·CT 템플릿·디스크 이미지 화면.
///     저장소가 담을 수 있는 콘텐츠 종류의 탭만 보인다.
/// </summary>
public static class StorageNavigator
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

    public static NavWindow Create(ProxmoxApiClient api, string node, string storage, string content,
        PermissionsInfo permissions)
    {
        var basePath = $"nodes/{Seg(node)}/storage/{Seg(storage)}";
        var kinds = content.Split(',', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var canTemplate = permissions.Has("Datastore.AllocateTemplate");
        var canDelete = permissions.Has("Datastore.Allocate") || canTemplate;
        var tabs = new List<NavTab>();

        void Add(string id, string labelKey, string iconKey, bool visible, Func<UIElement> create)
        {
            if (visible) tabs.Add(new NavTab { Id = id, LabelKey = labelKey, IconKey = iconKey, Create = create });
        }

        Add("summary", "GuestTab_Summary", "IconList", true, () => Summary(api, basePath));
        Add("backup", "GuestTab_Backup", "IconArchive", kinds.Contains("backup"),
            () => Content(api, basePath, "backup", BackupColumns, "StorageContent_BackupHint",
                canDelete
                    ? [BackupConfigAction(api, node), ..DeleteActions(api, basePath)]
                    : [BackupConfigAction(api, node)]));
        Add("iso", "StorageContent_Iso", "IconDownload", kinds.Contains("iso"),
            () => Content(api, basePath, "iso", FileColumns, "StorageContent_IsoHint",
                FileActions(api, node, storage, basePath, "iso", canTemplate, canDelete)));
        Add("vztmpl", "StorageContent_Templates", "IconBox", kinds.Contains("vztmpl"),
            () => Content(api, basePath, "vztmpl", FileColumns, "StorageContent_TemplatesHint",
                FileActions(api, node, storage, basePath, "vztmpl", canTemplate, canDelete)));
        Add("images", "StorageContent_Images", "IconDatabase", kinds.Contains("images") || kinds.Contains("rootdir"),
            () => Content(api, basePath, null, DiskColumns, "StorageContent_ImagesHint",
                canDelete ? DeleteActions(api, basePath) : null));

        return new NavWindow(Loc.T("StorageWindow_Title", storage, node), Loc.T("StorageWindow_Title", storage, node),
            "IconDatabase", tabs, null);
    }

    private static TableTab Summary(ProxmoxApiClient api, string basePath)
    {
        return new TableTab(async () =>
        {
            var status = await api.GetObjectAsync($"{basePath}/status");
            return StatusFields
                .Where(f => Value(status, f.Key).Length > 0)
                .Select(f => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["name"] = Loc.T(f.LabelKey),
                    ["value"] = f.Format is { } format ? format(Value(status, f.Key)) : Value(status, f.Key)
                })
                .ToList();
        }, SummaryColumns, "StorageSummary_Hint");
    }

    /// <summary>콘텐츠 목록. content 가 null 이면 디스크 이미지(images·rootdir) 전체.</summary>
    private static TableTab Content(ProxmoxApiClient api, string basePath, string? content,
        IReadOnlyList<TableColumn> columns, string hintKey, IReadOnlyList<TableAction>? actions)
    {
        return new TableTab(async () =>
        {
            if (content is not null) return await api.GetTableAsync($"{basePath}/content?content={content}");

            var images = await api.GetTableAsync($"{basePath}/content?content=images");
            var rootdirs = await api.GetTableAsync($"{basePath}/content?content=rootdir");
            return images.Concat(rootdirs).ToList();
        }, columns, hintKey, actions);
    }

    private static TableAction BackupConfigAction(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "BackupList_ShowConfig", IconKey = "IconList", NeedsSelection = true,
            Run = async (row, owner) =>
            {
                var text = await api.GetTextAsync(
                    $"nodes/{Seg(node)}/vzdump/extractconfig?volume={Uri.EscapeDataString(row!["volid"])}");
                return TextViewWindow.ShowModal(owner, Loc.T("BackupList_ConfigTitle", row["volid"]), text);
            }
        };
    }

    private static IReadOnlyList<TableAction> DeleteActions(ProxmoxApiClient api, string basePath)
    {
        return
        [
            new TableAction
            {
                LabelKey = "Action_Delete", IconKey = "IconTrash", NeedsSelection = true,
                Confirm = row => Loc.T("BackupList_DeleteConfirm", row!["volid"]),
                Run = async (row, _) => await RunTaskAsync(api,
                    api.DeleteActionAsync($"{basePath}/content/{Seg(row!["volid"])}"), "StorageContent_Deleted")
            }
        ];
    }

    /// <summary>ISO·CT 템플릿 — 올리기, URL 에서 받기, (템플릿은) 공식 템플릿 받기, 삭제.</summary>
    private static IReadOnlyList<TableAction>? FileActions(ProxmoxApiClient api, string node, string storage,
        string basePath, string content, bool canTemplate, bool canDelete)
    {
        var actions = new List<TableAction>();
        if (canTemplate)
        {
            actions.Add(new TableAction
            {
                LabelKey = "StorageUpload_Button", IconKey = "IconDownload",
                Run = (_, owner) => UploadAsync(api, node, storage, content, owner)
            });
            actions.Add(new TableAction
            {
                LabelKey = "StorageDownload_Button", IconKey = "IconExternal",
                Run = (_, owner) => SubmitTaskAsync(api, owner, Loc.T("StorageDownload_Button"),
                [
                    new FormField { Key = "url", LabelKey = "StorageDownload_Url", Required = true },
                    new FormField { Key = "filename", LabelKey = "StorageDownload_FileName", Required = true },
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
                    return api.PostActionAsync($"{basePath}/download-url", form);
                }, "StorageDownload_Done",
                    values => values["url"].StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                              || values["url"].StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : Loc.T("StorageDownload_UrlInvalid"))
            });
            if (content == "vztmpl")
                actions.Add(new TableAction
                {
                    LabelKey = "StorageTemplates_Button", IconKey = "IconBox",
                    Run = (_, owner) => DownloadTemplateAsync(api, node, storage, owner)
                });
        }

        if (canDelete) actions.AddRange(DeleteActions(api, basePath));
        return actions.Count > 0 ? actions : null;
    }

    private static async Task<string?> UploadAsync(ProxmoxApiClient api, string node, string storage, string content,
        Window? owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.T(content == "iso" ? "StorageUpload_IsoFilter" : "StorageUpload_TemplateFilter")
        };
        if (dialog.ShowDialog(owner) != true) return null;

        var progress = new UploadProgressWindow(Path.GetFileName(dialog.FileName)) { Owner = owner };
        progress.Show();
        try
        {
            var upid = await api.UploadToStorageAsync(node, storage, content, dialog.FileName, progress.Progress,
                progress.Token);
            progress.Close();
            var status = upid.Length == 0 ? "OK" : (await api.WaitTaskAsync(upid)).Status;
            return Loc.T("StorageUpload_Done", status);
        }
        catch (OperationCanceledException)
        {
            return Loc.T("StorageUpload_Cancelled");
        }
        finally
        {
            if (progress.IsVisible) progress.Close();
        }
    }

    /// <summary>Proxmox 가 제공하는 CT 템플릿 목록(aplinfo)에서 골라 저장소로 받는다.</summary>
    private static async Task<string?> DownloadTemplateAsync(ProxmoxApiClient api, string node, string storage,
        Window? owner)
    {
        var templates = (await api.GetTableAsync($"nodes/{Seg(node)}/aplinfo"))
            .OrderBy(t => Value(t, "section"), StringComparer.Ordinal)
            .ThenBy(t => Value(t, "template"), StringComparer.Ordinal)
            .Select(t => (Value(t, "template"), $"{Value(t, "template")}  {Value(t, "headline")}".Trim()))
            .ToList();

        return await SubmitTaskAsync(api, owner, Loc.T("StorageTemplates_Button"),
        [
            new FormField
            {
                Key = "template", LabelKey = "StorageTemplates_Template", Kind = FormFieldKind.Choice,
                Choices = templates, Required = true
            }
        ], values => api.PostActionAsync($"nodes/{Seg(node)}/aplinfo", new Dictionary<string, string>
        {
            ["storage"] = storage, ["template"] = values["template"]
        }), "StorageTemplates_Done");
    }
}
