using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Files;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     폴더 목록 — 폴더를 열면 곧바로 옮겨 가서 캐시가 있으면 그 목록을, 없으면 빈 목록을 먼저 보이고
///     게스트에서 새로 읽어 바뀐 줄만 반영한다(스크롤·선택 유지). 읽는 동안은 아래 진행 막대만 움직이고 화면을 막지 않는다.
///     다른 폴더로 옮긴 뒤 도착한 옛 결과는 버린다(캐시에는 넣는다).
/// </summary>
public partial class GuestFilesPanel
{
    /// <summary>바뀐 줄이 이보다 많으면 줄마다 고치지 않고 목록을 통째로 바꾼다.</summary>
    private const int MaxMergeChanges = 200;

    private static readonly IComparer<GuestFileEntry> DefaultOrder = Comparer<GuestFileEntry>.Create((a, b) =>
    {
        var byKind = (a.OpensAsFolder ? 0 : 1).CompareTo(b.OpensAsFolder ? 0 : 1);
        return byKind != 0 ? byKind : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
    });

    private IReadOnlyList<GuestFileEntry>? _lastEntries; // 지금 폴더의 전체 목록(숨김 포함) — 보기 전환용
    private bool _entriesComplete; // _lastEntries 가 게스트에서 방금 다 읽은 목록인가(캐시·빈 목록·받는 도중이면 아님)
    private int _listing; // 폴더를 옮길 때마다 올린다 — 옛 결과를 가려낸다
    private int _loads; // 읽는 중인 목록 수(진행 막대)
    private ObservableCollection<GuestFileRow> _rows = [];
    private CancellationTokenSource _loadCancel = new(); // 뒤에서 읽는 목록 — 아래 ✕ 로 멈춘다

    /// <summary>
    ///     폴더로 옮긴다 — 캐시(없으면 빈 목록)를 곧바로 보이고 새로 읽어 반영한다. 작업(올리기·지우기 등) 중에는
    ///     옮기지 않는다(작업이 고른 폴더와 보이는 폴더가 어긋나지 않게).
    /// </summary>
    private async Task NavigateAsync(string directory)
    {
        if (_files is not { } files || _busy is not null) return;

        var generation = ++_listing;
        var cached = files.Peek(directory);
        var back = cached is null ? (_current, _lastEntries) : default; // 못 읽으면 돌아갈 곳
        ShowEntries(directory, cached ?? []);
        _entriesComplete = false;
        if (cached is null) StatusText.Text = Loc.T("GuestFiles_Loading");
        if (!await RevalidateAsync(files, directory, generation, cached is null) && back._lastEntries is { } entries
            && generation == _listing)
        {
            ShowEntries(back._current, entries, false); // 없는 폴더 등 — 그 폴더에 머물지 않고 되돌린다
            _entriesComplete = false;
        }
    }

    /// <summary>새로 고침 — 보이는 목록은 그대로 두고 게스트에서 다시 읽어 바뀐 줄만 반영한다.</summary>
    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_files is not { } files || _busy is not null) return;

        await RevalidateAsync(files, _current, ++_listing);
    }

    /// <summary>
    ///     게스트에서 새로 읽어 반영 — 실패·취소면 상태 줄에 알리고 false. progressive 면(보인 목록이 비어 있을 때)
    ///     받는 도중의 목록도 채워 큰 폴더를 앞부분부터 보인다(캐시가 보일 때는 아직 안 온 줄이 사라지므로 하지 않는다).
    /// </summary>
    private async Task<bool> RevalidateAsync(CachedGuestFileSystem files, string directory, int generation,
        bool progressive = false)
    {
        SetLoading(+1);
        try
        {
            var done = false; // 끝난 뒤에 늦게 도착한 도중 목록이 다 받은 목록을 덮지 않게
            Action<IReadOnlyList<GuestFileEntry>>? partial = progressive
                ? list => Dispatcher.BeginInvoke(() =>
                {
                    if (!done) ShowPartial(directory, generation, list);
                })
                : null;
            var fresh = await files.RefreshAsync(directory, partial, _loadCancel.Token);
            done = true;
            // 작업이 상태 줄(진행률 등)을 쓰는 중이면 목록만 바꾼다
            if (generation == _listing)
            {
                ShowEntries(directory, fresh, _busy is null);
                _entriesComplete = true;
            }
            return true;
        }
        catch (Exception ex) when (IsExpected(ex) || ex is OperationCanceledException)
        {
            App.Log($"[게스트 파일] 목록 실패({directory}): {ex.Message}");
            if (generation != _listing) return false;

            StatusText.Foreground = (Brush)FindResource("BrushWarn");
            StatusText.Text = ex is OperationCanceledException
                ? Loc.T("GuestFiles_Cancelled")
                : Loc.T("GuestFiles_Error", ex.Message);
            return false;
        }
        finally
        {
            SetLoading(-1);
        }
    }

    /// <summary>받는 도중의 목록 — 아직 이 폴더를 보고 있으면 채우고 몇 개째인지 알린다.</summary>
    private void ShowPartial(string directory, int generation, IReadOnlyList<GuestFileEntry> list)
    {
        if (generation != _listing) return;

        ShowEntries(directory, list, false);
        _entriesComplete = false;
        if (_busy is null) StatusText.Text = Loc.T("GuestFiles_LoadingCount", list.Count);
    }

    /// <summary>뒤에서 읽는 목록을 멈춘다(CT 셸은 다음 작업 때 새로 연다).</summary>
    private void CancelLoads()
    {
        if (_loads == 0) return;

        _loadCancel.Cancel();
        _loadCancel = new CancellationTokenSource();
    }

    /// <summary>작업(올리기 등) 안에서 — 새로 읽어 보인다(끝까지 기다림).</summary>
    private async Task ListAsync(string directory, CancellationToken ct)
    {
        var files = _files ?? throw new InvalidOperationException();
        var entries = await files.RefreshAsync(directory, ct);
        ++_listing; // 그 전에 시작한 목록 결과가 이것을 덮지 않게
        ShowEntries(directory, entries);
        _entriesComplete = true;
    }

    /// <summary>
    ///     목록을 보인다 — 같은 폴더면 바뀐 줄만 고치고, 다른 폴더면 통째로 바꾼다.
    ///     숨김 파일 보기를 껐으면 숨김·시스템 항목을 뺀다.
    /// </summary>
    private void ShowEntries(string directory, IReadOnlyList<GuestFileEntry> entries, bool updateStatus = true)
    {
        _lastEntries = entries;
        var sorted = entries.Where(e => _showHidden || !e.IsHidden).Order(DefaultOrder).ToList();
        if (directory != _current || !ReferenceEquals(FileGrid.ItemsSource, _rows) || !TryMerge(sorted))
        {
            // 통째로 바꾸면 열 머리글로 고른 정렬이 풀리므로 옮겨 붙인다
            var sorts = FileGrid.Items.SortDescriptions.ToList();
            _rows = new ObservableCollection<GuestFileRow>(sorted.Select(e => new GuestFileRow(e, this)));
            FileGrid.ItemsSource = _rows;
            foreach (var sort in sorts) FileGrid.Items.SortDescriptions.Add(sort);
        }

        _current = directory;
        UpdateAddress(directory, sorted.Any(e => e.OpensAsFolder));
        Placeholder.Visibility = Visibility.Collapsed;
        if (!updateStatus) return;

        StatusText.Foreground = (Brush)FindResource("BrushDim");
        var hidden = entries.Count - sorted.Count;
        StatusText.Text = hidden > 0
            ? Loc.T("GuestFiles_CountHidden", sorted.Count, hidden)
            : Loc.T("GuestFiles_Count", sorted.Count);
    }

    /// <summary>바뀐 줄만 고친다(없어진 줄 빼기, 바뀐 줄 바꾸기, 새 줄 끼우기) — 너무 많이 바뀌었으면 false.</summary>
    private bool TryMerge(IReadOnlyList<GuestFileEntry> sorted)
    {
        var fresh = new Dictionary<string, GuestFileEntry>(StringComparer.Ordinal);
        foreach (var entry in sorted) fresh.TryAdd(entry.Name, entry);
        var known = _rows.Select(r => r.Entry.Name).ToHashSet(StringComparer.Ordinal);
        var changes = _rows.Count(r => !fresh.TryGetValue(r.Entry.Name, out var e) || e != r.Entry)
                      + fresh.Keys.Count(n => !known.Contains(n));
        if (changes == 0) return true;
        if (changes > MaxMergeChanges) return false;

        var selected = SelectedRows.Select(r => r.Entry.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            if (!fresh.TryGetValue(_rows[i].Entry.Name, out var entry)) _rows.RemoveAt(i);
            else if (entry != _rows[i].Entry) _rows[i] = new GuestFileRow(entry, this);
        }

        foreach (var entry in fresh.Values.Where(e => !known.Contains(e.Name)))
        {
            var at = 0;
            while (at < _rows.Count && DefaultOrder.Compare(_rows[at].Entry, entry) < 0) at++;
            _rows.Insert(at, new GuestFileRow(entry, this));
        }

        // 바뀐 줄은 새 객체라 선택이 풀린다 — 이름으로 되살린다
        foreach (var row in _rows.Where(r => selected.Contains(r.Entry.Name) && !FileGrid.SelectedItems.Contains(r)))
            FileGrid.SelectedItems.Add(row);
        return true;
    }

    /// <summary>읽는 중인 목록 수를 바꾸고 진행 막대를 맞춘다(작업 중이면 작업이 막대를 쓴다).</summary>
    private void SetLoading(int delta)
    {
        _loads = Math.Max(0, _loads + delta);
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        if (_busy is not null) return;

        Progress.IsIndeterminate = _loads > 0;
        Progress.Visibility = BtnCancel.Visibility = _loads > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
