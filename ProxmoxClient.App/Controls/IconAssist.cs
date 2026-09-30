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

    /// <summary>누르면 메뉴가 펼쳐지는 버튼 — 글자 뒤에 아래 화살표 아이콘을 붙인다.</summary>
    public static readonly DependencyProperty DropdownProperty = DependencyProperty.RegisterAttached(
        "Dropdown", typeof(bool), typeof(IconAssist), new PropertyMetadata(false));

    /// <summary>
    ///     버튼 모서리 — 나뉜 버튼([종료 | ▾])은 맞붙는 쪽을 각지게 둔다(왼쪽 "4,0,0,4", 오른쪽 "0,4,4,0").
    /// </summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(IconAssist), new PropertyMetadata(new CornerRadius(4)));

    /// <summary>마우스를 올렸을 때 버튼 배경·테두리 — 강조 버튼은 파란색을 밝게, 일반 버튼은 회색에 파란 테두리.</summary>
    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(IconAssist), new PropertyMetadata(null));

    public static readonly DependencyProperty HoverBorderProperty = DependencyProperty.RegisterAttached(
        "HoverBorder", typeof(Brush), typeof(IconAssist), new PropertyMetadata(null));

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

    public static bool GetDropdown(DependencyObject element)
    {
        return (bool)element.GetValue(DropdownProperty);
    }

    public static void SetDropdown(DependencyObject element, bool value)
    {
        element.SetValue(DropdownProperty, value);
    }

    public static CornerRadius GetCornerRadius(DependencyObject element)
    {
        return (CornerRadius)element.GetValue(CornerRadiusProperty);
    }

    public static void SetCornerRadius(DependencyObject element, CornerRadius value)
    {
        element.SetValue(CornerRadiusProperty, value);
    }

    public static Brush? GetHoverBackground(DependencyObject element)
    {
        return (Brush?)element.GetValue(HoverBackgroundProperty);
    }

    public static void SetHoverBackground(DependencyObject element, Brush? value)
    {
        element.SetValue(HoverBackgroundProperty, value);
    }

    public static Brush? GetHoverBorder(DependencyObject element)
    {
        return (Brush?)element.GetValue(HoverBorderProperty);
    }

    public static void SetHoverBorder(DependencyObject element, Brush? value)
    {
        element.SetValue(HoverBorderProperty, value);
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
