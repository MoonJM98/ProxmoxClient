using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ProxmoxClient.App.Controls;

/// <summary>
///     웹 UI 의 [종료 | ▾] 같은 나뉜 버튼 — 테두리 하나 안에 주 동작과 ▾ 를 둔다.
///     올리면 버튼 전체가 강조되고, ▾ 위에서는 ▾ 부분만 한 번 더 밝아진다. ▾ 를 누르면 주 동작(Click·Command) 대신
///     <see cref="DropDownMenu" /> 를 펼치거나(메뉴를 코드에서 만들 때는) <see cref="DropDownOpening" /> 을 알린다.
///     템플릿은 DarkTheme.xaml(PART_Drop = ▾ 영역).
/// </summary>
public sealed class SplitButton : Button
{
    public static readonly DependencyProperty DropDownMenuProperty = DependencyProperty.Register(
        nameof(DropDownMenu), typeof(ContextMenu), typeof(SplitButton), new PropertyMetadata(null));

    /// <summary>false 면 ▾ 를 숨기고 보통 버튼처럼 보인다(예: CT 는 콘솔 종류가 하나).</summary>
    public static readonly DependencyProperty ShowDropDownProperty = DependencyProperty.Register(
        nameof(ShowDropDown), typeof(bool), typeof(SplitButton), new PropertyMetadata(true));

    public ContextMenu? DropDownMenu
    {
        get => (ContextMenu?)GetValue(DropDownMenuProperty);
        set => SetValue(DropDownMenuProperty, value);
    }

    public bool ShowDropDown
    {
        get => (bool)GetValue(ShowDropDownProperty);
        set => SetValue(ShowDropDownProperty, value);
    }

    /// <summary>▾ 를 눌렀다 — 메뉴를 그때그때 만드는 쪽이 듣는다(sender = 이 버튼).</summary>
    public event RoutedEventHandler? DropDownOpening;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (ShowDropDown && GetTemplateChild("PART_Drop") is UIElement drop && drop.IsMouseOver)
        {
            e.Handled = true; // 주 동작(Click·Command)은 부르지 않는다
            OpenDropDown();
            return;
        }

        base.OnPreviewMouseLeftButtonDown(e);
    }

    /// <summary>키보드로도 ▾ 를 연다 — Alt+↓ 또는 F4(콤보 상자와 같은 키).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ShowDropDown && (key == Key.F4 || (key == Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))))
        {
            e.Handled = true;
            OpenDropDown();
            return;
        }

        base.OnKeyDown(e);
    }

    private void OpenDropDown()
    {
        DropDownOpening?.Invoke(this, new RoutedEventArgs());
        if (DropDownMenu is not { } menu) return;

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }
}
