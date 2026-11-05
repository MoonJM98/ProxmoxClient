using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;

namespace ProxmoxClient.UI.Views.Shared;

/// <summary>
///     서버 목록 하나를 보여 주는 표 — WPF TableTab 과 같은 생성자(불러오기·열·안내·작업·필터·열기).
///     데스크톱: 머리글이 있는 표. 모바일: 행마다 카드(첫 열이 제목, 나머지는 "이름 값" 줄).
///     작업 버튼은 위에 두고, 선택이 필요한 작업은 행을 고른 뒤에 눌린다.
/// </summary>
public sealed class TableTab : UserControl
{
    private const int MaxLooseActions = 5;
    private const int KeepVisibleActions = 4;
    private const string AutoMoreMenu = "GuestLife_MoreMenu";

    private readonly List<(Button Button, TableAction Action)> _actions = [];
    private readonly IReadOnlyList<TableColumn> _columns;
    private readonly Func<Task<IReadOnlyList<IReadOnlyDictionary<string, string>>>> _load;
    private readonly Action<IReadOnlyDictionary<string, string>, Control?>? _open;
    private readonly ListBox _list = new() { Background = Brushes.Transparent };
    private readonly TextBlock _status = new() { Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
    private readonly TextBox _filter = new() { MinWidth = 180 };
    private readonly WrapPanel _actionPanel = new();
    private readonly Border _root;
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

        _filter.PlaceholderText = L.T("TableTab_Filter");
        _filter.IsVisible = filterable;
        _filter.TextChanged += (_, _) => ApplyFilter();

        AddActions((actions ?? []).Where(a => a.Requires is not { IsAvailable: false }).ToList());
        var refresh = ActionBar.IconButton(L.T("Common_Refresh"), "IconRefresh", this, iconOnly: true);
        refresh.Click += async (_, _) => await ReloadAsync();
        _actionPanel.Children.Add(refresh);
        _actionPanel.Children.Add(_filter);

        _list.SelectionChanged += (_, _) => UpdateActionState();
        _list.DoubleTapped += async (_, _) => await OpenSelectedAsync();
        _list.ItemTemplate = PlatformInfo.IsDesktop ? DesktopRowTemplate() : MobileRowTemplate();

        var hint = new TextBlock
        {
            Text = L.T(hintKey), Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None, IsVisible = L.Has(hintKey)
        };
        Control body = PlatformInfo.IsDesktop
            ? new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new DockPanel { Children = { Top(HeaderRow()), _list } }
            }
            : _list;

        _root = new Border
        {
            Padding = new Thickness(12),
            Child = new DockPanel
            {
                Children =
                {
                    Top(new StackPanel { Spacing = 6, Children = { _actionPanel, hint, _status } }),
                    body
                }
            }
        };
        Content = _root;
        UpdateActionState();
        AttachedToVisualTree += async (_, _) =>
        {
            if (_loaded) return;

            _loaded = true;
            await ReloadAsync();
        };
    }

    /// <summary>다른 화면 안에 넣을 때 바깥 여백을 없앤다(WPF 와 같은 이름).</summary>
    public TableTab Embedded()
    {
        _root.Padding = new Thickness(0);
        return this;
    }

    private static T Top<T>(T control) where T : Control
    {
        DockPanel.SetDock(control, Dock.Top);
        control.Margin = new Thickness(control.Margin.Left, 0, 0, 8);
        return control;
    }

    private static double ColumnWidth(TableColumn column) => column.Width <= 0 ? 220 : column.Width;

    private Control HeaderRow()
    {
        var grid = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) }; // 목록 행 안쪽 여백과 맞춘다
        foreach (var column in _columns)
            grid.Children.Add(new TextBlock
            {
                Text = L.T(column.HeaderKey), Width = ColumnWidth(column), FontWeight = FontWeight.SemiBold,
                Classes = { "subtle" }, Margin = new Thickness(0, 0, 8, 0)
            });
        return grid;
    }

    private IDataTemplate DesktopRowTemplate()
    {
        return new FuncDataTemplate<TableRow>((row, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            if (row is null) return panel;

            foreach (var column in _columns)
                panel.Children.Add(new TextBlock
                {
                    Text = CellText(row, column), Width = ColumnWidth(column), TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
                });
            return panel;
        });
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
                var text = CellText(row, column);
                if (i == 0)
                {
                    panel.Children.Add(new TextBlock { Text = text, Classes = { "title" }, FontSize = 15 });
                    continue;
                }

                if (text.Length == 0) continue;

                panel.Children.Add(new TextBlock
                {
                    Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
                    Text = L.T(column.HeaderKey) + "  " + text
                });
            }

            return panel;
        });
    }

    private static string CellText(TableRow row, TableColumn column)
    {
        var text = row[column.Key];
        return TableFormats.IsCheck(column.Format) ? text == TableFormats.CheckValue ? "✓" : string.Empty : text;
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

            var button = ActionBar.IconButton(L.T(action.LabelKey), action.IconKey, this, action.IconOnly);
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

    private void AddMenuButton(string menuKey, IReadOnlyList<TableAction> items)
    {
        var button = ActionBar.IconButton(L.T(menuKey), "IconMore", this);
        var menu = new MenuFlyout();
        foreach (var action in items)
        {
            var item = new MenuItem { Header = L.T(action.LabelKey) };
            item.Click += async (_, _) => await RunActionAsync(action);
            menu.Items.Add(item);
            _actions.Add((button, action));
        }

        button.Flyout = menu;
        _actionPanel.Children.Add(button);
    }

    private void UpdateActionState()
    {
        var hasSelection = _list.SelectedItem is TableRow;
        foreach (var (button, action) in _actions)
            button.IsEnabled = !_busy && (!action.NeedsSelection || hasSelection ||
                                          _actions.Count(a => a.Button == button) > 1);
    }

    private void ApplyFilter()
    {
        var text = _filter.Text?.Trim();
        var visible = string.IsNullOrEmpty(text)
            ? _rows
            : _rows.Where(row => _columns.Any(c => row[c.Key].Contains(text, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        _list.ItemsSource = visible;
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

    private async Task OpenSelectedAsync()
    {
        if (_open is null || _busy || _list.SelectedItem is not TableRow row) return;

        try
        {
            _open(row.Source, this);
        }
        catch (Exception ex)
        {
            _status.Text = L.T("TableTab_ActionFailed", ex.Message);
        }

        await Task.CompletedTask;
    }

    private async Task RunActionAsync(TableAction action)
    {
        if (_busy) return;

        var selected = (_list.SelectedItem as TableRow)?.Source;
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
