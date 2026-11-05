using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ProxmoxClient.UI.Views.Storage;

/// <summary>
///     설명 한 줄 — WPF InfoTip 과 같다. 글이 받은 폭에 한 줄로 들어가면 글을 보이고, 아니면 정보 아이콘만 두어
///     마우스를 올리거나 누르면(모바일) 설명 풍선이 뜬다.
/// </summary>
internal sealed class StorageInfoTip : Grid
{
    private readonly string _text;
    private readonly TextBlock _label;
    private readonly Avalonia.Controls.Shapes.Path _icon;

    public StorageInfoTip(string text)
    {
        _text = text;
        Margin = new Thickness(4, 0, 8, 0);
        VerticalAlignment = VerticalAlignment.Center;
        _label = new TextBlock
        {
            Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Classes = { "subtle" }
        };
        _icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 16, Height = 16, Stretch = Stretch.Uniform, Classes = { "icon" },
            Data = StorageFiles.Icon("IconInfo")
        };
        Children.Add(_icon);
        Children.Add(_label);
        SizeChanged += (_, _) => Fit();
    }

    /// <summary>글이 한 줄로 들어가는지 재고, 글 또는 아이콘을 고른다. 안 보이는 쪽은 그리기만 끈다(배치는 그대로).</summary>
    private void Fit()
    {
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var fits = _label.DesiredSize.Width <= Bounds.Width;
        _label.Opacity = fits ? 1 : 0;
        _icon.Opacity = fits ? 0 : 1;
        ToolTip.SetTip(this, fits ? null : _text);
        Cursor = fits ? null : new Cursor(StandardCursorType.Hand);
    }
}
