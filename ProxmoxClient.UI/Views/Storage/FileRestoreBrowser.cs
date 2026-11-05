using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Storage;

/// <summary>
///     PBS 백업 속 파일 복원(웹 UI 의 File Restore) — 백업 안을 폴더처럼 둘러보고(두 번 눌러 들어가기·위로),
///     고른 파일을 받거나 폴더를 zip 으로 받는다. 경로는 서버가 준 base64 값을 그대로 쓴다.
/// </summary>
internal static class FileRestoreBrowser
{
    /// <summary>맨 위 경로 — 서버 규칙상 base64 가 아닌 "/" 그대로.</summary>
    private const string Root = "/";

    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "text", HeaderKey = "Table_Name", Width = 0 },
        new() { Key = "kind", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "size", HeaderKey = "Table_Size", Width = 100, Format = TableFormats.Bytes },
        new() { Key = "mtime", HeaderKey = "FileRestore_Modified", Width = 150, Format = TableFormats.EpochDate }
    ];

    public static TableAction Create(ProxmoxApiClient api, string node, string storage)
    {
        return new TableAction
        {
            LabelKey = "FileRestore_Action", IconKey = "IconFolder", NeedsSelection = true,
            Run = (row, owner) =>
            {
                var volume = row!["volid"];
                // PBS 가 아닌 백업(vzdump-… 파일)은 서버가 거절한다 — 먼저 알려 준다
                if (volume.Contains("/vzdump-", StringComparison.Ordinal))
                    return Task.FromResult<string?>(Loc.T("FileRestore_PbsOnly"));

                return Task.FromResult(TableWindow.ShowModal(owner, Loc.T("FileRestore_Title", volume),
                    Browser(api, node, storage, volume)));
            }
        };
    }

    private static TableTab Browser(ProxmoxApiClient api, string node, string storage, string volume)
    {
        var trail = new Stack<string>(); // 지나온 경로(위로 가기용)
        var current = Root;
        string? next = null; // 들어가려는 폴더 — 목록을 읽은 뒤에야 current 로 확정한다
        var goingUp = false;
        TableTab? tab = null;

        async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> LoadAsync()
        {
            try
            {
                var rows = await api.Storage.FileRestoreListAsync(node, storage, volume, next ?? current);
                if (goingUp) trail.Pop();
                else if (next is not null) trail.Push(current);
                current = next ?? current;
                return rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(r)
                    {
                        ["kind"] = KindLabel(Value(r, "type"))
                    })
                    .ToList();
            }
            finally
            {
                next = null; // 실패하면 제자리(보이는 목록과 경로가 어긋나지 않게)
                goingUp = false;
            }
        }

        // 두 번 누르면 들어간다 — WPF 는 열고 나서 목록을 다시 읽었으므로 여기서도 다시 읽는다
        void Open(IReadOnlyDictionary<string, string> row)
        {
            if (IsLeaf(row)) return; // 파일은 들어갈 수 없다 — 받기 버튼으로
            next = Value(row, "filepath");
            _ = tab?.ReloadAsync();
        }

        tab = new TableTab(LoadAsync, Columns, "FileRestore_Hint",
        [
            new TableAction
            {
                LabelKey = "FileRestore_Up", IconKey = "IconArrowUp",
                Run = (_, _) =>
                {
                    if (trail.Count == 0) return Task.FromResult<string?>(Loc.T("FileRestore_AtTop"));
                    next = trail.Peek();
                    goingUp = true;
                    return Task.FromResult<string?>(Loc.T("FileRestore_Location", Display(next)));
                }
            },
            new TableAction
            {
                LabelKey = "FileRestore_Download", IconKey = "IconDownload", NeedsSelection = true,
                Run = (row, owner) => DownloadAsync(api, node, storage, volume, row!, owner)
            }
        ], open: (row, _) => Open(row));
        return tab;
    }

    /// <summary>파일은 그대로, 폴더(또는 디스크·파티션)는 zip 으로 받는다 — 저장할 곳은 사용자가 고른다.</summary>
    private static async Task<string?> DownloadAsync(ProxmoxApiClient api, string node, string storage,
        string volume, IReadOnlyDictionary<string, string> row, Control? owner)
    {
        var isFile = IsLeaf(row);
        var name = Value(row, "text").Trim('/');
        if (name.Length == 0) name = "restore";
        var suggested = StorageFiles.SafeFileName(isFile ? name : name + ".zip");
        var target = await StorageFiles.PickSaveAsync(owner, suggested, zip: !isFile);
        if (target is null) return null;

        // 먼저 앱 임시 폴더에 다 받고, 다 받은 뒤에만 고른 곳에 쓴다 — 받다 실패하면 기존 파일은 그대로 남는다
        using var temp = new TempFolder();
        var partial = Path.Combine(temp.Folder, suggested);
        await using (var file = File.Create(partial))
            await api.Storage.FileRestoreDownloadAsync(node, storage, volume, Value(row, "filepath"), false, file);

        if (target.TryGetLocalPath() is { } localPath) File.Copy(partial, localPath, overwrite: true);
        else
        {
            await using var input = File.OpenRead(partial);
            await using var output = await target.OpenWriteAsync();
            await input.CopyToAsync(output);
        }

        var size = new FileInfo(partial).Length;
        return Loc.T("FileRestore_Saved", target.TryGetLocalPath() ?? target.Name,
            TableFormats.Bytes(size.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>base64 경로를 사람이 읽는 경로로(읽지 못하면 그대로).</summary>
    private static string Display(string path)
    {
        if (path == Root) return Root;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(path));
        }
        catch (FormatException)
        {
            return path;
        }
    }

    private static bool IsLeaf(IReadOnlyDictionary<string, string> row)
    {
        return Value(row, "leaf") is "1" or "true" || Value(row, "type") == "f";
    }

    private static string KindLabel(string type)
    {
        return type switch
        {
            "d" => Loc.T("FileRestore_Dir"),
            "f" => Loc.T("FileRestore_File"),
            "l" or "h" => Loc.T("FileRestore_Link"),
            "v" => Loc.T("FileRestore_Volume"),
            _ => type
        };
    }

    private static string Value(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var v) ? v : string.Empty;
    }
}
