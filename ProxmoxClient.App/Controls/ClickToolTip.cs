using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ProxmoxClient.App.Controls;

/// <summary>
///     누르면 툴팁을 바로 띄운다 — (i) 아이콘처럼 설명을 담은 곳은 마우스를 올려 기다리지 않아도 보이게.
///     한 번 더 누르거나 마우스를 치우면 닫힌다. 사용 예: <c>ic:ClickToolTip.Enabled="True"</c>.
///     원래 툴팁(바인딩 포함)은 건드리지 않고, 누를 때의 글로 따로 띄운다 — 떠 있는 동안 마우스 툴팁은 쉰다.
/// </summary>
public static class ClickToolTip
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ClickToolTip), new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>누를 때 띄우는 툴팁(요소마다 하나).</summary>
    private static readonly DependencyProperty PopupProperty = DependencyProperty.RegisterAttached(
        "Popup", typeof(ToolTip), typeof(ClickToolTip), new PropertyMetadata(null));

    public static bool GetEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(EnabledProperty);
    }

    public static void SetEnabled(DependencyObject element, bool value)
    {
        element.SetValue(EnabledProperty, value);
    }

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement target) return;

        target.MouseLeftButtonUp -= OnClick;
        if (e.NewValue is not true) return;

        target.MouseLeftButtonUp += OnClick;
        target.Cursor = Cursors.Hand;
    }

    private static void OnClick(object sender, MouseButtonEventArgs e)
    {
        var target = (FrameworkElement)sender;
        e.Handled = true;
        if (target.GetValue(PopupProperty) is ToolTip { IsOpen: true } open)
        {
            open.IsOpen = false;
            return;
        }

        // 지금 툴팁 글(ToolTip 객체면 그 내용)을 담아 바로 띄운다
        var content = target.ToolTip is ToolTip existing ? existing.Content : target.ToolTip;
        if (content is null || content is string { Length: 0 }) return;

        var tip = new ToolTip
        {
            Content = content, PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false
        };
        tip.Closed += (_, _) => ToolTipService.SetIsEnabled(target, true);
        target.SetValue(PopupProperty, tip);
        ToolTipService.SetIsEnabled(target, false); // 마우스 툴팁과 겹쳐 두 개가 뜨지 않게
        tip.IsOpen = true;
    }
}
