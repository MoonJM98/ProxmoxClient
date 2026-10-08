using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Files;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     목록에서 PC(탐색기 등)로 끌어내기 — 가상 파일(<see cref="VirtualFileDataObject" />)로 이름·크기만 알리고,
///     놓은 뒤 받는 쪽이 파일마다 달라고 할 때 게스트에서 임시 폴더로 받아 건넨다. 탐색기는 이 받기를 자기 진행 창에서
///     뒤에서 하므로 앱이 멈추지 않는다. 파일만(폴더는 [받기] 로). 지난 끌어내기의 임시 폴더는 다음 끌기 때 지운다.
/// </summary>
public partial class GuestFilesPanel
{
    private static readonly string DragRoot = Path.Combine(Path.GetTempPath(), "ProxmoxClient-drag");
    private readonly CancellationTokenSource _dragCancel = new(); // 창을 닫으면 끌어내기 받기도 멈춘다
    private Point? _dragStart;

    /// <summary>패널에서 끌어내는 중 — 앱 안(패널·콘솔 화면)에 놓으면 받았다가 다시 올리게 되므로 받지 않는다.</summary>
    public static bool IsDraggingOut { get; private set; }

    private void OnGridPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = IsRowClick(e) ? e.GetPosition(this) : null;
    }

    private void OnGridPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || _files is not { } files
            || _busy is not null || SelectedRows is not { Count: > 0 } rows)
            return;

        var moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragStart = null;
        var entries = rows.Select(r => r.Entry).Where(entry => entry.Kind == GuestFileKind.File).ToList();
        if (entries.Count == 0) return;

        CleanOldDrags();
        DragOut(files, entries);
    }

    /// <summary>끌기 — 놓은 뒤 받는 쪽이 비동기로 받으면 끝날 때, 아니면 끌기가 끝날 때 결과를 알린다.</summary>
    private void DragOut(IGuestFileSystem files, IReadOnlyList<GuestFileEntry> entries)
    {
        var folder = Path.Combine(DragRoot, Guid.NewGuid().ToString("N"));
        var directory = _current;
        var token = _dragCancel.Token;
        var virtualFiles = entries.Select(entry =>
        {
            // 진행률은 UI 스레드에서 만들어 둔다. 받기는 한 번만(같은 파일을 두 번 달라고 해도 같은 결과)
            var progress = TransferProgress(entry.Name, entry.Size);
            var fetch = new Lazy<Task<string>>(() =>
                Task.Run(() => FetchForDragAsync(files, directory, entry, folder, progress, token)));
            return new VirtualFile(LocalName(entry), entry.Size, entry.Modified, () => Wait(fetch.Value));
        }).ToList();

        var data = new VirtualFileDataObject(virtualFiles);
        data.Finished += failed => Dispatcher.BeginInvoke(() => FinishDragOut(data, failed, folder));
        bool dropped;
        IsDraggingOut = true;
        try
        {
            dropped = VirtualFileDrag.Run(data, 1); // DROPEFFECT_COPY
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
        {
            App.Log($"[게스트 파일] 끌어내기 실패: {ex}");
            FinishDragOut(data, true, folder);
            return;
        }
        finally
        {
            IsDraggingOut = false;
        }

        // 놓지 않았으면(취소·받지 않는 곳) 결과 없이 정리만
        if (!data.StartedAsync) FinishDragOut(data, dropped && data.AnyFailed, folder);
    }

    /// <summary>파일 하나를 임시 폴더로 받아 경로를 돌려준다(스레드 풀).</summary>
    private static async Task<string> FetchForDragAsync(IGuestFileSystem files, string directory,
        GuestFileEntry entry, string folder, IProgress<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, LocalName(entry));
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            await files.DownloadAsync(directory, entry, stream, progress, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    ///     받기를 기다린다 — 받는 쪽의 요청이 UI 스레드로 들어오면 메시지를 돌리며 기다려 창이 멈추지 않고
    ///     진행률도 보인다. 다른 스레드면 그냥 기다린다.
    /// </summary>
    private static string Wait(Task<string> task)
    {
        if (Dispatcher.FromThread(Thread.CurrentThread) is not null && !task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        return task.GetAwaiter().GetResult();
    }

    /// <summary>끌어내기가 끝났을 때 — 결과를 알리고 임시 폴더를 지운다(받는 쪽이 다 읽은 뒤).</summary>
    private void FinishDragOut(VirtualFileDataObject data, bool failed, string folder)
    {
        TryDeleteFolder(folder);
        if (!failed && data.Fetched == 0) return; // 받는 쪽이 가져가지 않았다

        StatusText.Foreground = (Brush)FindResource(failed ? "BrushWarn" : "BrushDim");
        StatusText.Text = failed ? Loc.T("GuestFiles_DragFailed") : Loc.T("GuestFiles_Downloaded", data.Fetched);
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log($"[게스트 파일] 끌어내기 임시 폴더 지우기 실패: {ex.Message}"); // 다음 끌기 때 다시 정리
        }
    }

    /// <summary>지난 끌어내기가 남긴 임시 폴더(한 시간 넘은 것) — 받는 쪽이 복사를 끝냈을 만큼 지난 뒤 지운다.</summary>
    private static void CleanOldDrags()
    {
        try
        {
            if (!Directory.Exists(DragRoot)) return;

            foreach (var folder in Directory.EnumerateDirectories(DragRoot))
                if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddHours(-1))
                    Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log($"[게스트 파일] 끌어내기 임시 폴더 정리 실패: {ex.Message}");
        }
    }
}
