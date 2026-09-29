using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     표가 없는 화면(요약 탭 등) 위에 놓는 작업 버튼 줄 — 선택 행 없이 실행하고 결과를 상태 줄에 쓴다.
///     <see cref="TableAction.MenuKey" /> 가 같은 작업은 드롭다운 버튼 하나로 모아 줄을 짧게 한다.
/// </summary>
public static class ActionBar
{
    public static void Populate(Panel panel, IEnumerable<TableAction> actions, FrameworkElement host,
        TextBlock status)
    {
        var available = actions.Where(action => action.Requires is not { IsAvailable: false }).ToList();

        // 항목이 하나뿐인 메뉴는 드롭다운 대신 그냥 버튼으로 둔다
        var menus = available.Where(action => action.MenuKey is not null)
            .GroupBy(action => action.MenuKey!)
            .Where(group => group.Count() > 1)
            .ToList();
        var inMenu = menus.SelectMany(group => group).ToHashSet(ReferenceEqualityComparer.Instance);

        foreach (var action in available.Where(action => !inMenu.Contains(action)))
            panel.Children.Add(CreateButton(action, host, status));
        foreach (var menu in menus)
            panel.Children.Add(CreateMenuButton(menu.Key, menu.ToList(), host, status));
    }

    private static Button CreateButton(TableAction action, FrameworkElement host, TextBlock status)
    {
        var label = Loc.T(action.LabelKey);
        var button = new Button { Content = label, ToolTip = label, Margin = new Thickness(0, 0, 8, 4) };
        if (host.TryFindResource(action.IconKey) is Geometry icon) IconAssist.SetIcon(button, icon);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await RunAsync(action, host, status);
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    /// <summary>누르면 모은 작업들을 메뉴로 펼치는 버튼 — 툴팁에 담긴 작업 이름을 보여 준다.</summary>
    private static Button CreateMenuButton(string menuKey, IReadOnlyList<TableAction> actions,
        FrameworkElement host, TextBlock status)
    {
        var label = Loc.T(menuKey);
        var button = new Button
        {
            Content = label, Margin = new Thickness(0, 0, 8, 4),
            ToolTip = $"{label}: {string.Join(", ", actions.Select(action => Loc.T(action.LabelKey)))}"
        };
        if (host.TryFindResource("IconMore") is Geometry icon) IconAssist.SetIcon(button, icon);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var action in actions)
        {
            var item = new MenuItem { Header = Loc.T(action.LabelKey) };
            if (host.TryFindResource(action.IconKey) is Geometry itemIcon) IconAssist.SetIcon(item, itemIcon);
            item.Click += async (_, _) =>
            {
                // 느린 작업(삭제 등)을 메뉴에서 두 번 시작하지 않게 실행 중에는 드롭다운을 막는다
                button.IsEnabled = false;
                try
                {
                    await RunAsync(action, host, status);
                }
                finally
                {
                    button.IsEnabled = true;
                }
            };
            menu.Items.Add(item);
        }

        button.ContextMenu = menu;
        button.Click += (_, _) => menu.IsOpen = true;
        return button;
    }

    private static async Task RunAsync(TableAction action, FrameworkElement host, TextBlock status)
    {
        if (action.Confirm is { } confirm && !Confirm(host, confirm(null))) return;

        try
        {
            if (await action.Run(null, Window.GetWindow(host)) is { } result) status.Text = result;
        }
        catch (Exception ex)
        {
            status.Text = Loc.T("TableTab_ActionFailed", ex.Message);
        }
    }

    private static bool Confirm(FrameworkElement host, string text)
    {
        var title = Loc.T("TableTab_ConfirmTitle");
        var answer = Window.GetWindow(host) is { } owner
            ? ThemedMessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : ThemedMessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes;
    }
}
