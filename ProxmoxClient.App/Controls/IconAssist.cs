using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace ProxmoxClient.App.Controls;

/// <summary>
///     Button / MenuItem 에 아이콘을 붙이는 연결 속성. 테마 템플릿(DarkTheme.xaml)이 값을 읽어 <see cref="PathIcon" /> 으로 표시한다.
///     사용 예: &lt;Button ic:IconAssist.Icon="{StaticResource IconPlay}" Content="시작"/&gt;
/// </summary>
public static class IconAssist
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(IconAssist), new PropertyMetadata(null, OnIconChanged));

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

    private static void OnIconChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ButtonBase button || e.OldValue is not null) return;

        // 글자가 번역 확장·바인딩으로 늦게 채워지므로 화면에 올라온 뒤 확인한다
        button.Loaded -= EnsureToolTip;
        button.Loaded += EnsureToolTip;
    }

    private static void EnsureToolTip(object sender, RoutedEventArgs e)
    {
        // 글자가 바뀌어도(제거↔분리, 언어 전환) 따라가도록 복사하지 않고 버튼 글자에 묶는다
        if (sender is not ButtonBase { ToolTip: null } button
            || BindingOperations.IsDataBound(button, FrameworkElement.ToolTipProperty)) return;

        button.SetBinding(FrameworkElement.ToolTipProperty,
            new Binding(nameof(ContentControl.Content)) { Source = button, Converter = TextOnly.Instance });
    }

    /// <summary>글자 내용만 툴팁으로 — 아이콘·패널 같은 요소를 넘기면 부모가 둘이 되므로 null(툴팁 없음).</summary>
    private sealed class TextOnly : IValueConverter
    {
        public static readonly TextOnly Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is string { Length: > 0 } text ? text : null;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
