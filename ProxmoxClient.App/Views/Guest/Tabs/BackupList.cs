using System.Globalization;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>
///     이 게스트의 백업 파일 목록 — 백업을 담는 저장소를 모두 훑어 모은다(웹 UI 게스트 › 백업 화면).
///     같은 번호로 복원하면 지금 게스트를 덮어쓰므로 번호를 직접 입력해야 한다.
/// </summary>
internal static class BackupList
{
    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "ctime", HeaderKey = "BackupList_Created", Width = 140, Format = TableFormats.EpochDate },
        new() { Key = "storage", HeaderKey = "Table_Storage", Width = 110 },
        new() { Key = "volid", HeaderKey = "BackupList_File", Width = 0 },
        new() { Key = "size", HeaderKey = "Table_Size", Width = 90, Format = TableFormats.Bytes },
        new() { Key = "protected", HeaderKey = "BackupList_Protected", Width = 60, Format = TableFormats.Flag },
        new() { Key = "notes", HeaderKey = "Table_Comment", Width = 160 }
    ];

    public static TableTab Create(ProxmoxApiClient api, PveResource guest, bool canRestore)
    {
        var actions = new List<TableAction>
        {
            new()
            {
                LabelKey = "BackupList_ShowConfig", IconKey = "IconList", NeedsSelection = true,
                Run = async (row, owner) =>
                {
                    var text = await api.GetTextAsync(
                        $"nodes/{Seg(guest.Node)}/vzdump/extractconfig?volume={Uri.EscapeDataString(row!["volid"])}");
                    return TextViewWindow.ShowModal(owner, Loc.T("BackupList_ConfigTitle", row["volid"]), text);
                }
            },
            new()
            {
                LabelKey = "BackupList_Notes", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => SubmitAsync(owner, Loc.T("BackupList_NotesTitle", row!["volid"]),
                [
                    new FormField { Key = "notes", LabelKey = "Table_Comment", Initial = Value(row, "notes") },
                    new FormField
                    {
                        Key = "protected", LabelKey = "BackupList_Protected", Kind = FormFieldKind.Bool,
                        Initial = Value(row, "protected") is "1" or "true" ? "1" : "0"
                    }
                ], values => api.PutActionAsync(ContentPath(guest, row), values), "BackupList_Saved",
                    titleIsKey: false)
            },
            DeleteAction(row => Loc.T("BackupList_DeleteConfirm", row["volid"]),
                row => api.DeleteActionAsync(ContentPath(guest, row)), "BackupList_Deleted")
        };

        if (canRestore)
            actions.Insert(0, new TableAction
            {
                LabelKey = "BackupList_Restore", IconKey = "IconUndo", NeedsSelection = true,
                Run = (row, owner) => RestoreAsync(api, guest, row!, owner)
            });

        return new TableTab(() => LoadAsync(api, guest), Columns, "BackupList_Hint", actions);
    }

    /// <summary>이 노드에서 쓸 수 있는 백업 저장소마다 이 게스트의 백업을 읽어 최신순으로 합친다.</summary>
    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> LoadAsync(ProxmoxApiClient api,
        PveResource guest)
    {
        var node = Seg(guest.Node);
        var storages = await api.GetTableAsync($"nodes/{node}/storage?content=backup&enabled=1");
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var storage in storages.Select(s => Value(s, "storage")).Where(s => s.Length > 0))
        {
            var files = await api.GetTableAsync(
                $"nodes/{node}/storage/{Seg(storage)}/content?content=backup&vmid={guest.VmId}");
            rows.AddRange(files.Select(f =>
                (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(f, StringComparer.Ordinal)
                {
                    ["storage"] = storage
                }));
        }

        return rows
            .OrderByDescending(r => long.TryParse(Value(r, "ctime"), out var t) ? t : 0)
            .ToList();
    }

    /// <summary>
    ///     복원 — 같은 번호면 지금 게스트를 덮어쓰고(번호 입력 확인), 다른 번호면 새 게스트로 만든다.
    /// </summary>
    private static async Task<string?> RestoreAsync(ProxmoxApiClient api, PveResource guest,
        IReadOnlyDictionary<string, string> row, Window? owner)
    {
        var isVm = guest.Kind == ResourceKind.Qemu;
        var current = guest.VmId.ToString(CultureInfo.InvariantCulture);
        var content = isVm ? "images" : "rootdir";
        var storages = (await api.GetTableAsync($"nodes/{Seg(guest.Node)}/storage?content={content}&enabled=1"))
            .Select(s => (Value(s, "storage"), Value(s, "storage")))
            .Prepend((string.Empty, "BackupList_OriginalStorage"))
            .ToList();

        return await SubmitTaskAsync(api, owner, Loc.T("BackupList_RestoreTitle", row["volid"]),
        [
            new FormField { Key = "vmid", LabelKey = "BackupList_TargetId", Required = true, Initial = current },
            new FormField
            {
                Key = "storage", LabelKey = "Table_Storage", Kind = FormFieldKind.Choice, Choices = storages
            },
            new FormField
            {
                Key = "unique", LabelKey = "BackupList_Unique", Kind = FormFieldKind.Bool, Initial = "0"
            },
            new FormField { Key = "start", LabelKey = "BackupList_StartAfter", Kind = FormFieldKind.Bool },
            new FormField { Key = "confirm", LabelKey = "BackupList_OverwriteConfirm" }
        ], values =>
        {
            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vmid"] = values["vmid"],
                [isVm ? "archive" : "ostemplate"] = row["volid"],
                ["unique"] = values["unique"],
                ["start"] = values["start"]
            };
            if (!isVm) form["restore"] = "1";
            if (values["storage"].Length > 0) form["storage"] = values["storage"];
            if (values["vmid"] == current) form["force"] = "1";
            return api.PostActionAsync($"nodes/{Seg(guest.Node)}/{guest.Kind.ApiSegment()}", form);
        }, "BackupList_Restored",
            values => values["vmid"] != current || values["confirm"] == current
                ? null
                : Loc.T("BackupList_OverwriteMismatch", current));
    }

    private static string ContentPath(PveResource guest, IReadOnlyDictionary<string, string> row)
    {
        return $"nodes/{Seg(guest.Node)}/storage/{Seg(row["storage"])}/content/{Seg(row["volid"])}";
    }
}
