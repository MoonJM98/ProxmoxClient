using System.Globalization;
using Avalonia.Controls;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Views.Shared;
using static ProxmoxClient.UI.Views.Shared.ActionHelpers;

namespace ProxmoxClient.UI.Views.Guest.Tabs;

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
        new() { Key = "protected", HeaderKey = "BackupList_Protected", Width = 90, Format = TableFormats.Flag },
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
                    if (row is null) return null;

                    var text = await api.Guests.BackupConfigAsync(guest.Node, row["volid"]);
                    return TextViewWindow.ShowModal(owner, Loc.T("BackupList_ConfigTitle", row["volid"]), text);
                }
            },
            new()
            {
                LabelKey = "BackupList_Notes", IconKey = "IconPencil", NeedsSelection = true,
                Run = (row, owner) => row is null
                    ? Task.FromResult<string?>(null)
                    : EditNotesAsync(api, guest, row, owner)
            },
            DeleteAction(row => Loc.T("BackupList_DeleteConfirm", row["volid"]),
                row => api.Storage.DeleteVolumeAsync(guest.Node, row["storage"], row["volid"]), "BackupList_Deleted")
        };

        if (canRestore)
            actions.Insert(0, new TableAction
            {
                LabelKey = "BackupList_Restore", IconKey = "IconUndo", NeedsSelection = true,
                Run = (row, owner) => row is null
                    ? Task.FromResult<string?>(null)
                    : RestoreAsync(api, guest, row, owner)
            });

        return new TableTab(() => LoadAsync(api, guest), Columns, "BackupList_Hint", actions);
    }

    /// <summary>이 노드에서 쓸 수 있는 백업 저장소마다 이 게스트의 백업을 읽어 최신순으로 합친다.</summary>
    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> LoadAsync(ProxmoxApiClient api,
        PveResource guest)
    {
        var storages = await api.Storage.NodeStoragesAsync(guest.Node, "backup");
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var storage in storages.Select(s => Value(s, "storage")).Where(s => s.Length > 0))
        {
            var files = await api.Storage.ContentAsync(guest.Node, storage, "backup", guest.VmId);
            rows.AddRange(files.Select(f =>
                (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(f, StringComparer.Ordinal)
                {
                    ["storage"] = storage
                }));
        }

        return rows
            .OrderByDescending(r => long.TryParse(Value(r, "ctime"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 0)
            .ToList();
    }

    /// <summary>메모·보호 설정만 바꾼다(볼륨 내용은 그대로).</summary>
    private static Task<string?> EditNotesAsync(ProxmoxApiClient api, PveResource guest,
        IReadOnlyDictionary<string, string> row, Control? owner)
    {
        return SubmitAsync(owner, Loc.T("BackupList_NotesTitle", row["volid"]),
        [
            new FormField { Key = "notes", LabelKey = "Table_Comment", Initial = Value(row, "notes") },
            new FormField
            {
                Key = "protected", LabelKey = "BackupList_Protected", Kind = FormFieldKind.Bool,
                Initial = Value(row, "protected") is "1" or "true" ? "1" : "0"
            }
        ], values => api.Storage.UpdateVolumeAsync(guest.Node, row["storage"], row["volid"], values),
            "BackupList_Saved", titleIsKey: false);
    }

    /// <summary>
    ///     복원 — 같은 번호면 지금 게스트를 덮어쓰고(번호 입력 확인), 다른 번호면 새 게스트로 만든다.
    /// </summary>
    private static async Task<string?> RestoreAsync(ProxmoxApiClient api, PveResource guest,
        IReadOnlyDictionary<string, string> row, Control? owner)
    {
        var isVm = guest.Kind == ResourceKind.Qemu;
        var current = guest.VmId.ToString(CultureInfo.InvariantCulture);
        var content = isVm ? "images" : "rootdir";
        var storages = (await api.Storage.NodeStoragesAsync(guest.Node, content))
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
            ..RestoreOptionFields(api, isVm, row),
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
            AddRestoreOptions(form, values);
            return api.Guests.CreateAsync(guest.Node, guest.Kind, form);
        }, "BackupList_Restored",
            values => ValidateRestoreOptions(values)
                      ?? (values["vmid"] != current || values["confirm"] == current
                          ? null
                          : Loc.T("BackupList_OverwriteMismatch", current)));
    }

    /// <summary>
    ///     복원 추가 설정 — 대역폭 제한(bwlimit, KiB/s), VM 은 PBS 백업일 때 실시간 복원(live-restore: 복원하면서 바로 켠다),
    ///     CT 는 권한 없는(unprivileged) 컨테이너 여부를 백업과 다르게 정할 수 있다.
    /// </summary>
    private static IEnumerable<FormField> RestoreOptionFields(ProxmoxApiClient api, bool isVm,
        IReadOnlyDictionary<string, string> row)
    {
        yield return new FormField
        {
            Key = "bwlimit", LabelKey = "BackupList_BwLimit", Hint = Loc.T("BackupList_BwLimitHint"), Advanced = true
        };
        if (isVm)
        {
            // 실시간 복원은 Proxmox Backup Server 의 백업만 된다(형식 pbs-vm)
            if (Value(row, "format").StartsWith("pbs", StringComparison.Ordinal))
                yield return new FormField
                {
                    Key = "live-restore", LabelKey = "BackupList_LiveRestore", Kind = FormFieldKind.Bool,
                    Initial = "0", Hint = Loc.T("BackupList_LiveRestoreHint"),
                    Requires = api.Guests.Feature(nameof(Core.Api.Domains.GuestsApi.CreateAsync), "live-restore")
                };
        }
        else
        {
            yield return new FormField
            {
                Key = "unprivileged", LabelKey = "BackupList_Unprivileged", Kind = FormFieldKind.Choice,
                Choices = [("", "BackupList_FromBackup"), ("1", "BackupList_Unprivileged1"), ("0", "BackupList_Unprivileged0")], Advanced = true
            };
        }
    }

    internal static void AddRestoreOptions(Dictionary<string, string> form, IReadOnlyDictionary<string, string> values)
    {
        string V(string key) => values.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
        if (V("bwlimit").Length > 0) form["bwlimit"] = V("bwlimit");
        if (V("live-restore") == "1")
        {
            form["live-restore"] = "1";
            form.Remove("start"); // 실시간 복원은 서버가 바로 켠다 — start 는 필요 없다
        }

        if (V("unprivileged").Length > 0) form["unprivileged"] = V("unprivileged");
    }

    private static string? ValidateRestoreOptions(IReadOnlyDictionary<string, string> values)
    {
        var bw = values.TryGetValue("bwlimit", out var v) ? v.Trim() : string.Empty;
        return bw.Length == 0 || long.TryParse(bw, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? null
            : Loc.T("BackupList_BwLimitHint");
    }
}
