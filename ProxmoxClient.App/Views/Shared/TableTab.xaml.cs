using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     서버 목록을 표로 보여 주는 범용 탭 — 네트워크·디스크·사용자처럼 필드만 다른 화면을 한 틀로 만든다.
/// </summary>
public partial class TableTab : UserControl
{
    /// <summary>버튼과 그 동작 — 메뉴로 묶은 드롭다운 버튼은 Action 이 null(바쁠 때만 막는다).</summary>
    private readonly List<(Button Button, TableAction? Action)> _actions = [];
    private readonly IReadOnlyList<TableColumn> _columns;
    private readonly Func<Task<IReadOnlyList<IReadOnlyDictionary<string, string>>>> _load;
    private readonly Action<IReadOnlyDictionary<string, string>, Window?>? _open;
    private bool _busy;

    /// <param name="open">행을 두 번 누르면 부를 동작(예: 해당 리소스 창 열기). 없으면 두 번 눌러도 아무 일 없다.</param>
    public TableTab(
        Func<Task<IReadOnlyList<IReadOnlyDictionary<string, string>>>> load,
        IReadOnlyList<TableColumn> columns,
        string hintKey,
        IReadOnlyList<TableAction>? actions = null,
        bool filterable = false,
        Action<IReadOnlyDictionary<string, string>, Window?>? open = null)
    {
        InitializeComponent();
        _load = load;
        _open = open;
        _columns = columns;
        HintText.Text = Loc.T(hintKey);
        FilterPanel.Visibility = filterable ? Visibility.Visible : Visibility.Collapsed;

        foreach (var column in columns) TableGrid.Columns.Add(CreateColumn(column));

        // 서버 버전이 못 쓰는 기능의 버튼은 두지 않는다(버튼마다 Requires 로 알린다)
        AddActions((actions ?? []).Where(a => a.Requires is not { IsAvailable: false }).ToList());
        // 새로고침·필터는 작업 버튼 뒤에 이어 놓는다 — 버튼이 두 줄로 넘어가도 새로고침이 세로로 늘어나지 않게
        if (BtnRefresh.Parent is Panel toolbar)
        {
            toolbar.Children.Remove(BtnRefresh);
            toolbar.Children.Remove(FilterPanel);
            ActionPanel.Children.Add(BtnRefresh);
            ActionPanel.Children.Add(FilterPanel);
        }

        UpdateActionState();
        Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>자동 너비 열의 상한 — 이보다 긴 값(긴 설명·경로 등)은 말줄임(…)으로 줄인다.</summary>
    private const double MaxAutoColumnWidth = 400;

    /// <summary>채움 열(너비 0)이 가로 스크롤 때 줄어드는 하한.</summary>
    private const double MinFillColumnWidth = 140;

    /// <summary>
    ///     글자 열, 켜짐 표시 열(<see cref="TableFormats.IsCheck" />)은 체크 아이콘 열.
    ///     정한 너비는 처음 모양 — 데이터가 오면 <see cref="FitColumns" /> 가 내용에 맞춰 넓힌다(넘치면 가로 스크롤).
    /// </summary>
    private static DataGridColumn CreateColumn(TableColumn column)
    {
        var path = $"[{column.Key}]";
        var fill = column.Width <= 0;
        var width = fill
            ? new DataGridLength(1, DataGridLengthUnitType.Star)
            : new DataGridLength(column.Width);
        if (!TableFormats.IsCheck(column.Format))
            return new DataGridTextColumn
            {
                Header = Loc.T(column.HeaderKey), Width = width, MinWidth = fill ? MinFillColumnWidth : 0,
                Binding = new Binding(path) { Mode = BindingMode.OneWay },
                ElementStyle = TrimmedCell
            };

        var icon = new FrameworkElementFactory(typeof(PathIcon));
        icon.SetValue(WidthProperty, 14.0);
        icon.SetValue(HeightProperty, 14.0);
        icon.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        icon.SetValue(MarginProperty, new Thickness(4, 0, 0, 0));
        icon.SetValue(PathIcon.DataProperty, Application.Current.TryFindResource("IconCheck"));
        icon.SetValue(PathIcon.IconBrushProperty, Application.Current.TryFindResource("BrushOk"));
        icon.SetBinding(VisibilityProperty,
            new Binding(path) { Mode = BindingMode.OneWay, Converter = new CheckVisibility() });
        return new DataGridTemplateColumn
        {
            Header = Loc.T(column.HeaderKey), Width = width, SortMemberPath = path,
            CellTemplate = new DataTemplate { VisualTree = icon }
        };
    }

    /// <summary>상한을 넘는 칸은 말줄임(…) — 전체 값은 칸에 마우스를 올리면 툴팁으로 본다.</summary>
    private static readonly Style TrimmedCell = CreateTrimmedCell();

    private static Style CreateTrimmedCell()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
            new Binding(nameof(TextBlock.Text)) { RelativeSource = RelativeSource.Self }));
        style.Setters.Add(new Setter(ToolTipService.IsEnabledProperty,
            new Binding(nameof(TextBlock.Text))
            {
                RelativeSource = RelativeSource.Self, Converter = NonEmpty.Instance
            }));
        style.Seal();
        return style;
    }

    /// <summary>빈 칸에는 툴팁을 띄우지 않는다.</summary>
    private sealed class NonEmpty : IValueConverter
    {
        public static readonly NonEmpty Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is string { Length: > 0 };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>켜짐 표시 값이면 아이콘을 보인다.</summary>
    private sealed class CheckVisibility : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value as string == TableFormats.CheckValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    private void AddActionButton(TableAction action)
    {
        var label = Loc.T(action.LabelKey);
        // 아이콘만 두는 버튼(위로·아래로 등)은 글자 대신 툴팁으로 이름을 보인다
        var button = new Button
        {
            Content = action.IconOnly ? null : label, ToolTip = label, Margin = new Thickness(0, 0, 8, 4),
            Padding = action.IconOnly ? new Thickness(8, 6, 8, 6) : new Thickness(12, 6, 12, 6)
        };
        if (TryFindResource(action.IconKey) is Geometry icon) IconAssist.SetIcon(button, icon);
        button.Click += async (_, _) => await RunActionAsync(action);
        ActionPanel.Children.Add(button);
        _actions.Add((button, action));
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await ReloadAsync();
    }

    /// <summary>행 위에서 두 번 눌렀을 때만 연다(머리글·빈 곳·스크롤바는 무시).</summary>
    private async void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_open is null || _busy
            || (e.OriginalSource as FrameworkElement)?.DataContext is not TableRow row)
            return;

        try
        {
            _open(row.Source, Window.GetWindow(this));
            await ReloadAsync(); // 열었던 창에서 바뀐 상태를 반영
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("TableTab_ActionFailed", ex.Message);
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (TableGrid.ItemsSource is not IReadOnlyCollection<TableRow> rows) return;

        var view = CollectionViewSource.GetDefaultView(rows);
        view.Refresh();
        StatusText.Text = CountText(view.Cast<object>().Count(), rows.Count, FilterBox.Text);
    }

    /// <summary>상태줄 건수 — 필터가 걸려 있으면 "전체 중 보이는 수"를 보인다.</summary>
    internal static string CountText(int visible, int total, string? filter)
    {
        if (total == 0) return Loc.T("TableTab_Empty");
        return string.IsNullOrWhiteSpace(filter)
            ? Loc.T("TableTab_Count", total)
            : Loc.T("TableTab_FilteredCount", visible, total);
    }

    /// <summary>보이는 열 값 중 하나라도 필터 글자를 포함하면(대소문자 무시) 남긴다. 빈 필터는 모두 남긴다.</summary>
    internal static bool Matches(TableRow row, IReadOnlyList<TableColumn> columns, string? filter)
    {
        var text = filter?.Trim();
        return string.IsNullOrEmpty(text)
               || columns.Any(c => row[c.Key].Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        var hasSelection = TableGrid.SelectedItem is TableRow;
        foreach (var (button, action) in _actions)
            button.IsEnabled = !_busy && (action is not { NeedsSelection: true } || hasSelection);
    }

    /// <summary>목록을 다시 읽는다 — 실패하면 상태줄에 오류를 두고 false.</summary>
    private async Task<bool> ReloadAsync()
    {
        if (_busy) return false;

        SetBusy(true);
        try
        {
            var rows = await _load();
            var tableRows = rows.Select(row => new TableRow(row, _columns)).ToList();
            FitColumns(tableRows);
            TableGrid.ItemsSource = tableRows;
            var view = CollectionViewSource.GetDefaultView(TableGrid.ItemsSource);
            view.Filter = item => item is TableRow row && Matches(row, _columns, FilterBox.Text);
            StatusText.Text = CountText(view.Cast<object>().Count(), rows.Count, FilterBox.Text);
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("MainViewModel_M07", ex.Message);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunActionAsync(TableAction action)
    {
        if (_busy) return;

        var selected = (TableGrid.SelectedItem as TableRow)?.Source;
        if (action.NeedsSelection && selected is null) return;
        if (action.Confirm is { } confirm && !Confirm(confirm(selected))) return;

        string? result;
        SetBusy(true);
        try
        {
            result = await action.Run(selected, Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("TableTab_ActionFailed", ex.Message);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        if (result is null) return;

        if (await ReloadAsync()) StatusText.Text = result;
    }

    private bool Confirm(string text)
    {
        var title = Loc.T("TableTab_ConfirmTitle");
        var answer = Window.GetWindow(this) is { } owner
            ? ThemedMessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : ThemedMessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateActionState();
    }
}
