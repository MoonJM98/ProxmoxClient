using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Controls;

namespace ProxmoxClient.UI.Views.Shared;

/// <summary>
///     서버 목록 하나를 보여 주는 표 — WPF TableTab 과 같은 생성자·같은 배치(위: 작업 버튼 · 새로고침 · 필터 /
///     가운데: 표 / 아래: 상태 줄). 데스크톱은 WPF 와 같은 DataGrid(열 폭 자동 맞춤·정렬·더블클릭 열기).
///     휴대폰은 한눈에 볼 것만 담은 카드(제목 · 상태 칩 · 요약 한 줄 · ›)이고, 카드를 누르면 아래에서 상세 시트가 올라와
///     모든 열과 이 항목의 작업(편집·지우기 등)을 보여 준다 — 위 단추 줄에는 항목을 고르지 않고 하는 작업(추가 등)만 남는다.
/// </summary>
public sealed partial class TableTab : UserControl
{
    private const int MaxLooseActions = 5;
    private const int KeepVisibleActions = 4;
    private const string AutoMoreMenu = "GuestLife_MoreMenu";
    private const double MaxAutoColumnWidth = 400;
    private const double MinFillColumnWidth = 140;
    private const double CellPadding = 44; // 칸 여백
    private const double HeaderPadding = 64; // 머리글 여백(8+8) + 정렬 화살표 + 나눔 손잡이

    private readonly List<(Button Button, TableAction? Action)> _actions = [];
    private readonly IReadOnlyList<TableColumn> _columns;
    private readonly Func<Task<IReadOnlyList<IReadOnlyDictionary<string, string>>>> _load;
    private readonly Action<IReadOnlyDictionary<string, string>, Control?>? _open;
    private readonly DataGrid? _grid;
    private readonly ListBox? _cards;
    private readonly TextBlock _status = new() { Classes = { "DialogStatus" }, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _filter = new() { Width = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _actionPanel = new();
    private readonly Grid _root;
    /// <summary>휴대폰: 항목을 골라야 하는 작업 — 상세 시트에 단추로 나온다.</summary>
    private readonly List<TableAction> _rowActions = [];
    private readonly int _titleColumn;
    private readonly int _statusColumn = -1;
    private readonly List<int> _summaryColumns = [];
    private List<TableRow> _rows = [];
    private bool _busy;
    private bool _loaded;

    public TableTab(
        Func<Task<IReadOnlyList<IReadOnlyDictionary<string, string>>>> load,
        IReadOnlyList<TableColumn> columns,
        string hintKey,
        IReadOnlyList<TableAction>? actions = null,
        bool filterable = false,
        Action<IReadOnlyDictionary<string, string>, Control?>? open = null)
    {
        _load = load;
        _open = open;
        _columns = columns;
        (_titleColumn, _statusColumn) = PickTitleAndStatus(columns);
        _summaryColumns = PickSummary(columns, _titleColumn, _statusColumn);

        // 위 줄: 작업 버튼 → 새로고침(아이콘만) → 필터 — WPF 와 같은 순서
        var available = (actions ?? []).Where(a => a.Requires is not { IsAvailable: false }).ToList();
        if (PlatformInfo.IsDesktop)
        {
            AddActions(available);
        }
        else
        {
            // 휴대폰: 항목에 하는 작업은 카드를 눌러 뜨는 상세 시트로, 위에는 고르지 않고 하는 작업만
            _rowActions.AddRange(available.Where(a => a.NeedsSelection));
            AddActions(available.Where(a => !a.NeedsSelection).ToList());
            // 여는 것 말고 할 작업이 없으면(검색 결과 등) 누르면 바로 연다
            TapOpens = open is not null && _rowActions.Count == 0;
        }
        var refresh = IconButton(null, "IconRefresh");
        if (!PlatformInfo.IsDesktop) refresh.IsVisible = false; // 휴대폰은 당겨서 새로고침
        ToolTip.SetTip(refresh, L.T("MainWindow_03"));
        refresh.Padding = new Thickness(8, 6);
        refresh.Click += async (_, _) => await ReloadAsync();
        ToolTip.SetTip(_filter, L.T("TableTab_Filter"));
        _filter.TextChanged += (_, _) => ApplyFilter();
        var hint = new TextBlock
        {
            // 휴대폰용 설명이 따로 있으면 그것(누르기·길게 누르기 안내)
            Text = L.T(!PlatformInfo.IsDesktop && L.Has(hintKey + "_Mobile") ? hintKey + "_Mobile" : hintKey),
            Foreground = Brush("BrushDim"), TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 4), IsVisible = L.Has(hintKey)
        };
        Control top;
        if (PlatformInfo.IsDesktop)
        {
            // 데스크톱·태블릿: WPF 와 같이 [작업 버튼 · 새로고침 · 돋보기 + 필터(220)] 뒤 남은 자리에 설명
            // (태블릿은 툴팁을 볼 수 없으므로 칸 안에 "필터" 안내 글자)
            if (PlatformInfo.IsTouch) _filter.PlaceholderText = L.T("TableTab_Filter");
            var filterPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 4), IsVisible = filterable,
                Children =
                {
                    new Avalonia.Controls.Shapes.Path
                    {
                        Classes = { "icon" }, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center, Data = Icon("IconSearch"), Opacity = 0.7
                    },
                    _filter
                }
            };
            var left = new WrapPanel { Children = { _actionPanel, refresh, filterPanel } };
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(left, Dock.Left);
            dock.Children.Add(left);
            dock.Children.Add(hint);
            top = dock;
        }
        else
        {
            // 휴대폰: 작업 버튼 한 줄(새로고침은 목록을 당겨서) → 필터는 화면 폭을 꽉 채운 한 줄
            //        (칸 안에 돋보기와 지우기 ×) → 설명
            _filter.Width = double.NaN;
            _filter.HorizontalAlignment = HorizontalAlignment.Stretch;
            _filter.PlaceholderText = L.T("TableTab_Filter");
            _filter.InnerLeftContent = new Avalonia.Controls.Shapes.Path
            {
                Classes = { "icon" }, Width = 16, Height = 16, Margin = new Thickness(10, 0, 4, 0), Opacity = 0.7,
                Data = Icon("IconSearch")
            };
            var clear = new Button
            {
                Classes = { "icon" }, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 4), MinHeight = 0, IsVisible = false
            };
            IconAssist.SetIcon(clear, Icon("IconX"));
            clear.Click += (_, _) => _filter.Text = string.Empty;
            _filter.InnerRightContent = clear;
            _filter.TextChanged += (_, _) => clear.IsVisible = !string.IsNullOrEmpty(_filter.Text);
            _filter.IsVisible = filterable;
            _filter.Margin = new Thickness(0, 2, 0, 6);
            _actionPanel.IsVisible = _actionPanel.Children.Count > 0;
            top = new StackPanel { Margin = new Thickness(0, 0, 0, 4), Children = { _actionPanel, _filter, hint } };
        }

        Control body;
        if (PlatformInfo.IsDesktop)
        {
            // WPF TableTab 과 같이 행 사이 가로선으로 경계를 보인다
            _grid = new DataGrid
            {
                CanUserSortColumns = true, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = Brush("BrushBorder")
            };
            foreach (var column in columns) _grid.Columns.Add(CreateColumn(column));
            _grid.SelectionChanged += (_, _) => UpdateActionState();
            _grid.DoubleTapped += (_, _) => OpenSelected();
            _grid.FontSize = 13;
            body = _grid;
        }
        else
        {
            _cards = new ListBox { ItemTemplate = MobileRowTemplate(), Classes = { "cards" } };
            // 카드를 누르면 바로 상세 시트(고른 상태는 남기지 않는다 — 다시 눌러도 열리게)
            _cards.SelectionChanged += (_, _) =>
            {
                if (_cards.SelectedItem is not TableRow row) return;

                _cards.SelectedItem = null;
                if (TapOpens && CanOpen(row)) Open(row);
                else _ = ShowDetailAsync(row);
            };
            // 길게 누르기(터치)·오른쪽 클릭 → 상세 시트(누르면 바로 여는 목록에서도 작업을 고를 수 있게)
            _cards.ContextRequested += (_, e) =>
            {
                if ((e.Source as Control)?.DataContext is TableRow row)
                {
                    e.Handled = true;
                    _ = ShowDetailAsync(row);
                }
            };
            // 당겨서 새로고침(휴대폰 관례) — 새로고침 버튼 대신 공간을 아낀다
            var pull = new RefreshContainer { Content = _cards };
            pull.RefreshRequested += async (_, e) =>
            {
                var deferral = e.GetDeferral();
                try
                {
                    await ReloadAsync();
                }
                finally
                {
                    deferral.Complete();
                }
            };
            body = pull;
        }

        _root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"),
            Margin = PlatformInfo.IsDesktop ? new Thickness(20, 16) : new Thickness(12),
            Children = { top, body, _status }
        };
        Grid.SetRow(body, 1);
        Grid.SetRow(_status, 2);
        Content = _root;
        UpdateActionState();
        AttachedToVisualTree += async (_, _) =>
        {
            if (_loaded) return;

            _loaded = true;
            await ReloadAsync();
        };
    }

    /// <summary>
    ///     휴대폰: 카드를 누르면 상세 시트 대신 바로 연다(설정 목록처럼 여는 것이 곧 편집인 경우).
    ///     상세 시트(모든 값·다른 작업)는 길게 눌러 연다.
    /// </summary>
    public bool TapOpens { get; set; }

    /// <summary>이 항목을 열 수 있는지(파일 목록의 파일처럼 못 여는 항목은 누르면 상세 시트).</summary>
    public Func<IReadOnlyDictionary<string, string>, bool>? OpenableIf { get; set; }

    private bool CanOpen(TableRow row) => _open is not null && (OpenableIf?.Invoke(row.Source) ?? true);

    private object? Selected => (object?)_grid?.SelectedItem ?? _cards?.SelectedItem;

    /// <summary>다른 화면 안에 넣을 때 바깥 여백을 없앤다(WPF 와 같은 이름).</summary>
    public TableTab Embedded()
    {
        _root.Margin = new Thickness(0);
        return this;
    }

    private static Geometry? Icon(string key)
    {
        return Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            ? value as Geometry
            : null;
    }

    private static IBrush? Brush(string key)
    {
        return Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            ? value as IBrush
            : null;
    }

    /// <summary>WPF 처럼 아이콘이 붙은 버튼(IconAssist).</summary>
    private static Button IconButton(string? label, string iconKey)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 4), VerticalAlignment = VerticalAlignment.Top };
        IconAssist.SetIcon(button, Icon(iconKey));
        if (label is not null)
        {
            ToolTip.SetTip(button, label);
            CompactButton.Apply(button, label); // 좁은 화면: 아이콘만, 길게 누르면 이름
        }

        return button;
    }

    private static DataGridColumn CreateColumn(TableColumn column)
    {
        var fill = column.Width <= 0;
        var width = fill ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(column.Width);
        var key = column.Key;
        if (!TableFormats.IsCheck(column.Format))
            return new DataGridTextColumn
            {
                Header = L.T(column.HeaderKey), Width = width, MinWidth = fill ? MinFillColumnWidth : 0,
                Binding = new Binding($"[{key}]") { Mode = BindingMode.OneWay }
            };

        return new DataGridTemplateColumn
        {
            Header = L.T(column.HeaderKey), Width = width, SortMemberPath = $"[{key}]",
            CellTemplate = new FuncDataTemplate<TableRow>((row, _) => new TextBlock
            {
                Text = row?[key] == TableFormats.CheckValue ? "✓" : string.Empty, Foreground = Brush("BrushOk"),
                FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0)
            })
        };
    }

    /// <summary>열 폭을 내용에 맞춘다(WPF FitColumns 와 같은 규칙: 지정 폭 이상 · 400 이하, 채움 열은 그대로).</summary>
    private void FitColumns(IReadOnlyList<TableRow> rows)
    {
        if (_grid is null) return;

        // 표가 실제로 쓰는 글꼴(앱 글꼴 Pretendard)로 잰다 — 기본 글꼴로 재면 한글 머리글이 몇 px 모자라 잘렸다
        var family = _grid.FontFamily;
        var face = new Typeface(family);
        var headerFace = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold);
        for (var i = 0; i < _columns.Count && i < _grid.Columns.Count; i++)
        {
            var column = _columns[i];
            if (column.Width <= 0) continue;

            // 머리글은 여백 + 정렬 화살표 자리 + 열 나눔 손잡이까지 — 칸보다 더 넉넉해야 한글 머리글이 잘리지 않는다
            var widest = TextWidth(L.T(column.HeaderKey), headerFace) + HeaderPadding;
            foreach (var row in rows) widest = Math.Max(widest, TextWidth(row[column.Key], face) + CellPadding);
            _grid.Columns[i].Width = new DataGridLength(
                Math.Clamp(widest, column.Width, Math.Max(column.Width, MaxAutoColumnWidth)));
        }
    }

    private static double TextWidth(string text, Typeface face)
    {
        return text.Length == 0
            ? 0
            : new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 13, Brushes.Black)
                .WidthIncludingTrailingWhitespace;
    }

    /// <summary>제목 열(상태 열이 아닌 첫 열 또는 Title)과 상태 칩 열(status·state 또는 Status).</summary>
    private static (int Title, int Status) PickTitleAndStatus(IReadOnlyList<TableColumn> columns)
    {
        var status = IndexOf(columns, c => c.Mobile == MobileColumn.Status);
        if (status < 0)
            status = IndexOf(columns, c => c.Mobile == MobileColumn.Auto && c.Key is "status" or "state" or "active-state" or "health");
        var title = IndexOf(columns, c => c.Mobile == MobileColumn.Title);
        if (title < 0) title = IndexOf(columns, (c, i) => i != status && c.Mobile != MobileColumn.Detail);
        return (Math.Max(0, title), status == title ? -1 : status);
    }

    /// <summary>요약 줄 열 — Summary 로 정한 열, 없으면 제목·상태 다음의 글자 열 둘(체크 열은 시트에서만).</summary>
    private static List<int> PickSummary(IReadOnlyList<TableColumn> columns, int title, int status)
    {
        var chosen = Enumerable.Range(0, columns.Count).Where(i => columns[i].Mobile == MobileColumn.Summary).ToList();
        if (chosen.Count > 0) return chosen;

        return Enumerable.Range(0, columns.Count)
            .Where(i => i != title && i != status && columns[i].Mobile == MobileColumn.Auto &&
                        !TableFormats.IsCheck(columns[i].Format))
            .ToList(); // 비어 있지 않은 앞의 둘만 쓴다(행마다 다르다)
    }

    private static int IndexOf(IReadOnlyList<TableColumn> columns, Func<TableColumn, bool> match) =>
        IndexOf(columns, (c, _) => match(c));

    private static int IndexOf(IReadOnlyList<TableColumn> columns, Func<TableColumn, int, bool> match)
    {
        for (var i = 0; i < columns.Count; i++)
            if (match(columns[i], i)) return i;
        return -1;
    }

    /// <summary>
    ///     카드 제목 — 볼륨 ID(저장소:종류/파일)는 파일 이름만(앞의 저장소·종류는 화면이 이미 보여 준다).
    ///     전체 ID 는 상세 시트에 나온다.
    /// </summary>
    public static string ShortTitle(string title) =>
        VolumeId().Match(title) is { Success: true } match ? match.Groups[1].Value : title;

    /// <summary>저장소:종류/파일 (local:backup/vzdump-….zst, nas:iso/x.iso) — URL·IPv6 는 해당하지 않는다.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z][\w.-]*:[a-z]+/(?:.*/)?([^/]+)$")]
    private static partial System.Text.RegularExpressions.Regex VolumeId();

    /// <summary>상태 칩 글자와 색 — 체크 열(사용 여부)이면 사용/끔.</summary>
    private (string Text, StatusChip.Tone? Tone)? StatusOf(TableRow row)
    {
        if (_statusColumn < 0) return null;

        var column = _columns[_statusColumn];
        if (TableFormats.IsCheck(column.Format))
            return row[column.Key] == TableFormats.CheckValue
                ? (L.T("Common_Enabled"), StatusChip.Tone.Ok)
                : (L.T("Common_Disabled"), StatusChip.Tone.Neutral);
        return row[column.Key] is { Length: > 0 } text ? (text, null) : null;
    }

    private string CellText(TableRow row, TableColumn column) =>
        TableFormats.IsCheck(column.Format)
            ? L.T(row[column.Key] == TableFormats.CheckValue ? "GuestOptions_Yes" : "GuestOptions_No")
            : row[column.Key];

    /// <summary>휴대폰 카드 — 제목 · 상태 칩 / 요약 한 줄(값만 · 로 이어서) · 오른쪽 › (누르면 상세).</summary>
    private IDataTemplate MobileRowTemplate()
    {
        return new FuncDataTemplate<TableRow>((row, _) =>
        {
            var grid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"), RowDefinitions = RowDefinitions.Parse("Auto,Auto"),
                Margin = new Thickness(0, 4), MinHeight = 40
            };
            if (row is null) return grid;

            var title = ShortTitle(row[_columns[_titleColumn].Key]);
            grid.Children.Add(new TextBlock
            {
                Text = title.Length > 0 ? title : "—", FontWeight = FontWeight.SemiBold, FontSize = 15,
                TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (StatusOf(row) is { } status)
            {
                var chip = StatusChip.Create(status.Text, tone: status.Tone);
                chip.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(chip, 1);
                grid.Children.Add(chip);
            }

            // 정한 요약 열은 모두, 자동이면 비어 있지 않은 앞의 둘만
            var explicitSummary = _columns.Any(c => c.Mobile == MobileColumn.Summary);
            var summary = string.Join(" · ", _summaryColumns.Select(i => CellText(row, _columns[i]))
                .Where(text => text.Length > 0).Take(explicitSummary ? int.MaxValue : 2));
            if (summary.Length > 0)
            {
                var line = new TextBlock
                {
                    Text = summary, Foreground = Brush("BrushDim"), FontSize = 13, TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
                };
                Grid.SetRow(line, 1);
                Grid.SetColumnSpan(line, 2);
                grid.Children.Add(line);
            }

            var chevron = new Avalonia.Controls.Shapes.Path
            {
                Classes = { "icon" }, Width = 14, Height = 14, Data = Icon("IconChevronRight"), Opacity = 0.5,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(chevron, 2);
            Grid.SetRowSpan(chevron, 2);
            grid.Children.Add(chevron);
            return grid;
        });
    }

    /// <summary>
    ///     휴대폰 상세 시트 — 모든 열(빈 값은 —)과 표에 없는 서버 필드, 이 항목에 할 수 있는 작업.
    ///     고른 작업은 시트가 닫힌 뒤 그 항목으로 실행한다.
    /// </summary>
    private async Task ShowDetailAsync(TableRow row)
    {
        if (_busy) return;

        var fields = _columns.Select(c => (L.T(c.HeaderKey), CellText(row, c))).ToList();
        var shownKeys = _columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var extra = row.Source.Where(pair => !shownKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value)).ToList();
        var actions = new List<SheetAction>();
        if (CanOpen(row)) actions.Add(new SheetAction(L.T("TableTab_Open"), "IconChevronRight", true, false, null));
        foreach (var action in _rowActions)
            actions.Add(new SheetAction(L.T(action.LabelKey), action.IconKey, false,
                action.IconKey is "IconTrash" || action.LabelKey.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
                action.LabelKey.Contains("Remove", StringComparison.OrdinalIgnoreCase), action));

        var title = row[_columns[_titleColumn].Key];
        var status = StatusOf(row);
        var sheet = new RowDetailSheet(title.Length > 0 ? title : "—", status?.Text, status?.Tone, fields, extra, actions);
        await AppServices.Dialogs.ShowSheetAsync(sheet);

        switch (sheet.Chosen)
        {
            case { Tag: TableAction action }:
                await RunActionAsync(action, row.Source);
                break;
            case { Tag: null }:
                Open(row);
                break;
        }
    }

    private void AddActions(IReadOnlyList<TableAction> requested)
    {
        var actions = CollapseOverflow(requested);
        var menus = actions.Where(action => action.MenuKey is not null)
            .GroupBy(action => action.MenuKey!)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.ToList());
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            if (action.MenuKey is { } key && menus.TryGetValue(key, out var items))
            {
                if (placed.Add(key)) AddMenuButton(key, items);
                continue;
            }

            var label = L.T(action.LabelKey);
            var button = IconButton(action.IconOnly ? null : label, action.IconKey);
            ToolTip.SetTip(button, label);
            if (action.IconOnly) button.Padding = new Thickness(8, 6);
            button.Click += async (_, _) => await RunActionAsync(action);
            _actionPanel.Children.Add(button);
            _actions.Add((button, action));
        }
    }

    private static IReadOnlyList<TableAction> CollapseOverflow(IReadOnlyList<TableAction> actions)
    {
        // 휴대폰: 글자 단추는 둘까지, 넘치면 첫 단추(보통 추가)와 "더 보기" 메뉴 — 아이콘만 남은 단추가 줄지어
        // 무엇인지 알 수 없게 되지 않도록
        var (maxLoose, keep) = PlatformInfo.IsDesktop ? (MaxLooseActions, KeepVisibleActions) : (2, 1);
        var loose = actions.Where(action => action.MenuKey is null && !action.IconOnly).ToList();
        if (loose.Count <= maxLoose) return actions;

        var overflow = loose.Skip(keep).ToHashSet(ReferenceEqualityComparer.Instance);
        return actions.Select(action => overflow.Contains(action) ? action with { MenuKey = AutoMoreMenu } : action)
            .ToList();
    }

    /// <summary>같은 MenuKey 작업을 드롭다운 버튼 하나에(WPF 와 같은 모양: 아이콘 ⋯ + 이름 + ▾).</summary>
    private void AddMenuButton(string menuKey, IReadOnlyList<TableAction> items)
    {
        var label = L.T(menuKey);
        var button = IconButton(label, "IconMore");
        IconAssist.SetDropdown(button, true);
        ToolTip.SetTip(button, $"{label}: {string.Join(", ", items.Select(item => L.T(item.LabelKey)))}");
        var menu = new MenuFlyout();
        var entries = new List<(MenuItem Item, TableAction Action)>();
        foreach (var action in items)
        {
            var item = new MenuItem { Header = L.T(action.LabelKey) };
            if (Icon(action.IconKey) is { } icon)
                item.Icon = new Avalonia.Controls.Shapes.Path { Data = icon, Classes = { "icon" }, Width = 14, Height = 14 };
            item.Click += async (_, _) => await RunActionAsync(action);
            menu.Items.Add(item);
            entries.Add((item, action));
        }

        menu.Opening += (_, _) =>
        {
            var hasSelection = Selected is TableRow;
            foreach (var (item, action) in entries) item.IsEnabled = !action.NeedsSelection || hasSelection;
        };
        button.Flyout = menu;
        _actionPanel.Children.Add(button);
        _actions.Add((button, null));
    }

    private void UpdateActionState()
    {
        var hasSelection = Selected is TableRow;
        foreach (var (button, action) in _actions)
            button.IsEnabled = !_busy && (action is not { NeedsSelection: true } || hasSelection);
    }

    private void ApplyFilter()
    {
        var text = _filter.Text?.Trim();
        var visible = string.IsNullOrEmpty(text)
            ? _rows
            : _rows.Where(row => _columns.Any(c => row[c.Key].Contains(text, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        if (_grid is not null) _grid.ItemsSource = visible;
        if (_cards is not null) _cards.ItemsSource = visible;
        _status.Text = CountText(visible.Count, _rows.Count, text);
    }

    public static string CountText(int visible, int total, string? filter)
    {
        if (total == 0) return L.T("TableTab_Empty");
        return string.IsNullOrWhiteSpace(filter)
            ? L.T("TableTab_Count", total)
            : L.T("TableTab_FilteredCount", visible, total);
    }

    /// <summary>다시 불러온다. 성공하면 true.</summary>
    public async Task<bool> ReloadAsync()
    {
        if (_busy) return false;

        SetBusy(true);
        try
        {
            var rows = await _load();
            _rows = rows.Select(row => new TableRow(row, _columns)).ToList();
            FitColumns(_rows);
            ApplyFilter();
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = L.T("MainViewModel_M07", ex.Message);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenSelected()
    {
        if (Selected is TableRow row) Open(row);
    }

    private async void Open(TableRow row)
    {
        if (_open is null || _busy) return;

        try
        {
            _open(row.Source, this);
            await ReloadAsync(); // 열었던 화면에서 바뀐 상태를 반영(WPF 와 같음)
        }
        catch (Exception ex)
        {
            _status.Text = L.T("TableTab_ActionFailed", ex.Message);
        }
    }

    private async Task RunActionAsync(TableAction action, IReadOnlyDictionary<string, string>? row = null)
    {
        if (_busy) return;

        var selected = row ?? (Selected as TableRow)?.Source;
        if (action.NeedsSelection && selected is null)
        {
            _status.Text = L.T("TableTab_SelectRow");
            return;
        }

        string? result;
        SetBusy(true);
        try
        {
            result = await ActionRunner.RunAsync(action, selected, this);
        }
        catch (Exception ex)
        {
            _status.Text = L.T("TableTab_ActionFailed", ex.Message);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        if (result is null) return;

        if (await ReloadAsync()) _status.Text = result;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateActionState();
    }
}
