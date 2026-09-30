using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Files;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     게스트 파일 패널 — 파일 창(<see cref="GuestFilesWindow" />)에 담긴다. 처음 보일 때 파일 시스템(CT: 보이지 않는 노드 셸, VM: 게스트 에이전트)을
///     열고 폴더를 보여 준다. 작업은 한 번에 하나(바쁜 동안 버튼을 막는다), 전송은 진행률과 취소를 보인다.
///     창이 닫히면 <see cref="Close" /> 로 연결을 정리한다.
/// </summary>
public partial class GuestFilesPanel : UserControl
{
    private CancellationTokenSource? _busy;
    private bool _closed;
    private string _current = "/";
    private CachedGuestFileSystem? _files;
    private Func<CancellationToken, Task<IGuestFileSystem>>? _open;
    private Task? _opening;

    public GuestFilesPanel()
    {
        InitializeComponent();
        _ = LoadViewSettingsAsync();
    }

    /// <summary>파일 시스템을 여는 방법 — 처음 보일 때 연다.</summary>
    public void Attach(Func<CancellationToken, Task<IGuestFileSystem>> open)
    {
        _open = open;
    }

    /// <summary>
    ///     패널을 보일 때 — 아직 열지 않았으면 연결하고 처음 폴더를 연다. 이미 여는 중이면 그 연결이 끝나기를
    ///     기다린다(연결 중에 콘솔 화면에 놓은 파일도 연결 뒤 올라가게).
    /// </summary>
    public async Task ShowAsync()
    {
        if (_files is not null || _open is null) return;

        var opening = _opening ??= OpenAsync(_open);
        await opening;
        if (ReferenceEquals(_opening, opening)) _opening = null; // 곧바로 끝난 경우에도 다음에 다시 열 수 있게
    }

    private async Task OpenAsync(Func<CancellationToken, Task<IGuestFileSystem>> open)
    {
        try
        {
            await RunAsync(Loc.T("GuestFiles_Connecting"), async ct =>
            {
                var files = await open(ct);
                if (_closed) // 여는 사이 창이 닫혔다 — 연결(노드 셸 등)을 남기지 않는다
                {
                    files.Dispose();
                    return;
                }

                _files = new CachedGuestFileSystem(files); // 이 창이 열려 있는 동안만 폴더 목록을 기억
                await ListAsync(_files.HomePath, ct);
            });
        }
        finally
        {
            _opening = null; // 실패·취소면 다음에 다시 연결
        }
    }

    /// <summary>창을 닫을 때 — 진행 중인 작업을 멈추고 연결을 정리한다.</summary>
    public void Close()
    {
        _closed = true;
        _busy?.Cancel();
        _loadCancel.Cancel();
        _dragCancel.Cancel(); // 탐색기가 받고 있던 끌어내기도 멈춘다
        _files?.Dispose();
        _files = null;
    }

    private GuestFileRow? SelectedRow => FileGrid.SelectedItem as GuestFileRow;

    private IReadOnlyList<GuestFileRow> SelectedRows => FileGrid.SelectedItems.OfType<GuestFileRow>().ToList();

    private async void OnUp(object sender, RoutedEventArgs e)
    {
        if (_files is not null) await NavigateAsync(_files.Parent(_current));
    }

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _files is null) return;

        e.Handled = true;
        var path = PathBox.Text.Trim();
        await NavigateAsync(path.Length == 0 ? _files.HomePath : path);
    }

    /// <summary>더블클릭 — 폴더면 들어가고, 링크면 가리키는 곳(폴더가 아니면 받기), 파일이면 받는다.</summary>
    private async void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_files is null || SelectedRow is not { } row || !IsRowClick(e)) return;

        await OpenAsync(row);
    }

    /// <summary>
    ///     단축키 — Enter·Space 열기(폴더 들어가기, 파일 받기), Backspace·Alt+↑ 위 폴더, Delete 지우기, F2 이름 바꾸기,
    ///     F5 새로 고침, Ctrl+Shift+C 경로 복사. DataGrid 가 Enter(다음 줄)·Delete(줄 빼기)를 먼저 가져가지 않게
    ///     Preview 에서 받는다.
    /// </summary>
    private async void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (_files is null) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var ready = _busy is null;
        switch (key)
        {
            case Key.Enter or Key.Space when modifiers == ModifierKeys.None && ready && SelectedRow is { } row:
                e.Handled = true;
                await OpenAsync(row);
                break;
            case Key.Back when modifiers == ModifierKeys.None:
            case Key.Up when modifiers == ModifierKeys.Alt:
                e.Handled = true;
                await NavigateAsync(_files.Parent(_current));
                break;
            case Key.Delete when modifiers == ModifierKeys.None && ready:
                e.Handled = true;
                OnDelete(sender, e);
                break;
            case Key.F2 when ready:
                e.Handled = true;
                OnRename(sender, e);
                break;
            case Key.F5:
                e.Handled = true;
                OnRefresh(sender, e);
                break;
            case Key.C when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true;
                OnCopyPath(sender, e);
                break;
        }
    }

    private async Task OpenAsync(GuestFileRow row)
    {
        var files = _files!;
        if (row.Entry.IsDirectory)
        {
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
                    StatusText.Text = Loc.T("GuestFiles_LinkIsFile", target); // 폴더가 아닌 링크 — 받기로
                }
            });
            if (_current == target) return;
        }

        await DownloadAsync([row]);
    }

    private static bool IsRowClick(MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow) source = VisualTreeHelper.GetParent(source);
        return source is DataGridRow;
    }

    /// <summary>
    ///     작업 하나를 실행 — 그동안 버튼을 막고, 실패는 상태 줄에 알린다. transfer 면 진행률·취소를 보인다.
    ///     취소하면 원격 작업은 멈출 수 없어 연결을 새로 연다(다음 작업 때 자동).
    /// </summary>
    private async Task RunAsync(string status, Func<CancellationToken, Task> work, bool transfer = false)
    {
        if (_busy is not null) return;

        using var busy = new CancellationTokenSource();
        _busy = busy;
        SetButtons(false);
        StatusText.Foreground = (Brush)FindResource("BrushDim");
        StatusText.Text = status;
        Progress.Value = 0;
        // 전송은 진행률, 그 밖(연결·목록·작업)은 움직이는 막대 — 무언가 하고 있음을 늘 보인다
        Progress.IsIndeterminate = !transfer;
        Progress.Visibility = Visibility.Visible;
        // 취소는 늘 — 목록·지우기도 게스트가 멈추면(멈춘 네트워크 파일 시스템 등) 끝나지 않을 수 있다
        BtnCancel.Visibility = Visibility.Visible;
        try
        {
            await work(busy.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.T("GuestFiles_Cancelled");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            App.Log($"[게스트 파일] {status} 실패: {ex}");
            StatusText.Foreground = (Brush)FindResource("BrushWarn");
            StatusText.Text = Loc.T("GuestFiles_Error", ex.Message);
            if (_files is null && ex is AgentExecBlockedException blocked) ShowUnblockGuide(blocked.Family, blocked.ScriptCommand);
            else if (_files is null) ShowPlaceholder(ex.Message);
        }
        finally
        {
            _busy = null;
            Progress.IsIndeterminate = false;
            Progress.Visibility = BtnCancel.Visibility = Visibility.Collapsed;
            UpdateProgress(); // 뒤에서 읽는 목록이 남아 있으면 막대를 계속 보인다
            SetButtons(_files is not null);
        }
    }

    /// <summary>파일 작업에서 날 수 있는 실패(게스트·연결·로컬 파일) — 상태 줄에 알린다.</summary>
    internal static bool IsExpected(Exception ex)
    {
        return ex is GuestFileException or IOException or InvalidOperationException or UnauthorizedAccessException
            or TimeoutException or FormatException or Core.Api.ProxmoxApiException
            or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException
            or System.Text.Json.JsonException;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _busy?.Cancel();
        CancelLoads();
    }

    private void SetButtons(bool enabled)
    {
        foreach (var button in new[] { BtnUp, BtnRefresh, BtnUpload, BtnDownload, BtnNewFolder, BtnRename, BtnDelete })
            button.IsEnabled = enabled;
        FileGrid.IsEnabled = enabled || _files is not null;
    }

    private void ShowPlaceholder(string text)
    {
        Placeholder.Text = text;
        Placeholder.Visibility = Visibility.Visible;
    }

    /// <summary>진행률 — 받은/보낸 바이트(전체를 알면 막대도).</summary>
    private IProgress<long> TransferProgress(string label, long total)
    {
        return new Progress<long>(done =>
        {
            Progress.Value = total > 0 ? Math.Min(1, (double)done / total) : 0;
            StatusText.Text = total > 0
                ? $"{label} {ByteFormatter.Format(done)} / {ByteFormatter.Format(total)}"
                : $"{label} {ByteFormatter.Format(done)}";
        });
    }
}

/// <summary>파일 패널 한 줄.</summary>
public sealed class GuestFileRow(GuestFileEntry entry, FrameworkElement owner)
{
    public GuestFileEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string NameTip => Entry.LinkTarget is { } target ? $"{Entry.Name} → {target}" : Entry.Name;
    public string SortName => (Entry.IsDirectory ? "0" : "1") + Entry.Name;
    public long SortSize => Entry.IsDirectory ? -1 : Entry.Size;
    public DateTime SortModified => Entry.Modified ?? DateTime.MinValue;
    public string SizeText => Entry.Kind == GuestFileKind.File ? ByteFormatter.Format(Entry.Size) : string.Empty;
    /// <summary>숨김·시스템 항목은 흐리게.</summary>
    public double RowOpacity => Entry.IsHidden ? 0.5 : 1;

    public string ModifiedText => Entry.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    public string OwnerText => Entry.Owner.Length == 0 ? string.Empty
        : Entry.Group.Length == 0 || Entry.Group == Entry.Owner ? Entry.Owner
        : $"{Entry.Owner}:{Entry.Group}";

    public Geometry? Icon => owner.TryFindResource(Entry.Kind switch
    {
        GuestFileKind.Directory => "IconFolder",
        GuestFileKind.Link => "IconLink",
        _ => "IconFile"
    }) as Geometry;

    public Brush? IconBrush => owner.TryFindResource(Entry.IsDirectory ? "BrushAccent" : "BrushDim") as Brush;
}
