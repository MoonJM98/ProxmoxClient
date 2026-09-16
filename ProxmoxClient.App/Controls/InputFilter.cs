using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ProxmoxClient.App.Controls;

public enum InputFilterMode
{
    None,

    /// <summary>0-9 만 허용.</summary>
    Integer,

    /// <summary>0-9 와 소수점 1개.</summary>
    Decimal,

    /// <summary>포트 목록/범위: 0-9 , : - 공백.</summary>
    PortList
}

/// <summary>
///     TextBox 입력 제한 연결 속성 — 타이핑·붙여넣기 모두 검사하고 숫자 모드에서는 IME(한글 입력)를 끈다.
///     사용 예: &lt;TextBox ic:InputFilter.Mode="Integer"/&gt;
/// </summary>
public static class InputFilter
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(InputFilterMode), typeof(InputFilter),
        new PropertyMetadata(InputFilterMode.None, OnModeChanged));

    public static InputFilterMode GetMode(DependencyObject element)
    {
        return (InputFilterMode)element.GetValue(ModeProperty);
    }

    public static void SetMode(DependencyObject element, InputFilterMode value)
    {
        element.SetValue(ModeProperty, value);
    }

    internal static bool IsAllowed(InputFilterMode mode, string text)
    {
        return mode switch
        {
            InputFilterMode.Integer => text.All(char.IsAsciiDigit),
            InputFilterMode.Decimal => text.All(c => char.IsAsciiDigit(c) || c == '.') &&
                                       text.Count(c => c == '.') <= 1,
            InputFilterMode.PortList => text.All(c => char.IsAsciiDigit(c) || c is ',' or ':' or '-' or ' '),
            _ => true
        };
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        box.PreviewTextInput -= OnPreviewTextInput;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        DataObject.RemovePastingHandler(box, OnPasting);

        var mode = (InputFilterMode)e.NewValue;
        InputMethod.SetIsInputMethodEnabled(box, mode == InputFilterMode.None);
        if (mode == InputFilterMode.None) return;

        box.PreviewTextInput += OnPreviewTextInput;
        box.PreviewKeyDown += OnPreviewKeyDown;
        DataObject.AddPastingHandler(box, OnPasting);
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;
        e.Handled = !IsAllowed(GetMode(box), ProposedText(box, e.Text));
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // WPF TextBox 는 스페이스를 TextInput 으로 보내지 않으므로 별도 차단
        if (e.Key == Key.Space && GetMode((TextBox)sender) is InputFilterMode.Integer or InputFilterMode.Decimal)
            e.Handled = true;
    }

    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text
            || !IsAllowed(GetMode(box), ProposedText(box, text)))
            e.CancelCommand();
    }

    private static string ProposedText(TextBox box, string input)
    {
        return box.Text.Remove(box.SelectionStart, box.SelectionLength).Insert(box.SelectionStart, input);
    }
}