using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;

namespace ProxmoxClient.UI.Views.Login;

/// <summary>
///     화면 설명(WPF ic:InfoTip) — 받은 폭에 글이 한 줄로 다 들어가면 흐린 글자로 보이고, 모자라면 (i) 아이콘만 두고
///     올리면 툴팁으로 전체 글을 보여 준다. 글이 비어 있으면 아무것도 보이지 않는다.
/// </summary>
public sealed class InfoTip : Panel
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<InfoTip, string?>(nameof(Text));

    private const double IconSize = 16;

    private static readonly IBrush DimBrush = new SolidColorBrush(Color.Parse("#9E9E9E"));

    // Icons.axaml 의 IconInfo 와 같은 그림
    private static readonly Geometry InfoGeometry = StreamGeometry.Parse(
        "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 Z M12,16 V12 M12,8 H12.01");

    private readonly TextBlock _text = new()
        { FontSize = 11, Foreground = DimBrush, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

    private readonly Avalonia.Controls.Shapes.Path _icon = new()
    {
        Width = IconSize, Height = IconSize, Stretch = Stretch.Uniform, Data = InfoGeometry,
        Stroke = DimBrush, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round
    };

    private bool _showText;

    public InfoTip()
    {
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Margin = new Thickness(4, 0, 8, 0);
        Children.Add(_text);
        Children.Add(_icon);
        IsVisible = false;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty) return;

        _text.Text = Text ?? string.Empty;
        IsVisible = !string.IsNullOrEmpty(Text);
        InvalidateMeasure();
    }

    /// <summary>받은 폭에 글이 한 줄로 들어가면 글자, 아니면 아이콘.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _icon.Measure(availableSize);
        _showText = _text.DesiredSize.Width <= availableSize.Width;
        // 보이지 않는 쪽은 그리기만 끈다(측정 중에 IsVisible 을 바꾸면 배치를 다시 부른다)
        _text.Opacity = _showText ? 1 : 0;
        _icon.Opacity = _showText ? 0 : 1;
        ToolTip.SetTip(this, _showText || string.IsNullOrEmpty(Text) ? null : Text);
        Cursor = _showText ? null : new Cursor(StandardCursorType.Hand);
        return _showText ? _text.DesiredSize : _icon.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_showText) _text.Arrange(new Rect(new Point(0, 0), _text.DesiredSize));
        else _icon.Arrange(new Rect(new Point(0, 0), _icon.DesiredSize));
        return finalSize;
    }
}
