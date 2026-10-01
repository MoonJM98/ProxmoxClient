using System.IO;
using System.Windows;
using Microsoft.Win32;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Files;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>파일 패널 작업 — 내려받기·올리기(끌어다 놓기 포함)·새 폴더·이름 바꾸기·지우기.</summary>
public partial class GuestFilesPanel
{
    private Window? OwnerWindow => Window.GetWindow(this);

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (SelectedRows is { Count: > 0 } rows) await DownloadAsync(rows);
    }

    /// <summary>하나면 저장 위치를 묻고, 여럿이면 폴더를 골라 그 안에 받는다. 폴더는 tar 로 받는다.</summary>
    private async Task DownloadAsync(IReadOnlyList<GuestFileRow> rows)
    {
        var files = _files;
        if (files is null) return;

        var directory = _current; // 작업 중에 폴더가 바뀌어도 고른 곳에서 받는다
        var targets = new List<(GuestFileEntry Entry, string Path)>();
        if (rows.Count == 1)
        {
            var entry = rows[0].Entry;
            var dialog = new SaveFileDialog { FileName = LocalName(entry), OverwritePrompt = true };
            if (dialog.ShowDialog(OwnerWindow) != true) return;

            targets.Add((entry, dialog.FileName));
        }
        else
        {
            var dialog = new OpenFolderDialog { Title = Loc.T("GuestFiles_DownloadTo") };
            if (dialog.ShowDialog(OwnerWindow) != true) return;

            targets.AddRange(rows.Select(r => (r.Entry, Path.Combine(dialog.FolderName, LocalName(r.Entry)))));
            // PC 에 같은 이름이 있거나(대소문자·바뀐 글자로) 서로 겹치면 한 번 묻는다
            var clashes = targets.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1 || File.Exists(g.Key) || Directory.Exists(g.Key))
                .Select(g => Path.GetFileName(g.Key)).ToList();
            if (clashes.Count > 0 && !Confirm(Loc.T("GuestFiles_LocalOverwrite", string.Join(", ", clashes.Take(5)),
                    clashes.Count)))
                return;
        }

        await RunAsync(Loc.T("GuestFiles_Downloading"), async ct =>
        {
            foreach (var (entry, path) in targets)
            {
                if (entry.OpensAsFolder && !files.CanDownloadDirectory)
                    throw new GuestFileException(Loc.T("GuestFiles_NoFolderDownload", entry.Name));

                var (from, item) = entry.LinkToDirectory ? LinkedFolder(files, directory, entry) : (directory, entry);
                await DownloadOneAsync(files, from, item, path, ct);
            }

            StatusText.Text = Loc.T("GuestFiles_Downloaded", targets.Count);
        }, transfer: true);
    }

    /// <summary>임시 파일로 받은 뒤 끝나면 이름을 바꾼다 — 실패·취소 때 반쪽 파일이 남지 않게.</summary>
    private async Task DownloadOneAsync(IGuestFileSystem files, string directory, GuestFileEntry entry, string path,
        CancellationToken ct)
    {
        var partial = $"{path}.{Guid.NewGuid():N}.part"; // 원래 있던 .part 파일을 건드리지 않게
        try
        {
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var progress = TransferProgress(entry.Name, entry.IsDirectory ? 0 : entry.Size);
                await files.DownloadAsync(directory, entry, stream, progress, ct);
            }

            File.Move(partial, path, true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
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
        var name = string.Concat(entry.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return entry.OpensAsFolder ? name + ".tar" : name;
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = Loc.T("GuestFiles_Upload") };
        if (dialog.ShowDialog(OwnerWindow) == true) await UploadAsync(dialog.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var accepts = _files is not null && _busy is null && !IsDraggingOut
                      && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (IsDraggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (_files is null)
        {
            StatusText.Text = Loc.T("GuestFiles_NotReady");
            return;
        }

        e.Handled = true;
        await UploadAsync(paths);
    }

    /// <summary>
    ///     파일들을 지금 폴더로 올린다 — 폴더는 건너뛰고(알림), 같은 이름이 있으면 덮어쓸지 한 번 묻는다.
    ///     끝나면 목록을 다시 읽는다.
    /// </summary>
    public async Task UploadAsync(IReadOnlyList<string> paths)
    {
        var files = _files;
        if (files is null) return;
        if (_busy is not null)
        {
            StatusText.Text = Loc.T("GuestFiles_Busy"); // 놓은 파일을 조용히 버리지 않고 알린다
            return;
        }

        var sources = paths.Where(File.Exists).ToList();
        var skipped = paths.Count - sources.Count;
        // 숨겨 둔 항목까지(보기에서 뺐어도 덮어쓰게 된다), 게스트 규칙대로(Windows 는 README.md = readme.md)
        var existing = (_lastEntries ?? []).Select(e => e.Name).ToHashSet(files.NameComparer);
        var clashes = sources.Select(Path.GetFileName).Where(n => existing.Contains(n!)).ToList();
        if (clashes.Count > 0 && !Confirm(Loc.T("GuestFiles_OverwriteConfirm", string.Join(", ", clashes.Take(5)),
                clashes.Count)))
            return;

        await RunAsync(Loc.T("GuestFiles_Uploading"), async ct =>
        {
            var directory = _current;
            foreach (var path in sources)
            {
                var name = Path.GetFileName(path);
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                await files.UploadAsync(directory, name, stream, stream.Length,
                    TransferProgress(name, stream.Length), ct);
            }

            await ListAsync(directory, ct);
            StatusText.Text = skipped > 0
                ? Loc.T("GuestFiles_UploadedSkipped", sources.Count, skipped)
                : Loc.T("GuestFiles_Uploaded", sources.Count);
        }, transfer: true);
    }

    private async void OnNewFolder(object sender, RoutedEventArgs e)
    {
        if (_files is not { } files || AskName(Loc.T("GuestFiles_NewFolder"), string.Empty) is not { } name) return;

        var directory = _current;
        await RunAsync(Loc.T("GuestFiles_Working"), async ct =>
        {
            await files.CreateDirectoryAsync(directory, name, ct);
            await ListAsync(directory, ct);
        });
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (_files is not { } files || SelectedRow is not { } row) return;
        if (AskName(Loc.T("GuestFiles_Rename"), row.Entry.Name) is not { } name || name == row.Entry.Name) return;

        var directory = _current;
        await RunAsync(Loc.T("GuestFiles_Working"), async ct =>
        {
            await files.RenameAsync(directory, row.Entry.Name, name, ct);
            await ListAsync(directory, ct);
        });
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_files is not { } files || SelectedRows is not { Count: > 0 } rows) return;

        var names = string.Join(", ", rows.Take(5).Select(r => r.Entry.IsDirectory ? r.Name + "/" : r.Name));
        if (!Confirm(Loc.T("GuestFiles_DeleteConfirm", names, rows.Count, _current))) return;

        // 지우는 동안 폴더가 바뀌어도 고른 폴더의 항목만 지운다
        var directory = _current;
        await RunAsync(Loc.T("GuestFiles_Working"), async ct =>
        {
            foreach (var row in rows) await files.DeleteAsync(directory, row.Entry, ct);
            await ListAsync(directory, ct);
        });
    }

    /// <summary>이름 입력(새 폴더·이름 바꾸기) — 취소하면 null.</summary>
    private string? AskName(string title, string initial)
    {
        var dialog = new FormDialog(title,
        [
            new FormField
            {
                Key = "name", LabelKey = "GuestFiles_Name", Required = true, Trim = false, Initial = initial
            }
        ], values => values["name"].Contains('/') || values["name"] is "." or ".."
            ? Loc.T("GuestFiles_BadName")
            : null) { Owner = OwnerWindow };
        return dialog.ShowDialog() == true && dialog.Result is { } values ? values["name"] : null;
    }

    private bool Confirm(string text)
    {
        var owner = OwnerWindow;
        var result = owner is null
            ? ThemedMessageBox.Show(text, Loc.T("GuestFiles_Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : ThemedMessageBox.Show(owner, text, Loc.T("GuestFiles_Title"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }
}
