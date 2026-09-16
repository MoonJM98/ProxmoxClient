using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ProxmoxClient.App.Controls;

/// <summary>
///     24x24 좌표계의 선형(stroke) 아이콘 — Lucide(ISC) 스타일 경로를 <see cref="Themes" /> Icons.xaml 에서 참조한다.
///     색은 <see cref="IconBrush" /> 가 없으면 상속된 Foreground 를 따른다(비활성 상태 색 자동 반영).
/// </summary>
public sealed class PathIcon : Viewbox
{
    private const double DefaultSize = 16;
    private const double CanvasSize = 24;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(PathIcon),
        new PropertyMetadata(null, (d, e) => ((PathIcon)d)._path.Data = e.NewValue as Geometry));

    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
        nameof(IconBrush), typeof(Brush), typeof(PathIcon),
        new PropertyMetadata(null, (d, _) => ((PathIcon)d).UpdateStroke()));

    private readonly Path _path = new()
    {
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round
    };

    public PathIcon()
    {
        Width = DefaultSize;
        Height = DefaultSize;
        Stretch = Stretch.Uniform;
        VerticalAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;

        var canvas = new Canvas { Width = CanvasSize, Height = CanvasSize };
        canvas.Children.Add(_path);
        Child = canvas;
        Loaded += (_, _) => UpdateStroke();
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush? IconBrush
    {
        get => (Brush?)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextElement.ForegroundProperty) UpdateStroke();
    }

    private void UpdateStroke()
    {
        _path.Stroke = IconBrush ?? (Brush)GetValue(TextElement.ForegroundProperty);
    }
}