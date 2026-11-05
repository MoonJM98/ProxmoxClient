using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ProxmoxClient.UI.Views.Controls;

/// <summary>
///     WPF 앱의 IconAssist 와 같은 연결 속성 — 버튼 템플릿(WpfTheme.axaml)이 아이콘·드롭다운 화살표·올렸을 때 색을 그린다.
///     XAML: <c>c:IconAssist.Icon="{StaticResource IconPlay}"</c>.
/// </summary>
public sealed class IconAssist : AvaloniaObject
{
    public static readonly AttachedProperty<Geometry?> IconProperty =
        AvaloniaProperty.RegisterAttached<IconAssist, Control, Geometry?>("Icon");

    public static readonly AttachedProperty<bool> DropdownProperty =
        AvaloniaProperty.RegisterAttached<IconAssist, Control, bool>("Dropdown");

    public static readonly AttachedProperty<IBrush?> BrushProperty =
        AvaloniaProperty.RegisterAttached<IconAssist, Control, IBrush?>("Brush");

    /// <summary>
    ///     아이콘과 글자 사이 간격 — 글자가 없으면(아이콘만 있는 버튼) 0 이어서 아이콘이 버튼 한가운데 온다.
    /// </summary>
    public static readonly Avalonia.Data.Converters.IValueConverter IconMarginConverter =
        new Avalonia.Data.Converters.FuncValueConverter<object?, Thickness>(content =>
            content is null || content is string { Length: 0 } ? new Thickness(0) : new Thickness(0, 0, 6, 0));

    public static Geometry? GetIcon(Control control) => control.GetValue(IconProperty);

    public static void SetIcon(Control control, Geometry? value) => control.SetValue(IconProperty, value);

    public static bool GetDropdown(Control control) => control.GetValue(DropdownProperty);

    public static void SetDropdown(Control control, bool value) => control.SetValue(DropdownProperty, value);

    public static IBrush? GetBrush(Control control) => control.GetValue(BrushProperty);

    public static void SetBrush(Control control, IBrush? value) => control.SetValue(BrushProperty, value);
}

/// <summary>아이콘 + 글자(WPF IconLabel) — 탭·목록 머리에 쓴다.</summary>
public sealed class IconLabel : StackPanel
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<IconLabel, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<IconLabel, string?>(nameof(Text));

    private readonly Avalonia.Controls.Shapes.Path _icon = new() { Width = 15, Height = 15, Classes = { "icon" } };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public IconLabel()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 7;
        Children.Add(_icon);
        Children.Add(_text);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty)
        {
            _icon.Data = Icon;
            _icon.IsVisible = Icon is not null;
        }
        else if (change.Property == TextProperty)
        {
            _text.Text = Text;
        }
    }
}

/// <summary>이름 + 입력 한 줄(WPF FormRow) — 데스크톱은 이름 열 폭을 맞춰 나란히, 모바일은 위아래로.</summary>
public sealed class FormRow : ContentControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<FormRow, string?>(nameof(Label));

    public static readonly StyledProperty<double> LabelWidthProperty =
        AvaloniaProperty.Register<FormRow, double>(nameof(LabelWidth), 110);

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<FormRow, string?>(nameof(Hint));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double LabelWidth
    {
        get => GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }
}
