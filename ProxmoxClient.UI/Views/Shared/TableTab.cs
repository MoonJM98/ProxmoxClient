using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Views.Controls;

namespace ProxmoxClient.UI.Views.Shared;

/// <summary>
///     서버 목록 하나를 보여 주는 표 — WPF TableTab 과 같은 생성자·같은 배치(위: 작업 버튼 · 새로고침 · 필터 /
///     가운데: 표 / 아래: 상태 줄). 데스크톱은 WPF 와 같은 DataGrid(열 폭 자동 맞춤·정렬·더블클릭 열기),
///     모바일은 같은 행을 카드로(첫 열이 제목, 나머지는 "이름 값" 줄) 보여 준다.
/// </summary>
public sealed class TableTab : UserControl
{
    private const int MaxLooseActions = 5;
    private const int KeepVisibleActions = 4;
    private const string AutoMoreMenu = "GuestLife_MoreMenu";
    private const double MaxAutoColumnWidth = 400;
    private const double MinFillColumnWidth = 140;
    private const double CellPadding = 44; // 칸 여백 + 머리글 정렬 표시

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

        // 위 줄: 작업 버튼 → 새로고침(아이콘만) → 필터 — WPF 와 같은 순서
        AddActions((actions ?? []).Where(a => a.Requires is not { IsAvailable: false }).ToList());
        var refresh = IconButton(null, "IconRefresh");
        ToolTip.SetTip(refresh, L.T("MainWindow_03"));
        refresh.Padding = new Thickness(8, 6);
        refresh.Click += async (_, _) => await ReloadAsync();
        ToolTip.SetTip(_filter, L.T("TableTab_Filter"));
        _filter.TextChanged += (_, _) => ApplyFilter();
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
        var hint = new TextBlock
        {
            Text = L.T(hintKey), Foreground = Brush("BrushDim"), TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 4), IsVisible = L.Has(hintKey)
        };
        var left = new WrapPanel { Children = { _actionPanel, refresh, filterPanel } };
        Control top;
        if (PlatformInfo.IsDesktop)
        {
            // 데스크톱: 버튼 뒤 남은 자리에 설명(WPF 와 같음)
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(left, Dock.Left);
            dock.Children.Add(left);
            dock.Children.Add(hint);
            top = dock;
        }
        else
        {
            // 모바일: 폭이 좁아 설명은 버튼 아래 줄로
            top = new StackPanel { Margin = new Thickness(0, 0, 0, 6), Children = { left, hint } };
        }

        Control body;
        if (PlatformInfo.IsDesktop)
        {
            _grid = new DataGrid { CanUserSortColumns = true };
            foreach (var column in columns) _grid.Columns.Add(CreateColumn(column));
            _grid.SelectionChanged += (_, _) => UpdateActionState();
            _grid.DoubleTapped += (_, _) => OpenSelected();
            _grid.FontSize = 13;
            body = _grid;
        }
        else
        {
            _cards = new ListBox { ItemTemplate = MobileRowTemplate() };
            _cards.SelectionChanged += (_, _) => UpdateActionState();
            _cards.DoubleTapped += (_, _) => OpenSelected();
            body = _cards;
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
        if (label is not null) ToolTip.SetTip(button, label);
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

        var face = new Typeface(FontFamily.Default);
        var headerFace = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        for (var i = 0; i < _columns.Count && i < _grid.Columns.Count; i++)
        {
            var column = _columns[i];
            if (column.Width <= 0) continue;

            var widest = TextWidth(L.T(column.HeaderKey), headerFace);
            foreach (var row in rows) widest = Math.Max(widest, TextWidth(row[column.Key], face));
            _grid.Columns[i].Width = new DataGridLength(
                Math.Clamp(widest + CellPadding, column.Width, Math.Max(column.Width, MaxAutoColumnWidth)));
        }
    }

    private static double TextWidth(string text, Typeface face)
    {
        return text.Length == 0
            ? 0
            : new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 13, Brushes.Black)
                .WidthIncludingTrailingWhitespace;
    }

    private IDataTemplate MobileRowTemplate()
    {
        return new FuncDataTemplate<TableRow>((row, _) =>
        {
            var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4) };
            if (row is null) return panel;

            for (var i = 0; i < _columns.Count; i++)
            {
                var column = _columns[i];
                var text = TableFormats.IsCheck(column.Format)
                    ? row[column.Key] == TableFormats.CheckValue ? "✓" : string.Empty
                    : row[column.Key];
                if (i == 0)
                {
                    panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 14 });
                    continue;
                }

                if (text.Length == 0) continue;

                panel.Children.Add(new TextBlock
                {
                    Text = L.T(column.HeaderKey) + "  " + text, Foreground = Brush("BrushDim"),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            return panel;
        });
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
        var loose = actions.Where(action => action.MenuKey is null && !action.IconOnly).ToList();
        if (loose.Count <= MaxLooseActions) return actions;

        var overflow = loose.Skip(KeepVisibleActions).ToHashSet(ReferenceEqualityComparer.Instance);
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
        if (_open is null || _busy || Selected is not TableRow row) return;

        try
        {
            _open(row.Source, this);
        }
        catch (Exception ex)
        {
            _status.Text = L.T("TableTab_ActionFailed", ex.Message);
        }
    }

    private async Task RunActionAsync(TableAction action)
    {
        if (_busy) return;

        var selected = (Selected as TableRow)?.Source;
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
