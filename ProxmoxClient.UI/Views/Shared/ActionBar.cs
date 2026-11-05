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

    /// <summary>WPF 와 같은 아이콘 버튼(IconAssist — 아이콘 + 글자, 아이콘만이면 글자 없이 툴팁).</summary>
    internal static Button IconButton(string label, string iconKey, Control host, bool iconOnly = false)
    {
        var button = new Button
        {
            Content = iconOnly ? null : label, Margin = new Thickness(0, 0, 8, 4),
            Padding = iconOnly ? new Thickness(8, 6) : new Thickness(12, 6)
        };
        if ((host.TryFindResource(iconKey, out var icon) ||
             Application.Current?.TryGetResource(iconKey, Application.Current.ActualThemeVariant, out icon) == true) &&
            icon is Geometry geometry)
            Controls.IconAssist.SetIcon(button, geometry);
        ToolTip.SetTip(button, label);
        Controls.CompactButton.Apply(button, label); // 좁은 화면: 아이콘만, 길게 누르면 이름
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
        Controls.IconAssist.SetDropdown(button, true);
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
