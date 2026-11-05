using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Files;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Controls;
using ProxmoxClient.UI.Views.Shared;
using ProxmoxClient.UI.Views.Storage;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>
///     게스트 파일 패널 — WPF GuestFilesPanel 과 같은 배치(위: 올리기·받기·새 폴더·이름·지우기 / 위로·새로 고침·숨김 /
///     주소 표시줄, 가운데: 파일 표, 아래: 진행 막대·취소·상태 줄). 데스크톱·태블릿은 DataGrid 표, 휴대폰은 카드 목록.
///     처음 보일 때 파일 시스템(CT: 노드 셸, VM: 게스트 에이전트 등)을 열고 폴더를 보여 준다. 작업은 한 번에 하나.
///     화면을 떠날 때 <see cref="Close" /> 로 연결을 정리한다.
/// </summary>
public partial class GuestFilesPanel : UserControl
{
    private CancellationTokenSource? _busy;
    private bool _closed;
    private string _current = "/";
    private CachedGuestFileSystem? _files;
    private Func<CancellationToken, Task<IGuestFileSystem>>? _open;
    private Task? _opening;
    private bool _readsApproved;
    private bool _reapplyingSort; // 정렬을 다시 거는 동안의 Sorting 알림은 기억하지 않는다 // 에이전트 명령으로 읽는 것을 이 화면을 열 때 허락받았다(읽기는 쓰기 확인이 아니다)

    private readonly Button _upButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _uploadButton = new();
    private readonly Button _downloadButton = new();
    private readonly Button _newFolderButton = new();
    private readonly Button _renameButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _cancelButton = new();
    private readonly ToggleButton _hiddenButton = new();
    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _crumbScroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly TextBox _pathBox = new() { BorderThickness = new Thickness(0), IsVisible = false };
    private readonly Border _addressBar = new();
    private readonly DataGrid _grid = new();
    private readonly ListBox _cards = new();
    private readonly Grid _body = new();
    private readonly ProgressBar _progress = new() { Maximum = 1, Width = 160, Height = 6, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Classes = { "subtle" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _placeholder = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center, MaxWidth = 520, Margin = new Thickness(20), Classes = { "subtle" }, IsVisible = false
    };
    private readonly Border _guidance = new();
    private readonly TextBlock _guidanceTitle = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _guidanceText = new() { TextWrapping = TextWrapping.Wrap, Classes = { "subtle" }, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _guidanceCommands = new() { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, Padding = new Thickness(8), FontFamily = ProxmoxClient.UI.AppFonts.MonoText };
    private readonly TextBlock _guidanceNote = new() { TextWrapping = TextWrapping.Wrap, Classes = { "subtle" }, Margin = new Thickness(0, 8, 0, 0) };
    private readonly MenuFlyout _menu = new();
    private readonly MenuItem _menuOpen = new();
    private readonly MenuItem _menuDownload = new();
    private readonly MenuItem _menuCopy = new();
    private readonly MenuItem _menuRename = new();
    private readonly MenuItem _menuDelete = new();
    private readonly MenuItem _menuUpload = new();
    private readonly MenuItem _menuNewFolder = new();
    private readonly MenuItem _menuRefresh = new();
    private readonly MenuItem _menuHidden = new();

    public GuestFilesPanel()
    {
        BuildControls();
        Content = BuildLayout();
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, PlatformInfo.IsDesktop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        _ = LoadViewSettingsAsync();
    }

    /// <summary>파일 시스템을 여는 방법 — 처음 보일 때 연다.</summary>
    public void Attach(Func<CancellationToken, Task<IGuestFileSystem>> open)
    {
        _open = open;
    }

    /// <summary>
    ///     게스트 에이전트로 읽는 것을 허락받았음 — 목록·받기가 보내는 명령(POST)을 쓰기 확인으로 묻지 않는다.
    ///     쓰기(올리기·새 폴더·이름 바꾸기·지우기)는 그래도 작업마다 따로 묻는다.
    /// </summary>
    public void ApproveReads()
    {
        _readsApproved = true;
    }

    /// <summary>
    ///     패널을 보일 때 — 아직 열지 않았으면 연결하고 처음 폴더를 연다. 이미 여는 중이면 그 연결이 끝나기를
    ///     기다린다(연결 중에 놓은 파일도 연결 뒤 올라가게).
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
                if (_closed) // 여는 사이 화면을 떠났다 — 연결(노드 셸 등)을 남기지 않는다
                {
                    files.Dispose();
                    return;
                }

                _files = new CachedGuestFileSystem(files); // 이 화면이 열려 있는 동안만 폴더 목록을 기억
                // 허락(ApproveReads)은 여는 동안 받으므로, 처음 목록은 이 시점의 허락으로 묶는다
                using var listFlow = WriteConfirmation.Begin(FlowName, _readsApproved);
                await ListAsync(_files.HomePath, ct);
            });
        }
        finally
        {
            _opening = null; // 실패·취소면 다음에 다시 연결
        }
    }

    /// <summary>화면을 떠날 때 — 진행 중인 작업을 멈추고 연결을 정리한다.</summary>
    public void Close()
    {
        _closed = true;
        _busy?.Cancel();
        _loadCancel.Cancel();
        _files?.Dispose();
        _files = null;
    }

    private GuestFileRow? SelectedRow => _grid.IsVisible ? _grid.SelectedItem as GuestFileRow : _cards.SelectedItem as GuestFileRow;

    private IReadOnlyList<GuestFileRow> SelectedRows => SelectionList.OfType<GuestFileRow>().ToList();

    private IList SelectionList => (_grid.IsVisible ? _grid.SelectedItems : _cards.SelectedItems) ?? new List<object>();

    /// <summary>단축키 — Enter·Space 열기, Backspace·Alt+↑ 위 폴더, Delete 지우기, F2 이름 바꾸기, F5 새로 고침,
    /// Ctrl+Shift+C 경로 복사, Ctrl+L·Alt+D 주소 편집.</summary>
    private async void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.Key;
        var modifiers = e.KeyModifiers;
        if ((key == Key.L && modifiers == KeyModifiers.Control) || (key == Key.D && modifiers == KeyModifiers.Alt))
        {
            e.Handled = true;
            BeginAddressEdit();
            return;
        }

        if (_pathBox.IsVisible || _files is null) return;

        var ready = _busy is null;
        switch (key)
        {
            case Key.Enter or Key.Space when modifiers == KeyModifiers.None && ready && SelectedRow is { } row:
                e.Handled = true;
                await OpenRowAsync(row);
                break;
            case Key.Back when modifiers == KeyModifiers.None:
            case Key.Up when modifiers == KeyModifiers.Alt:
                e.Handled = true;
                await NavigateAsync(_files.Parent(_current));
                break;
            case Key.Delete when modifiers == KeyModifiers.None && ready:
                e.Handled = true;
                await DeleteSelectedAsync();
                break;
            case Key.F2 when ready:
                e.Handled = true;
                await RenameSelectedAsync();
                break;
            case Key.F5:
                e.Handled = true;
                await RefreshAsync();
                break;
            case Key.C when modifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                e.Handled = true;
                await CopyPathAsync();
                break;
        }
    }

    /// <summary>
    ///     작업 하나를 실행 — 그동안 버튼을 막고, 실패는 상태 줄에 알린다. transfer 면 진행률·취소를 보인다.
    ///     취소하면 원격 작업은 멈출 수 없어 연결을 새로 연다(다음 작업 때 자동). flow 는 쓰기 확인 흐름의 이름이다.
    /// </summary>
    private async Task RunAsync(string status, Func<CancellationToken, Task> work, bool transfer = false,
        bool confirmed = false)
    {
        if (_busy is not null) return;

        using var busy = new CancellationTokenSource();
        _busy = busy;
        // 이 작업의 서버 요청(에이전트 명령 등)은 한 번의 확인으로 묶는다 — 화면이 이미 물었으면(confirmed) 다시 묻지 않는다
        using var flow = WriteConfirmation.Begin(FlowName, confirmed);
        SetButtons(false);
        _status.Foreground = Brush("BrushDim");
        _status.Text = status;
        _progress.Value = 0;
        // 전송은 진행률, 그 밖(연결·목록·작업)은 움직이는 막대 — 무언가 하고 있음을 늘 보인다
        _progress.IsIndeterminate = !transfer;
        _progress.IsVisible = true;
        // 취소는 늘 — 목록·지우기도 게스트가 멈추면(멈춘 네트워크 파일 시스템 등) 끝나지 않을 수 있다
        _cancelButton.IsVisible = true;
        try
        {
            await work(busy.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WriteCancelledException)
        {
            _status.Text = Loc.T("GuestFiles_Cancelled");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            System.Diagnostics.Debug.WriteLine($"[게스트 파일] {status} 실패: {ex}");
            _status.Foreground = Brush("BrushWarn");
            _status.Text = Loc.T("GuestFiles_Error", ex.Message);
            if (_files is null && ex is AgentExecBlockedException blocked)
                ShowUnblockGuide(blocked.Family, blocked.ScriptCommand);
            else if (_files is null) ShowPlaceholder(ex.Message);
        }
        finally
        {
            _busy = null;
            _progress.IsIndeterminate = false;
            _progress.IsVisible = _cancelButton.IsVisible = false;
            UpdateProgress(); // 뒤에서 읽는 목록이 남아 있으면 막대를 계속 보인다
            SetButtons(_files is not null);
        }
    }

    /// <summary>파일 작업에서 날 수 있는 실패(게스트·연결·로컬 파일) — 상태 줄에 알린다.</summary>
    internal static bool IsExpected(Exception ex)
    {
        return ex is GuestFileException or IOException or InvalidOperationException or UnauthorizedAccessException
            or TimeoutException or FormatException or ProxmoxApiException
            or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException
            or System.Text.Json.JsonException;
    }

    private void OnCancel()
    {
        _busy?.Cancel();
        CancelLoads();
    }

    private void SetButtons(bool enabled)
    {
        foreach (var button in new[] { _upButton, _refreshButton, _uploadButton, _downloadButton, _newFolderButton, _renameButton, _deleteButton })
            button.IsEnabled = enabled;
        _grid.IsEnabled = _cards.IsEnabled = enabled || _files is not null;
    }

    private void ShowPlaceholder(string text)
    {
        _placeholder.Text = text;
        _placeholder.IsVisible = true;
    }

    /// <summary>진행률 — 받은/보낸 바이트(전체를 알면 막대도).</summary>
    private IProgress<long> TransferProgress(string label, long total)
    {
        return new Progress<long>(done =>
        {
            _progress.Value = total > 0 ? Math.Min(1, (double)done / total) : 0;
            _status.Text = total > 0
                ? $"{label} {ByteFormatter.Format(done)} / {ByteFormatter.Format(total)}"
                : $"{label} {ByteFormatter.Format(done)}";
        });
    }

    private static IBrush? Brush(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            ? value as IBrush
            : null;

    private static Geometry? Icon(string key) => StorageFiles.Icon(key);

    private void BuildControls()
    {
        _upButton.Margin = new Thickness(0);
        IconAssist.SetIcon(_upButton, Icon("IconArrowUp"));
        ToolTip.SetTip(_upButton, Loc.T("GuestFiles_Up"));
        _upButton.Click += async (_, _) => { if (_files is not null) await NavigateAsync(_files.Parent(_current)); };

        IconAssist.SetIcon(_refreshButton, Icon("IconRefresh"));
        ToolTip.SetTip(_refreshButton, Loc.T("GuestFiles_Refresh"));
        _refreshButton.Margin = new Thickness(4, 0, 0, 0);
        _refreshButton.Click += async (_, _) => await RefreshAsync();

        _hiddenButton.Content = new Avalonia.Controls.Shapes.Path
        {
            Classes = { "icon" }, Data = Icon("IconEye"), Width = 15, Height = 15, Stretch = Stretch.Uniform
        };
        _hiddenButton.Margin = new Thickness(4, 0, 6, 0);
        _hiddenButton.Padding = new Thickness(8, 5);
        ToolTip.SetTip(_hiddenButton, Loc.T("GuestFiles_ShowHidden"));
        _hiddenButton.IsCheckedChanged += (_, _) => OnToggleHidden();

        _uploadButton.Margin = new Thickness(6, 0, 0, 0);
        IconAssist.SetIcon(_uploadButton, Icon("IconUpload"));
        ToolTip.SetTip(_uploadButton, Loc.T("GuestFiles_Upload"));
        _uploadButton.Click += async (_, _) => await UploadPickedAsync();

        _downloadButton.Margin = new Thickness(4, 0, 0, 0);
        IconAssist.SetIcon(_downloadButton, Icon("IconDownload"));
        ToolTip.SetTip(_downloadButton, Loc.T("GuestFiles_Download"));
        _downloadButton.Click += async (_, _) => await DownloadSelectedAsync();

        _newFolderButton.Margin = new Thickness(4, 0, 0, 0);
        IconAssist.SetIcon(_newFolderButton, Icon("IconFolderPlus"));
        ToolTip.SetTip(_newFolderButton, Loc.T("GuestFiles_NewFolder"));
        _newFolderButton.Click += async (_, _) => await NewFolderAsync();

        _renameButton.Margin = new Thickness(4, 0, 0, 0);
        IconAssist.SetIcon(_renameButton, Icon("IconPencil"));
        ToolTip.SetTip(_renameButton, Loc.T("GuestFiles_Rename"));
        _renameButton.Click += async (_, _) => await RenameSelectedAsync();

        _deleteButton.Margin = new Thickness(4, 0, 0, 0);
        IconAssist.SetIcon(_deleteButton, Icon("IconTrash"));
        IconAssist.SetBrush(_deleteButton, Brush("BrushErr"));
        ToolTip.SetTip(_deleteButton, Loc.T("GuestFiles_Delete"));
        _deleteButton.Click += async (_, _) => await DeleteSelectedAsync();

        IconAssist.SetIcon(_cancelButton, Icon("IconX"));
        ToolTip.SetTip(_cancelButton, Loc.T("AppSettingsWindow_13"));
        _cancelButton.IsVisible = false;
        _cancelButton.Margin = new Thickness(8, 0, 0, 0);
        _cancelButton.Click += (_, _) => OnCancel();

        // 주소 표시줄 — 빈 곳을 누르면 경로를 직접 고친다(탐색기처럼)
        _addressBar.Background = Brush("BrushBg");
        _addressBar.BorderBrush = Brush("BrushBorder");
        _addressBar.BorderThickness = new Thickness(1);
        _addressBar.CornerRadius = new CornerRadius(4);
        _addressBar.MinHeight = 30;
        _addressBar.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(_addressBar, Loc.T("GuestFiles_BreadcrumbHint"));
        _addressBar.PointerPressed += OnAddressPressed;
        _pathBox.KeyDown += OnPathKeyDown;
        _pathBox.LostFocus += (_, _) => EndAddressEdit();
        ToolTip.SetTip(_pathBox, Loc.T("GuestFiles_PathHint"));

        BuildGrid();
        BuildCards();
        BuildMenu();
    }

    private void BuildGrid()
    {
        _grid.CanUserSortColumns = true;
        _grid.IsReadOnly = true;
        _grid.CanUserResizeColumns = true;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.HorizontalGridLinesBrush = Brush("BrushBorder");
        _grid.SelectionMode = DataGridSelectionMode.Extended;
        _grid.FontSize = 13;
        _grid.RowHeight = 26; // WPF FileGrid 와 같은 줄 높이
        _grid.ContextFlyout = _menu;
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        // 머리글 정렬을 기억한다 — 목록을 통째로 바꿀 때(폴더를 옮길 때) 다시 건다. 처음 누르면 오름차순, 다시 누르면 내림차순
        _grid.Sorting += (_, e) =>
        {
            if (_reapplyingSort) return;

            _sortDirection = ReferenceEquals(e.Column, _sortColumn) && _sortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
            _sortColumn = e.Column;
        };
        _grid.DoubleTapped += async (_, e) =>
        {
            if (!IsRowTap(e.Source) || SelectedRow is not { } row) return;

            await OpenRowAsync(row);
        };
        _grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);

        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = Loc.T("GuestFiles_Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 200,
            SortMemberPath = "SortName", CellTemplate = new FuncDataTemplate<GuestFileRow>((row, _) => NameCell(row, false))
        });
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = Loc.T("GuestFiles_Size"), Width = new DataGridLength(100), SortMemberPath = "SortSize",
            CellTemplate = new FuncDataTemplate<GuestFileRow>((row, _) => new TextBlock
            {
                Text = row?.SizeText ?? string.Empty, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
            })
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Loc.T("GuestFiles_Modified"), Width = new DataGridLength(150), Binding = new Binding("ModifiedText"),
            SortMemberPath = "SortModified"
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Loc.T("GuestFiles_Mode"), Width = new DataGridLength(70), Binding = new Binding("Entry.Mode")
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Loc.T("GuestFiles_Owner"), Width = new DataGridLength(140), Binding = new Binding("OwnerText")
        });
    }

    private void BuildCards()
    {
        _cards.Classes.Add("cards");
        _cards.Margin = new Thickness(12, 0); // 휴대폰 양옆 여백(가이드: 16px 이내의 여백, 표 화면과 같은 12)
        _cards.SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle;
        _cards.ItemTemplate = new FuncDataTemplate<GuestFileRow>((row, _) => NameCell(row, true));
        _cards.ContextFlyout = _menu;
        _cards.SelectionChanged += (_, _) => UpdateButtons();
        _cards.DoubleTapped += async (_, e) =>
        {
            if (SelectedRow is { } row) await OpenRowAsync(row);
        };
    }

    /// <summary>파일 이름 칸 — 아이콘(링크면 작은 표시) + 이름. 휴대폰은 이름 아래에 크기·날짜 줄.</summary>
    private static Control NameCell(GuestFileRow? row, bool card)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(card ? 0 : 4, card ? 4 : 0) };
        if (row is null) return panel;

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var iconGrid = new Grid { Width = 14, Height = 14, Margin = new Thickness(0, 0, 0, 0) };
        iconGrid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = row.Icon, Stretch = Stretch.Uniform, Stroke = row.IconBrush, StrokeThickness = 1.6,
            StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round
        });
        if (row.LinkBadge)
        {
            iconGrid.Children.Add(new Border
            {
                Width = 9, Height = 9, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-3, 0, 0, -3),
                Background = Brush("BrushBg"),
                Child = new Avalonia.Controls.Shapes.Path
                {
                    Data = Icon("IconLink"), Stretch = Stretch.Uniform, Margin = new Thickness(1),
                    Stroke = Brush("BrushText"), StrokeThickness = 1.3, StrokeLineCap = PenLineCap.Round
                }
            });
        }
        line.Children.Add(iconGrid);
        var name = new TextBlock
        {
            Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Opacity = row.RowOpacity, FontWeight = card ? FontWeight.SemiBold : FontWeight.Normal, FontSize = card ? 14 : 13
        };
        ToolTip.SetTip(name, row.NameTip);
        line.Children.Add(name);
        panel.Children.Add(line);
        if (card)
        {
            var detail = string.Join("  ", new[] { row.SizeText, row.ModifiedText }.Where(t => t.Length > 0));
            if (detail.Length > 0)
                panel.Children.Add(new TextBlock { Text = detail, Classes = { "subtle" }, FontSize = 12, Margin = new Thickness(22, 2, 0, 0), Opacity = row.RowOpacity });
        }

        return panel;
    }

    /// <summary>오른쪽 클릭 — 선택 밖의 줄이면 그 줄만 고르고, 빈 곳이면 선택을 푼다(메뉴가 가리키는 대상).</summary>
    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_grid).Properties.IsRightButtonPressed) return;

        if (e.Source is Visual source && source.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault() is { DataContext: GuestFileRow row })
        {
            if (_grid.SelectedItems.Contains(row)) return;

            _grid.SelectedItems.Clear();
            _grid.SelectedItem = row;
            return;
        }

        _grid.SelectedItems.Clear();
    }

    private static bool IsRowTap(object? source) =>
        source is Visual visual && visual.GetVisualAncestors().OfType<DataGridRow>().Any();

    private void BuildMenu()
    {
        _menuOpen.Header = Loc.T("GuestFiles_Open");
        _menuOpen.InputGesture = new KeyGesture(Key.Enter);
        _menuOpen.Click += async (_, _) => { if (_files is not null && SelectedRow is { } row) await OpenRowAsync(row); };
        _menuDownload.Header = Loc.T("GuestFiles_Download");
        _menuDownload.Icon = Glyph("IconDownload");
        _menuDownload.Click += async (_, _) => await DownloadSelectedAsync();
        _menuCopy.Header = Loc.T("GuestFiles_CopyPath");
        _menuCopy.Icon = Glyph("IconCopy");
        _menuCopy.InputGesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift);
        _menuCopy.Click += async (_, _) => await CopyPathAsync();
        _menuRename.Header = Loc.T("GuestFiles_Rename");
        _menuRename.Icon = Glyph("IconPencil");
        _menuRename.InputGesture = new KeyGesture(Key.F2);
        _menuRename.Click += async (_, _) => await RenameSelectedAsync();
        _menuDelete.Header = Loc.T("GuestFiles_Delete");
        _menuDelete.Icon = Glyph("IconTrash");
        _menuDelete.InputGesture = new KeyGesture(Key.Delete);
        _menuDelete.Click += async (_, _) => await DeleteSelectedAsync();
        _menuUpload.Header = Loc.T("GuestFiles_Upload");
        _menuUpload.Icon = Glyph("IconUpload");
        _menuUpload.Click += async (_, _) => await UploadPickedAsync();
        _menuNewFolder.Header = Loc.T("GuestFiles_NewFolder");
        _menuNewFolder.Icon = Glyph("IconFolderPlus");
        _menuNewFolder.Click += async (_, _) => await NewFolderAsync();
        _menuRefresh.Header = Loc.T("GuestFiles_Refresh");
        _menuRefresh.Icon = Glyph("IconRefresh");
        _menuRefresh.InputGesture = new KeyGesture(Key.F5);
        _menuRefresh.Click += async (_, _) => await RefreshAsync();
        _menuHidden.Header = Loc.T("GuestFiles_ShowHidden");
        _menuHidden.Click += (_, _) => _hiddenButton.IsChecked = !_hiddenButton.IsChecked;
        _menu.Items.Add(_menuOpen);
        _menu.Items.Add(_menuDownload);
        _menu.Items.Add(_menuCopy);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(_menuRename);
        _menu.Items.Add(_menuDelete);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(_menuUpload);
        _menu.Items.Add(_menuNewFolder);
        _menu.Items.Add(_menuRefresh);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(_menuHidden);
        _menu.Opening += (_, _) => UpdateMenu();
    }

    private static Avalonia.Controls.Shapes.Path? Glyph(string iconKey) => Icon(iconKey) is { } data
        ? new Avalonia.Controls.Shapes.Path { Data = data, Classes = { "icon" }, Width = 14, Height = 14 }
        : null;

    private Control BuildLayout()
    {
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_uploadButton);
        actions.Children.Add(_downloadButton);
        actions.Children.Add(new Border { Width = 1, Background = Brush("BrushBorder"), Margin = new Thickness(8, 4) });
        actions.Children.Add(_newFolderButton);
        actions.Children.Add(_renameButton);
        actions.Children.Add(_deleteButton);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Children = { _upButton, _refreshButton, _hiddenButton } };

        var addressGrid = new Grid();
        _crumbScroll.Content = _crumbs;
        addressGrid.Children.Add(_crumbScroll);
        addressGrid.Children.Add(_pathBox);
        _addressBar.Child = addressGrid;

        Control top;
        if (PlatformInfo.IsDesktop)
        {
            var dock = new DockPanel { Margin = new Thickness(8, 5) };
            DockPanel.SetDock(actions, Dock.Right);
            dock.Children.Add(actions);
            DockPanel.SetDock(navigation, Dock.Left);
            dock.Children.Add(navigation);
            dock.Children.Add(_addressBar);
            top = new Border { Padding = new Thickness(8, 5), Background = Brush("BrushPanel"), BorderBrush = Brush("BrushBorder"), BorderThickness = new Thickness(0, 0, 0, 1), Child = dock };
        }
        else
        {
            toolbar.Children.Add(navigation);
            toolbar.Children.Add(actions);
            var stack = new StackPanel { Spacing = 6, Children = { toolbar, _addressBar } };
            top = new Border { Padding = new Thickness(8, 6), Background = Brush("BrushPanel"), BorderBrush = Brush("BrushBorder"), BorderThickness = new Thickness(0, 0, 0, 1), Child = stack };
        }

        // 아래 줄: 취소 · 진행 막대 · 상태 줄
        var bottomDock = new DockPanel();
        DockPanel.SetDock(_cancelButton, Dock.Right);
        DockPanel.SetDock(_progress, Dock.Right);
        bottomDock.Children.Add(_cancelButton);
        bottomDock.Children.Add(_progress);
        bottomDock.Children.Add(_status);
        var bottom = new Border
        {
            Padding = new Thickness(8, 4), Background = Brush("BrushPanel"), BorderBrush = Brush("BrushBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0), Child = bottomDock
        };
        _status.Text = Loc.T("GuestFiles_DropHint");

        var guidancePanel = new StackPanel
        {
            Children =
            {
                _guidanceTitle, _guidanceText, _guidanceCommands, _guidanceNote,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
                    Children =
                    {
                        CopyCommandsButton(),
                        RetryButton()
                    }
                }
            }
        };
        _guidance.MaxWidth = 760;
        _guidance.Margin = new Thickness(20);
        _guidance.Padding = new Thickness(16);
        _guidance.HorizontalAlignment = HorizontalAlignment.Center;
        _guidance.VerticalAlignment = VerticalAlignment.Center;
        _guidance.CornerRadius = new CornerRadius(6);
        _guidance.BorderThickness = new Thickness(1);
        _guidance.Background = Brush("BrushPanel");
        _guidance.BorderBrush = Brush("BrushBorder");
        _guidance.IsVisible = false;
        _guidance.Child = guidancePanel;
        _guidanceCommands.IsReadOnly = true;

        _body.Children.Add(_grid);
        _body.Children.Add(_cards);
        _body.Children.Add(_placeholder);
        _body.Children.Add(_guidance);
        if (PlatformInfo.IsDesktop) _cards.IsVisible = false;
        else _grid.IsVisible = false;

        var root = new DockPanel { Background = Brush("BrushBg") };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_body);
        return root;
    }

    private Button CopyCommandsButton()
    {
        var button = new Button { Content = Loc.T("GuestFiles_CopyCommands"), Margin = new Thickness(0) };
        IconAssist.SetIcon(button, Icon("IconCopy"));
        button.Click += async (_, _) => await CopyCommandsAsync();
        return button;
    }

    private Button RetryButton()
    {
        var button = new Button { Content = Loc.T("GuestFiles_Retry"), Margin = new Thickness(8, 0, 0, 0), Classes = { "accent" } };
        IconAssist.SetIcon(button, Icon("IconRefresh"));
        button.Click += async (_, _) =>
        {
            _guidance.IsVisible = false;
            await ShowAsync();
        };
        return button;
    }

    private void UpdateButtons()
    {
        var ready = _files is not null && _busy is null;
        var count = SelectedRows.Count;
        _renameButton.IsEnabled = ready && count == 1;
        _deleteButton.IsEnabled = ready && count > 0;
        _downloadButton.IsEnabled = ready && count > 0;
    }

    private void UpdateMenu()
    {
        var ready = _files is not null && _busy is null;
        var count = SelectedRows.Count;
        _menuOpen.IsEnabled = ready && count == 1;
        _menuDownload.IsEnabled = ready && count > 0;
        _menuCopy.IsEnabled = _files is not null;
        _menuRename.IsEnabled = ready && count == 1;
        _menuDelete.IsEnabled = ready && count > 0;
        _menuUpload.IsEnabled = _menuNewFolder.IsEnabled = _menuRefresh.IsEnabled = ready;
        _menuHidden.IsEnabled = true;
        _menuHidden.Icon = _hiddenButton.IsChecked == true ? Glyph("IconCheck") : null;
    }
}
