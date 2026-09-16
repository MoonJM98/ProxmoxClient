using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace ProxmoxClient.App.Controls;

/// <summary>아이콘 + 텍스트 묶음(탭 헤더 등 템플릿 지원이 없는 곳에서 Content 로 사용).</summary>
public sealed class IconLabel : StackPanel
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(IconLabel),
        new PropertyMetadata(null, (d, e) => ((IconLabel)d)._icon.Data = e.NewValue as Geometry));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(IconLabel),
        new PropertyMetadata(string.Empty, (d, e) => ((IconLabel)d)._text.Text = e.NewValue as string ?? string.Empty));

    private readonly PathIcon _icon = new() { Margin = new Thickness(0, 0, 7, 0) };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public IconLabel()
    {
        Orientation = Orientation.Horizontal;
        // 전역 TextBlock 스타일 대신 부모(ListBoxItem 등)의 Foreground 를 따르도록 바인딩
        _text.SetBinding(TextBlock.ForegroundProperty, new Binding
        {
            Source = this,
            Path = new PropertyPath(TextElement.ForegroundProperty)
        });
        Children.Add(_icon);
        Children.Add(_text);
    }

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}