using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Views.Shared;

/// <summary>작업 버튼 줄(표가 아닌 화면용) — WPF ActionBar 와 같은 규칙(같은 MenuKey 는 한 메뉴로).</summary>
public static class ActionBar
{
    public static void Populate(Panel panel, IEnumerable<TableAction> actions, Control host, TextBlock status)
    {
        var available = actions.Where(action => action.Requires is not { IsAvailable: false }).ToList();
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

    internal static Button IconButton(string label, string iconKey, Control host, bool iconOnly = false)
    {
        var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        // 아직 화면에 붙지 않은 컨트롤은 앱 리소스까지 찾아 올라가지 못하므로 앱 리소스에서도 찾는다
        if ((host.TryFindResource(iconKey, out var icon) ||
             Application.Current?.TryGetResource(iconKey, Application.Current.ActualThemeVariant, out icon) == true) &&
            icon is Geometry geometry)
            content.Children.Add(new Avalonia.Controls.Shapes.Path { Data = geometry, Classes = { "icon" }, Width = 16, Height = 16 });
        if (!iconOnly) content.Children.Add(new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var button = new Button { Content = content, Margin = new Thickness(0, 0, 8, 6), MinHeight = 36 };
        ToolTip.SetTip(button, label);
        return button;
    }

    private static Button CreateButton(TableAction action, Control host, TextBlock status)
    {
        var button = IconButton(Loc.T(action.LabelKey), action.IconKey, host, action.IconOnly);
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

    private static Button CreateMenuButton(string menuKey, IReadOnlyList<TableAction> actions, Control host,
        TextBlock status)
    {
        var button = IconButton(Loc.T(menuKey), "IconMore", host);
        var menu = new MenuFlyout();
        foreach (var action in actions)
        {
            var item = new MenuItem { Header = Loc.T(action.LabelKey) };
            item.Click += async (_, _) =>
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
            menu.Items.Add(item);
        }

        button.Flyout = menu;
        return button;
    }

    private static async Task RunAsync(TableAction action, Control host, TextBlock status)
    {
        try
        {
            if (await ActionRunner.RunAsync(action, null, host) is { } result) status.Text = result;
        }
        catch (Exception ex)
        {
            status.Text = Loc.T("TableTab_ActionFailed", ex.Message);
        }
    }
}
