using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     표 위 버튼 줄 — <see cref="TableAction.MenuKey" /> 가 같은 동작은 드롭다운 버튼 하나(예: 더보기 ▾)로 묶는다.
///     드롭다운은 처음 묶인 동작의 자리에 놓고, 메뉴 항목은 펼칠 때 선택 상태에 맞춰 켜고 끈다.
/// </summary>
public partial class TableTab
{
    /// <summary>
    ///     다른 화면 안에 끼워 넣는 표(예: 게스트 방화벽 규칙) — 바깥 화면이 이미 여백을 두므로 표 자체 여백을 뺀다.
    /// </summary>
    public TableTab Embedded()
    {
        if (Content is FrameworkElement root) root.Margin = new Thickness(0);
        return this;
    }

    /// <summary>글자 버튼이 이보다 많으면 앞의 <see cref="KeepVisibleActions" />개만 두고 나머지는 '더보기 ▾' 로 묶는다.</summary>
    private const int MaxLooseActions = 5;

    private const int KeepVisibleActions = 4;

    private const string AutoMoreMenu = "GuestLife_MoreMenu";

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

            AddActionButton(action);
        }
    }

    /// <summary>
    ///     버튼 줄이 두 줄로 넘어가지 않게 — 따로 묶이지 않은 글자 버튼이 많으면 앞의 몇 개(보통 추가·편집·삭제 등 주 동작)만
    ///     남기고 나머지는 '더보기 ▾' 에 넣는다. 아이콘만 있는 버튼(위로·아래로)은 자리를 적게 차지하므로 그대로 둔다.
    /// </summary>
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
        var label = Loc.T(menuKey);
        var button = new Button
        {
            Content = label, Margin = new Thickness(0, 0, 8, 4),
            ToolTip = $"{label}: {string.Join(", ", items.Select(item => Loc.T(item.LabelKey)))}"
        };
        if (TryFindResource("IconMore") is Geometry icon) IconAssist.SetIcon(button, icon);
        IconAssist.SetDropdown(button, true);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var entries = items.Select(action => (Item: MenuItemFor(action), Action: action)).ToList();
        foreach (var (item, _) in entries) menu.Items.Add(item);
        menu.Opened += (_, _) =>
        {
            var hasSelection = TableGrid.SelectedItem is TableRow;
            foreach (var (item, action) in entries) item.IsEnabled = !action.NeedsSelection || hasSelection;
        };
        button.ContextMenu = menu;
        button.Click += (_, _) => menu.IsOpen = true;
        ActionPanel.Children.Add(button);
        _actions.Add((button, null));
    }

    private MenuItem MenuItemFor(TableAction action)
    {
        var item = new MenuItem { Header = Loc.T(action.LabelKey) };
        if (TryFindResource(action.IconKey) is Geometry icon) IconAssist.SetIcon(item, icon);
        item.Click += async (_, _) => await RunActionAsync(action);
        return item;
    }
}
