using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ProxmoxClient.App.Controls;

/// <summary>창 머리말에 새로고침 버튼을 두는 창(게스트·노드·데이터센터 창) — 탭 안의 새로고침 버튼은 숨긴다.</summary>
public interface IRefreshHost;

/// <summary>
///     탭 안의 새로고침 버튼 표시 — <c>ic:RefreshButton.IsRefresh="True"</c>.
///     <see cref="IRefreshHost" /> 창 안에서는 버튼을 숨기고, 창 머리말의 새로고침(F5)이 지금 보이는 탭의 이 버튼들을 대신 누른다.
///     머리말이 없는 창(표 창 등)에서는 버튼을 그대로 둔다.
/// </summary>
public static class RefreshButton
{
    public static readonly DependencyProperty IsRefreshProperty = DependencyProperty.RegisterAttached(
        "IsRefresh", typeof(bool), typeof(RefreshButton), new PropertyMetadata(false, OnIsRefreshChanged));

    public static bool GetIsRefresh(DependencyObject element)
    {
        return (bool)element.GetValue(IsRefreshProperty);
    }

    public static void SetIsRefresh(DependencyObject element, bool value)
    {
        element.SetValue(IsRefreshProperty, value);
    }

    /// <summary>
    ///     <paramref name="root" /> 아래에서 지금 화면에 올라와 있는 탭의 새로고침을 모두 누른다(숨긴 하위 탭은 건너뛴다).
    ///     누른 것이 있으면 true.
    /// </summary>
    public static bool RefreshVisible(DependencyObject root)
    {
        var pressed = false;
        foreach (var button in Descendants(root).OfType<ButtonBase>())
        {
            // 버튼 자신은 숨겨 두었으므로 그 부모가 보이는지로 "지금 보이는 탭" 을 가린다
            if (!GetIsRefresh(button) || !button.IsEnabled
                || VisualTreeHelper.GetParent(button) is not UIElement { IsVisible: true }) continue;

            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            pressed = true;
        }

        return pressed;
    }

    private static void OnIsRefreshChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is FrameworkElement button && e.NewValue is true) button.Loaded += HideInsideHost;
    }

    private static void HideInsideHost(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        if (Window.GetWindow(button) is IRefreshHost) button.Visibility = Visibility.Collapsed;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
