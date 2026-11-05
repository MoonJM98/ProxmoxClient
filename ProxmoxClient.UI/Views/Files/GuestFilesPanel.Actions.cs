using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Files;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;
using ProxmoxClient.UI.Views.Storage;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>
///     파일 패널 작업 — 내려받기·올리기(고르기 또는 끌어다 놓기)·새 폴더·이름 바꾸기·지우기·경로 복사.
///     받기는 게스트에서 앱 임시 폴더로 먼저 받은 뒤 고른 위치로 옮긴다(안드로이드 저장소 URI 는 경로가 없어 스트림으로 쓴다).
/// </summary>
public partial class GuestFilesPanel
{
    /// <summary>받을 항목 하나 — 게스트 항목과, 받은 임시 파일을 고른 곳에 쓰는 동작.</summary>
    private sealed record DownloadTarget(GuestFileEntry Entry, Func<string, Task> Save);

    /// <summary>골라 올린 파일의 로컬 복사본들 — 올리기가 끝날 때까지 두었다가 치운다.</summary>
    private sealed class LocalCopies : IAsyncDisposable
    {
        private readonly List<LocalFileCopy> _copies = [];

        public async Task<string> AddAsync(IStorageFile file)
        {
            var copy = await LocalFileCopy.ForAsync(file);
            _copies.Add(copy);
            return copy.Path;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var copy in _copies) await copy.DisposeAsync();
        }
    }

    private async Task DownloadSelectedAsync()
    {
        if (_files is null || SelectedRows is not { Count: > 0 } rows) return;

        await DownloadAsync(rows);
    }

    private async Task OpenRowAsync(GuestFileRow row)
    {
        if (_files is not { } files) return;

        if (row.Entry.IsDirectory)
        {
            await NavigateAsync(files.Combine(_current, row.Entry.Name));
            return;
        }

        if (row.Entry.LinkToDirectory)
        {
            // 탐색기처럼 링크 경로 그대로 들어간다(주소에 링크 이름이 보이고, 위로 가면 링크가 있던 폴더)
            await NavigateAsync(files.Combine(_current, row.Entry.Name));
            return;
        }

        if (row.Entry.Kind == GuestFileKind.Link)
        {
            var target = files.ResolveLink(_current, row.Entry);
            await RunAsync(Loc.T("GuestFiles_Loading"), async ct =>
            {
                try
                {
                    await ListAsync(target, ct);
                }
                catch (GuestFileException)
                {
                    _status.Text = Loc.T("GuestFiles_LinkIsFile", target); // 폴더가 아닌 링크 — 받기로
                }
            }, confirmed: _readsApproved);
            if (_current == target) return;
        }

        await DownloadAsync([row]);
    }

    /// <summary>하나면 저장 위치를 묻고, 여럿이면 폴더를 골라 그 안에 받는다. 폴더는 tar 로 받는다.</summary>
    private async Task DownloadAsync(IReadOnlyList<GuestFileRow> rows)
    {
        var files = _files;
        if (files is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider) return;

        var directory = _current; // 작업 중에 폴더가 바뀌어도 고른 곳에서 받는다
        var targets = new List<DownloadTarget>();
        var confirmed = false;
        if (rows.Count == 1)
        {
            var entry = rows[0].Entry;
            if (await StorageFiles.PickSaveAsync(this, LocalName(entry), zip: entry.OpensAsFolder) is not { } file) return;

            targets.Add(new DownloadTarget(entry, temp => SaveToFileAsync(temp, file)));
        }
        else
        {
            var picked = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Loc.T("GuestFiles_DownloadTo"), AllowMultiple = false
            });
            if (picked.Count == 0) return;

            var folder = picked[0];
            // PC 에 같은 이름이 있거나(대소문자·바뀐 글자로) 서로 겹치면 한 번 묻는다
            var existing = await NamesInAsync(folder);
            var names = rows.Select(r => LocalName(r.Entry)).ToList();
            var clashes = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1 || existing.Contains(g.Key, StringComparer.OrdinalIgnoreCase))
                .Select(g => g.Key).ToList();
            if (clashes.Count > 0)
            {
                if (!await Ask.ConfirmAsync(Loc.T("GuestFiles_LocalOverwrite", string.Join(", ", clashes.Take(5)),
                        clashes.Count), FlowName, destructive: true))
                    return;
                confirmed = true;
            }

            for (var i = 0; i < rows.Count; i++)
            {
                var name = names[i];
                targets.Add(new DownloadTarget(rows[i].Entry, temp => SaveToFolderAsync(temp, folder, name)));
            }
        }

        await RunAsync(Loc.T("GuestFiles_Downloading"), async ct =>
        {
            foreach (var target in targets)
            {
                var entry = target.Entry;
                if (entry.OpensAsFolder && !files.CanDownloadDirectory)
                    throw new GuestFileException(Loc.T("GuestFiles_NoFolderDownload", entry.Name));

                using var temp = new TempFolder();
                var tempFile = Path.Combine(temp.Folder, LocalName(entry));
                var (from, item) = entry.LinkToDirectory ? LinkedFolder(files, directory, entry) : (directory, entry);
                await DownloadOneAsync(files, from, item, tempFile, ct);
                await target.Save(tempFile);
            }

            _status.Text = Loc.T("GuestFiles_Downloaded", targets.Count);
        }, transfer: true, confirmed: confirmed || _readsApproved);
    }

    /// <summary>임시 파일로 받는다 — 실패·취소 때 반쪽 파일이 남지 않게(임시 폴더째로 지운다).</summary>
    private async Task DownloadOneAsync(IGuestFileSystem files, string directory, GuestFileEntry entry, string path,
        CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var progress = TransferProgress(entry.Name, entry.IsDirectory ? 0 : entry.Size);
        await files.DownloadAsync(directory, entry, stream, progress, ct);
    }

    /// <summary>받은 임시 파일을 고른 파일 위치에 쓴다 — 로컬 경로가 있으면 그대로 덮고, 아니면 스트림으로.</summary>
    private static async Task SaveToFileAsync(string temp, IStorageFile target)
    {
        if (target.TryGetLocalPath() is { } local)
        {
            File.Copy(temp, local, overwrite: true);
            return;
        }

        await using var output = await target.OpenWriteAsync();
        await using var input = File.OpenRead(temp);
        await input.CopyToAsync(output);
    }

    private static async Task SaveToFolderAsync(string temp, IStorageFolder folder, string name)
    {
        if (folder.TryGetLocalPath() is { } local)
        {
            File.Copy(temp, Path.Combine(local, name), overwrite: true);
            return;
        }

        await SaveToFileAsync(temp, await folder.CreateFileAsync(name) ?? throw new IOException(name));
    }

    private static async Task<List<string>> NamesInAsync(IStorageFolder folder)
    {
        var names = new List<string>();
        await foreach (var item in folder.GetItemsAsync()) names.Add(item.Name);
        return names;
    }

    /// <summary>
    ///     폴더 링크를 받을 때 — tar 는 링크 경로를 링크 하나로 묶으므로 가리키는 폴더(담긴 폴더, 폴더 항목)로
    ///     바꾼다. 대상을 모르거나 뿌리면 링크 경로 그대로.
    /// </summary>
    private static (string Directory, GuestFileEntry Entry) LinkedFolder(IGuestFileSystem files, string directory,
        GuestFileEntry link)
    {
        var target = string.IsNullOrEmpty(link.LinkTarget)
            ? files.Combine(directory, link.Name)
            : files.ResolveLink(directory, link);
        var name = CrumbLabel(files, target, false);
        var parent = files.Parent(target);
        return name.Length == 0 || files.NameComparer.Equals(parent, target)
            ? (directory, link with { Kind = GuestFileKind.Directory })
            : (parent, link with { Name = name, Kind = GuestFileKind.Directory, LinkToDirectory = false });
    }

    /// <summary>PC 에 저장할 이름 — 폴더(폴더 링크 포함)는 .tar, Windows 에서 못 쓰는 글자는 _ 로.</summary>
    private static string LocalName(GuestFileEntry entry)
    {
        var name = StorageFiles.SafeFileName(entry.Name);
        return entry.OpensAsFolder ? name + ".tar" : name;
    }

    /// <summary>[올리기] 단추 — 파일 여러 개를 고른다.</summary>
    private async Task UploadPickedAsync()
    {
        if (_files is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } provider) return;

        var picked = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true, Title = Loc.T("GuestFiles_Upload")
        });
        if (picked.Count > 0) await UploadItemsAsync(picked);
    }

    /// <summary>골라 둔 파일들을 지금 폴더로 올린다(로컬 복사본을 만든 뒤).</summary>
    private async Task UploadItemsAsync(IReadOnlyList<IStorageFile> items)
    {
        if (_files is null) return;
        if (_busy is not null)
        {
            _status.Text = Loc.T("GuestFiles_Busy"); // 놓은 파일을 조용히 버리지 않고 알린다
            return;
        }

        await using var copies = new LocalCopies();
        var paths = new List<string>();
        foreach (var file in items) paths.Add(await copies.AddAsync(file));
        await UploadAsync(paths);
    }

    /// <summary>
    ///     파일들을 지금 폴더로 올린다 — 폴더는 건너뛰고(알림), 같은 이름이 있으면 덮어쓸지 한 번 묻는다.
    ///     끝나면 목록을 다시 읽는다.
    /// </summary>
    private async Task UploadAsync(IReadOnlyList<string> paths)
    {
        var files = _files;
        if (files is null) return;
        if (_busy is not null)
        {
            _status.Text = Loc.T("GuestFiles_Busy");
            return;
        }

        var sources = paths.Where(File.Exists).ToList();
        var skipped = paths.Count - sources.Count;
        var directory = _current;
        // 같은 이름 확인은 다 읽은 목록으로 — 캐시·빈 목록·받는 도중의 목록이면 빠진 파일을 묻지 않고 덮어쓰게 된다
        var known = _lastEntries ?? [];
        if (!_entriesComplete)
        {
            _status.Text = Loc.T("GuestFiles_Loading");
            try
            {
                using var readFlow = WriteConfirmation.Begin(FlowName, _readsApproved);
                known = await files.RefreshAsync(directory, _loadCancel.Token);
            }
            catch (Exception ex) when (IsExpected(ex) || ex is OperationCanceledException)
            {
                _status.Foreground = Brush("BrushWarn");
                _status.Text = ex is OperationCanceledException
                    ? Loc.T("GuestFiles_Cancelled")
                    : Loc.T("GuestFiles_Error", ex.Message);
                return;
            }

            if (directory == _current)
            {
                ++_listing;
                ShowEntries(directory, known);
                _entriesComplete = true;
            }
        }

        // 숨겨 둔 항목까지(보기에서 뺐어도 덮어쓰게 된다), 게스트 규칙대로(Windows 는 README.md = readme.md)
        var existing = known.Select(e => e.Name).ToHashSet(files.NameComparer);
        var clashes = sources.Select(Path.GetFileName).Where(n => existing.Contains(n!)).ToList();
        // 쓰기는 항상 묻는다 — 같은 이름이 있으면 덮어쓸지, 아니면 올릴지를 확인한다
        if (clashes.Count > 0)
        {
            if (!await Ask.ConfirmAsync(Loc.T("GuestFiles_OverwriteConfirm", string.Join(", ", clashes.Take(5)),
                    clashes.Count), FlowName, destructive: true))
                return;
        }
        else if (!await Ask.ConfirmAsync(Loc.T("GuestFiles_UploadConfirm", sources.Count, directory), FlowName))
            return;

        await RunAsync(Loc.T("GuestFiles_Uploading"), async ct =>
        {
            foreach (var path in sources)
            {
                var name = Path.GetFileName(path);
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                await files.UploadAsync(directory, name, stream, stream.Length,
                    TransferProgress(name, stream.Length), ct);
            }

            await ListAsync(directory, ct);
            _status.Text = skipped > 0
                ? Loc.T("GuestFiles_UploadedSkipped", sources.Count, skipped)
                : Loc.T("GuestFiles_Uploaded", sources.Count);
        }, transfer: true, confirmed: true);
    }

    private async Task NewFolderAsync()
    {
        if (_files is not { } files || await AskNameAsync(Loc.T("GuestFiles_NewFolder"), string.Empty) is not { } name) return;

        var directory = _current;
        if (!await Ask.ConfirmAsync(Loc.T("GuestFiles_NewFolderConfirm", name, directory), FlowName)) return;

        await RunAsync(Loc.T("GuestFiles_Working"), async ct =>
        {
            await files.CreateDirectoryAsync(directory, name, ct);
            await ListAsync(directory, ct);
        }, confirmed: true);
    }

    private async Task RenameSelectedAsync()
    {
        if (_files is not { } files || SelectedRow is not { } row) return;
        if (await AskNameAsync(Loc.T("GuestFiles_Rename"), row.Entry.Name) is not { } name || name == row.Entry.Name) return;
        if (!await Ask.ConfirmAsync(Loc.T("GuestFiles_RenameConfirm", row.Entry.Name, name), FlowName)) return;

        var directory = _current;
        await RunAsync(Loc.T("GuestFiles_Working"), async ct =>
        {
            await files.RenameAsync(directory, row.Entry.Name, name, ct);
            await ListAsync(directory, ct);
        }, confirmed: true);
    }

    private async Task DeleteSelectedAsync()
    {
        if (_files is not { } files || SelectedRows is not { Count: > 0 } rows) return;

        var names = string.Join(", ", rows.Take(5).Select(r => r.Entry.IsDirectory ? r.Name + "/" : r.Name));
        if (!await Ask.ConfirmAsync(Loc.T("GuestFiles_DeleteConfirm", names, rows.Count, _current), FlowName, destructive: true))
            return;

        // 지우는 동안 폴더가 바뀌어도 고른 폴더의 항목만 지운다
        var directory = _current;
        await RunAsync(Loc.T("GuestFiles_Working"), async ct =>
        {
            foreach (var row in rows) await files.DeleteAsync(directory, row.Entry, ct);
            await ListAsync(directory, ct);
        }, confirmed: true);
    }

    /// <summary>이름 입력(새 폴더·이름 바꾸기) — 취소하면 null.</summary>
    private static async Task<string?> AskNameAsync(string title, string initial)
    {
        var result = await FormDialog.ShowAsync(title,
        [
            new FormField { Key = "name", LabelKey = "GuestFiles_Name", Required = true, Trim = false, Initial = initial }
        ], values => values["name"].Contains('/') || values["name"] is "." or ".."
            ? Loc.T("GuestFiles_BadName")
            : null);
        return result?["name"];
    }

    /// <summary>경로 복사 — 고른 항목들의 게스트 경로(줄마다 하나), 없으면 지금 폴더.</summary>
    private async Task CopyPathAsync()
    {
        if (_files is not { } files) return;

        var rows = SelectedRows;
        var text = rows.Count == 0
            ? _current
            : string.Join(Environment.NewLine, rows.Select(r => files.Combine(_current, r.Entry.Name)));
        try
        {
            await ClipboardHelper.SetTextAsync(text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _status.Text = Loc.T("GuestFiles_Error", ex.Message);
        }
    }

    private async Task CopyCommandsAsync()
    {
        try
        {
            await ClipboardHelper.SetTextAsync(_guidanceCommands.Text?.Replace("\n", Environment.NewLine) ?? string.Empty);
            _status.Text = Loc.T("GuestFiles_Copied");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _status.Text = Loc.T("GuestFiles_Error", ex.Message);
        }
    }
}
