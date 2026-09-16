using System.Windows;
using System.Windows.Controls;

namespace ProxmoxClient.App.Controls;

/// <summary>
///     설정 폼 한 줄: 왼쪽 고정폭 라벨 + 오른쪽 입력 컨트롤 + (선택) 아래 도움말.
///     모든 대화상자에서 라벨 폭·간격을 통일하기 위해 사용한다. 템플릿은 DarkTheme.xaml.
/// </summary>
public sealed class FormRow : ContentControl
{
    public const double DefaultLabelWidth = 150;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(FormRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(FormRow), new PropertyMetadata(null));

    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(
        nameof(LabelWidth), typeof(double), typeof(FormRow), new PropertyMetadata(DefaultLabelWidth));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Hint
    {
        get => (string?)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public double LabelWidth
    {
        get => (double)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }
}