using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views.Console;

/// <summary>
///     VM 콘솔 화면 — 원격 화면 + 아래쪽 특수키 막대(키보드 · Esc · Tab · Ctrl/Alt 고정 · Win · 방향키 · 더 보기(F1~F12 등)
///     · Ctrl+Alt+Del · 마우스/터치 모드 · 화면 맞춤). 데스크톱은 물리 키보드·마우스를 그대로 쓴다.
/// </summary>
public sealed class ConsolePage : ContentPage
{
    private readonly VncSurface _surface = new();
    private readonly TextBox _input;
    private readonly ToggleButton _ctrl;
    private readonly ToggleButton _alt;
    private readonly ToggleButton _super;
    private readonly Button _mode;
    private readonly Border _more;
    private ConsoleViewModel? _vm;

    public ConsolePage()
    {
        // 소프트 키보드 입력 받기 — 보이지 않는 입력 칸(포커스를 주면 모바일 키보드가 올라온다)
        _input = new TextBox
        {
            Width = 1, Height = 1, Opacity = 0.01, AcceptsReturn = false, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom, Text = string.Empty
        };
        Avalonia.Input.TextInput.TextInputOptions.SetAutoCapitalization(_input, false);
        _input.TextChanged += OnInputText;
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _ctrl = Toggle("Ctrl");
        _alt = Toggle("Alt");
        _super = Toggle("Win");
        _mode = KeyButton(ModeLabel(), (_, _) => ToggleMode());

        var status = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#CC202020")), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Child = new TextBlock { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }
        };
        status.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(ConsoleViewModel.HasStatus)));
        ((TextBlock)status.Child).Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ConsoleViewModel.Status)));

        var main = new WrapPanel
        {
            Children =
            {
                KeyButton("⌨", (_, _) => ShowKeyboard(), L.T("Console_Keyboard")),
                Key("Esc", KeySyms.Escape), Key("Tab", KeySyms.Tab), _ctrl, _alt, _super,
                Key("←", KeySyms.Left), Key("↑", KeySyms.Up), Key("↓", KeySyms.Down), Key("→", KeySyms.Right),
                KeyButton("⋯", (_, _) => _moreToggle(), L.T("Console_MoreKeys")),
                _mode,
                KeyButton("⤢", (_, _) => _surface.Viewport.Fit(), L.T("Console_Fit"))
            }
        };
        var extra = new WrapPanel();
        foreach (var (label, sym) in new[]
                 {
                     ("Home", KeySyms.Home), ("End", KeySyms.End), ("PgUp", KeySyms.PageUp), ("PgDn", KeySyms.PageDown),
                     ("Ins", KeySyms.Insert), ("Del", KeySyms.Delete), ("⌫", KeySyms.BackSpace), ("↵", KeySyms.Return)
                 })
            extra.Children.Add(Key(label, sym));
        for (var i = 0; i < 12; i++) extra.Children.Add(Key($"F{i + 1}", KeySyms.F1 + i));
        extra.Children.Add(KeyButton("Ctrl+Alt+Del", async (_, _) =>
        {
            if (_vm is not null && await AppServicesConfirm()) await _vm.Vnc.SendCtrlAltDelAsync();
        }));
        _more = new Border { IsVisible = false, Child = extra };

        var bar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F0202020")), Padding = new Thickness(4, 4, 4, 6),
            Child = new StackPanel { Children = { _more, main } }
        };
        DockPanel.SetDock(bar, Dock.Bottom);

        _surface.PointerSink = (b, x, y) => _vm?.SendPointer(b, x, y);
        _surface.KeySink = (keysym, down) => _vm?.SendKey(keysym, down); // 물리 키보드는 그대로
        Content = new DockPanel
        {
            Children = { bar, new Grid { Children = { _surface, status, _input } } }
        };
        Background = Brushes.Black;
        DataContextChanged += (_, _) => AttachViewModel(DataContext as ConsoleViewModel);
        NavigatedFrom += (_, _) => _surface.Gestures.Cancel();
    }

    private Action _moreToggle => () => _more.IsVisible = !_more.IsVisible;

    private static Task<bool> AppServicesConfirm()
    {
        return Services.AppServices.Dialogs.ConfirmAsync(L.T("Console_CadTitle"), L.T("Console_CadText"),
            L.T("Console_CadSend"));
    }

    private void AttachViewModel(ConsoleViewModel? vm)
    {
        _vm = vm;
        if (vm is null) return;

        Header = vm.Title;
        vm.Vnc.Connected += (w, h) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            _surface.Attach(w, h, () => vm.Vnc.Framebuffer));
        vm.Vnc.FrameReceived += (x, y, w, h) =>
        {
            // 해상도가 바뀌었으면(게스트 화면 크기 변경) 비트맵을 다시 만든다
            if (vm.Vnc.Width != (int)_surface.Viewport.Remote.Width || vm.Vnc.Height != (int)_surface.Viewport.Remote.Height)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    _surface.Attach(vm.Vnc.Width, vm.Vnc.Height, () => vm.Vnc.Framebuffer));
            else
                _surface.MarkDirty(x, y, w, h);
        };
        // 데스크톱은 마우스로 바로 다루므로 터치 모드(누른 곳 클릭)가 기본
        if (PlatformInfo.IsDesktop) _surface.Gestures.Mode = TouchInputMode.Touch;
        _mode.Content = ModeLabel();
    }

    private string ModeLabel()
    {
        return _surface.Gestures.Mode == TouchInputMode.Mouse ? L.T("Console_ModeMouse") : L.T("Console_ModeTouch");
    }

    private void ToggleMode()
    {
        _surface.Gestures.Mode = _surface.Gestures.Mode == TouchInputMode.Mouse
            ? TouchInputMode.Touch
            : TouchInputMode.Mouse;
        _mode.Content = ModeLabel();
        _surface.InvalidateVisual();
    }

    private void ShowKeyboard()
    {
        _input.Focus(); // 입력 칸에 포커스를 주면 모바일 키보드가 올라온다
    }

    /// <summary>
    ///     키 하나를 눌렀다 뗀다(특수키 막대·소프트 키보드) — 고정해 둔 Ctrl·Alt·Win 을 함께 누르고, 보낸 뒤 고정을 푼다
    ///     (한 번만 적용, 모바일 원격 앱들과 같은 방식).
    /// </summary>
    public void Tap(int keysym)
    {
        if (_vm is null) return;

        var modifiers = new List<int>();
        if (_ctrl.IsChecked == true) modifiers.Add(KeySyms.ControlL);
        if (_alt.IsChecked == true) modifiers.Add(KeySyms.AltL);
        if (_super.IsChecked == true) modifiers.Add(KeySyms.SuperL);
        foreach (var m in modifiers) _vm.SendKey(m, true);
        _vm.SendKey(keysym, true);
        _vm.SendKey(keysym, false);
        for (var i = modifiers.Count - 1; i >= 0; i--) _vm.SendKey(modifiers[i], false);
        if (modifiers.Count > 0) _ctrl.IsChecked = _alt.IsChecked = _super.IsChecked = false;
    }

    private void OnInputText(object? sender, TextChangedEventArgs e)
    {
        var text = _input.Text;
        if (string.IsNullOrEmpty(text)) return;

        foreach (var rune in text.EnumerateRunes()) Tap(KeySyms.FromChar(rune.Value));
        _input.Text = string.Empty;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // 글자가 아닌 키(지우기·엔터 등)는 입력 칸에 남지 않으므로 여기서 바로 보낸다
        var keysym = KeySyms.FromKey(e.Key);
        if (keysym == 0) return;

        Tap(keysym);
        e.Handled = true;
    }

    private Button Key(string label, int keysym)
    {
        return KeyButton(label, (_, _) => Tap(keysym));
    }

    private static Button KeyButton(string label, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click,
        string? tip = null)
    {
        var button = new Button
        {
            Content = label, MinWidth = 44, MinHeight = 40, Margin = new Thickness(2), Padding = new Thickness(10, 6),
            Foreground = Brushes.White, Background = new SolidColorBrush(Color.Parse("#3A3A3A")),
            HorizontalContentAlignment = HorizontalAlignment.Center, Focusable = false
        };
        if (tip is not null) ToolTip.SetTip(button, tip);
        button.Click += click;
        return button;
    }

    private static ToggleButton Toggle(string label)
    {
        return new ToggleButton
        {
            Content = label, MinWidth = 48, MinHeight = 40, Margin = new Thickness(2), Padding = new Thickness(10, 6),
            Foreground = Brushes.White, Background = new SolidColorBrush(Color.Parse("#3A3A3A")),
            HorizontalContentAlignment = HorizontalAlignment.Center, Focusable = false
        };
    }
}
