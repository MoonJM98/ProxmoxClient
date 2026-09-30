using System.Windows;
using System.Windows.Controls;

namespace ProxmoxClient.App.Controls;

/// <summary>
///     메뉴가 열릴 때마다 구분선을 정리한다 — 사이 항목이 모두 숨겨져(권한·전원 상태) 구분선이 겹치거나
///     맨 앞·맨 뒤에 남으면 숨긴다. 앱 전체의 ContextMenu·하위 메뉴에 한 번 등록한다.
/// </summary>
internal static class MenuSeparators
{
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => Tidy(((ItemsControl)sender).Items)));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent,
            new RoutedEventHandler((sender, e) =>
            {
                if (ReferenceEquals(sender, e.OriginalSource)) Tidy(((ItemsControl)sender).Items);
            }));
    }

    /// <summary>보이는 항목 사이에 있는 구분선만 남긴다(연속이면 하나만).</summary>
    internal static void Tidy(ItemCollection items)
    {
        Separator? pending = null;
        var seenItem = false;
        foreach (var element in items.OfType<UIElement>())
        {
            if (element is Separator separator)
            {
                separator.Visibility = Visibility.Collapsed;
                if (seenItem && pending is null) pending = separator;
                continue;
            }

            if (element.Visibility != Visibility.Visible) continue;
            if (pending is not null) pending.Visibility = Visibility.Visible;
            pending = null;
            seenItem = true;
        }
    }
}
