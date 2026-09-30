using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProxmoxClient.App.Controls;

/// <summary>
///     화면 설명 — 받은 자리에 글이 한 줄로 다 들어가면 흐린 글자로 그대로 보이고, 모자라면 (i) 아이콘으로 줄여
///     툴팁으로 보여 준다(입력 칸 라벨 옆처럼 늘 좁은 곳은 FormRow 의 Hint 가 아이콘만 쓴다).
///     사용 예: &lt;ic:InfoTip Text="{loc:Tr SomeHint}" /&gt; — 글이 비어 있으면 아무것도 보이지 않는다.
///     폭이 정해지지 않는 곳(StackPanel 가로·WrapPanel)에서는 늘 글자로 보이므로 DockPanel 의 채움 칸 등에 둔다.
/// </summary>
public sealed class InfoTip : FrameworkElement
{
    private const double IconSize = 16;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(InfoTip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, e) => ((InfoTip)d).Apply(e.NewValue as string)));

    /// <summary>늘 아이콘으로 — 저장 버튼 옆처럼 상태 문구와 자리를 나눠 쓰는 곳.</summary>
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(InfoTip),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private readonly Border _icon;
    private readonly TextBlock _text;
    private readonly VisualCollection _children;
    private bool _showText;

    public InfoTip()
    {
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(4, 0, 8, 0);
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetShowDuration(this, 60000);
        ClickToolTip.SetEnabled(this, true); // 아이콘을 누르면 설명이 바로 뜬다

        var dim = Application.Current?.TryFindResource("BrushDim") as Brush;
        _icon = new Border
        {
            Background = Brushes.Transparent, // 아이콘 선 사이 빈 곳에서도 툴팁이 뜨게
            Child = new PathIcon
            {
                Width = IconSize, Height = IconSize,
                Data = Application.Current?.TryFindResource("IconInfo") as Geometry, IconBrush = dim
            }
        };
        _text = new TextBlock { FontSize = 11, Foreground = dim, VerticalAlignment = VerticalAlignment.Center };
        _children = new VisualCollection(this) { _icon, _text };
        Apply(null);
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index)
    {
        return _children[index];
    }

    /// <summary>받은 폭에 글이 한 줄로 들어가면 글자, 아니면 아이콘.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _icon.Measure(availableSize);
        _showText = !Compact && _text.DesiredSize.Width <= availableSize.Width;
        // 보이지 않는 쪽은 그리기만 끈다(측정 중에 Visibility 를 바꾸면 배치를 다시 부른다)
        _text.Opacity = _showText ? 1 : 0;
        _icon.Opacity = _showText ? 0 : 1;
        ToolTip = _showText || string.IsNullOrEmpty(Text) ? null : Text;
        Cursor = _showText ? null : System.Windows.Input.Cursors.Hand; // 글로 다 보이면 누를 것이 없다
        return _showText ? _text.DesiredSize : _icon.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _text.Arrange(_showText ? new Rect(new Point(0, 0), _text.DesiredSize) : default);
        _icon.Arrange(_showText ? default : new Rect(new Point(0, 0), _icon.DesiredSize));
        return finalSize;
    }

    private void Apply(string? text)
    {
        _text.Text = text ?? string.Empty;
        Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
