using System.Windows;
using System.Windows.Media;

namespace ProxmoxClient.App.Controls;

/// <summary>
///     Button / MenuItem 에 아이콘을 붙이는 연결 속성. 테마 템플릿(DarkTheme.xaml)이 값을 읽어 <see cref="PathIcon" /> 으로 표시한다.
///     사용 예: &lt;Button ic:IconAssist.Icon="{StaticResource IconPlay}" Content="시작"/&gt;
/// </summary>
public static class IconAssist
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(IconAssist), new PropertyMetadata(null));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached(
        "Brush", typeof(Brush), typeof(IconAssist), new PropertyMetadata(null));

    public static Geometry? GetIcon(DependencyObject element)
    {
        return (Geometry?)element.GetValue(IconProperty);
    }

    public static void SetIcon(DependencyObject element, Geometry? value)
    {
        element.SetValue(IconProperty, value);
    }

    public static Brush? GetBrush(DependencyObject element)
    {
        return (Brush?)element.GetValue(BrushProperty);
    }

    public static void SetBrush(DependencyObject element, Brush? value)
    {
        element.SetValue(BrushProperty, value);
    }
}