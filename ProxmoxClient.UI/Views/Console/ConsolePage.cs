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
///     VM 콘솔 화면 — 원격 화면을 가득 채우고, 페이지 위쪽 막대·특수키 막대는 숨긴 채 떠 있는 버블만 보인다(사용자 요청).
///     버블을 누르면 위쪽 막대(뒤로 · 제목 · 콘솔 설정)와 아래쪽 특수키 막대(키보드 · Esc · Tab · Ctrl/Alt 고정 · Win ·
///     방향키 · 더 보기(F1~F12 등) · Ctrl+Alt+Del · 마우스/터치 모드 · 화면 맞춤)가 열리고, 다시 누르면 닫힌다.
///     버블은 끌어서 옮길 수 있다. 데스크톱은 물리 키보드·마우스를 그대로 쓴다.
/// </summary>
public sealed class ConsolePage : ContentPage, Services.IOwnsTouchDrag
{
    private readonly VncSurface _surface = new();
    private readonly TextBox _input;
    private readonly ToggleButton _ctrl;
    private readonly ToggleButton _alt;
    private readonly ToggleButton _super;
    private readonly Button _mode;
    private readonly Border _more;
    private readonly Border _bar;
    private readonly Border _top;
    private readonly TextBlock _title;
    private readonly Controls.FloatingBubble _bubble;
    private ConsoleViewModel? _vm;

    public ConsolePage()
    {
        // 원격 화면을 가리지 않게 페이지 위쪽 막대는 숨긴다 — 뒤로 가기는 버블 메뉴의 [뒤로]와 기기의 뒤로 가기 키로
        NavigationPage.SetHasNavigationBar(this, false);

        // 소프트 키보드 입력 받기 — 보이지 않는 입력 칸(포커스를 주면 모바일 키보드가 올라온다)
        _input = new TextBox
        {
            Width = 1, Height = 1, Opacity = 0.01, AcceptsReturn = false, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom, Text = string.Empty
        };
        Controls.SoftKeyboard.Configure(_input); // Enter 로 키보드가 닫히지 않게
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

        _viewMode = KeyButton(L.T("Console_ViewFit"), (_, _) => NextViewMode(), L.T("Console_ViewModeTip"));
        _surface.Viewport.Changed += UpdateViewModeLabel; // 손가락으로 확대하면 배율 표시
        var main = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                KeyboardButton(),
                Key("Esc", KeySyms.Escape), Key("Tab", KeySyms.Tab), _ctrl, _alt, _super,
                Key("←", KeySyms.Left), Key("↑", KeySyms.Up), Key("↓", KeySyms.Down), Key("→", KeySyms.Right),
                KeyButton("⋯", (_, _) => _moreToggle(), L.T("Console_MoreKeys")),
                _mode,
                _viewMode
            }
        };
        var extra = new StackPanel { Orientation = Orientation.Horizontal };
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
        // 콘솔 설정(화질·압축·키보드 등) — 데스크톱의 연결 설정과 같은 화면. 저장하고 돌아오면 다시 연결해 적용
        extra.Children.Add(KeyButton(L.T("ConsoleSettingsWindow_01"), async (_, _) => await OpenSettingsAsync()));
        _more = new Border { IsVisible = false, Child = Row(extra) };

        // 특수키 막대 — 화면 위에 반투명하게 겹쳐 한 줄로, 넘치면 옆으로 밀어 본다(원격 화면을 가리지 않게)
        _bar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#66000000")), Padding = new Thickness(4, 4, 4, 6),
            VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false,
            Child = new StackPanel { Spacing = 2, Children = { _more, Row(main) } }
        };

        // 위쪽 막대(버블 메뉴) — 뒤로 · 제목 · 콘솔 설정
        _title = new TextBlock
        {
            Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0)
        };
        var back = KeyButton(L.T("Console_Back"), async (_, _) => await Services.AppServices.Navigator.PopAsync());
        var settings = KeyButton(L.T("ConsoleSettingsWindow_01"), async (_, _) => await OpenSettingsAsync());
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(settings, 2);
        _top = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#99000000")), Padding = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Top, IsVisible = false,
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { back, _title, settings } }
        };

        _bubble = new Controls.FloatingBubble(L.T("Console_Menu"));
        _bubble.SetIcon("IconMore");
        _bubble.Clicked += () => SetMenuOpen(!_top.IsVisible);

        _surface.PointerSink = (b, x, y) => _vm?.SendPointer(b, x, y);
        // 키보드를 연 동안 화면을 만져도 키보드 유지 — 화면이 포커스를 가져가면(누르면 저절로) 키보드가 내려간다(사용자 요청)
        _input.GotFocus += (_, _) => UpdateSurfaceFocusable();
        _input.LostFocus += (_, _) => UpdateSurfaceFocusable();
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.InputPane is { } pane) pane.StateChanged += OnInputPaneChanged;
        };
        DetachedFromVisualTree += (_, e) =>
        {
            if (TopLevel.GetTopLevel(e.RootVisual)?.InputPane is { } pane) pane.StateChanged -= OnInputPaneChanged;
        };
        _surface.KeySink = (keysym, down) => _vm?.SendKey(keysym, down);
        _surface.ScanKeySink = (xt, keysym, down) => _vm?.SendScanKey(xt, keysym, down); // 물리 키보드: 키 위치 + keysym
        // 끊기거나 실패하면 상태 아래에 [다시 연결]
        var reconnect = new Button
        {
            Content = L.T("Console_Reconnect"), Classes = { "AccentButton" }, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 110, 0, 0), MinWidth = 140
        };
        reconnect.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(ConsoleViewModel.CanReconnect)));
        reconnect.Click += async (_, _) =>
        {
            if (_vm is not null) await _vm.ReconnectAsync();
        };
        Content = new Grid { Children = { _surface, status, reconnect, _input, _top, _bar, _bubble } };
        Background = Brushes.Black;
        DataContextChanged += (_, _) => AttachViewModel(DataContext as ConsoleViewModel);
        // 앱이 배경으로 가면 눌린 키를 모두 뗀다(게스트에 Alt 등이 눌린 채 남지 않게)
        Action<bool> onForeground = foreground =>
        {
            if (!foreground) _surface.ReleaseAllKeys();
        };
        Services.AppLifecycle.ForegroundChanged += onForeground;
        NavigatedFrom += (_, e) =>
        {
            if (e.NavigationType is NavigationType.Pop or NavigationType.PopToRoot or NavigationType.Remove)
                Services.AppLifecycle.ForegroundChanged -= onForeground;
            _surface.Gestures.Cancel();
            _surface.ReleaseAllKeys();
        };
    }

    private readonly Button _viewMode;

    /// <summary>한 줄 가로 스크롤(스크롤 막대는 숨기고 손가락으로 민다).</summary>
    private static ScrollViewer Row(Control content)
    {
        return new ScrollViewer
        {
            Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
    }

    /// <summary>화면 모드 돌리기: 맞춤 → 채우기 → 원본(1:1) → 맞춤. 손으로 확대했으면 맞춤으로 돌아간다.</summary>
    private void NextViewMode()
    {
        var vp = _surface.Viewport;
        var next = vp.Mode switch
        {
            ViewMode.Fit => ViewMode.Fill,
            ViewMode.Fill => ViewMode.Actual,
            _ => ViewMode.Fit
        };
        vp.SetMode(next);
        // 확대한 화면에서 커서가 가장자리·화면 밖에 남지 않게(실제 마우스는 사용자가 직접 움직이므로 두지 않는다)
        if (_surface.DrawsSoftwareCursor) _surface.Gestures.KeepCursorInView();
        UpdateViewModeLabel();
        _surface.InvalidateVisual();
    }

    private void UpdateViewModeLabel()
    {
        _viewMode.Content = _surface.Viewport.Mode switch
        {
            ViewMode.Fill => L.T("Console_ViewFill"),
            ViewMode.Actual => L.T("Console_ViewActual"),
            ViewMode.Custom => $"{_surface.Viewport.Zoom:0.#}×",
            _ => L.T("Console_ViewFit")
        };
    }

    private Action _moreToggle => () => _more.IsVisible = !_more.IsVisible;

    /// <summary>버블 메뉴(위쪽 막대 + 특수키 막대)를 열거나 닫는다.</summary>
    public void SetMenuOpen(bool open)
    {
        _top.IsVisible = open;
        _bar.IsVisible = open;
        _bubble.SetIcon(open ? "IconX" : "IconMore");
    }

    /// <summary>버블 메뉴가 열려 있다.</summary>
    public bool IsMenuOpen => _top.IsVisible;

    private async Task OpenSettingsAsync()
    {
        if (_vm is not { } vm) return;

        var current = await new ProxmoxClient.Core.Vnc.ConsoleSettingsStore().LoadAsync();
        var tab = vm.Protocol == ProxmoxClient.Core.Vnc.ConsoleProtocol.Rdp
            ? Settings.ConsoleSettingsTab.Rdp
            : Settings.ConsoleSettingsTab.Vnc;
        await Services.AppServices.Navigator.PushViewAsync(L.T("ConsoleSettingsWindow_01"),
            new Settings.ConsoleSettingsPage(current, tab, _ => vm.ReconnectWhenBack = true));
    }

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
        _title.Text = vm.Title;
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
        // 게스트 커서 모양 — 이후 커서는 이 모양으로 손가락·마우스 자리에 바로 그린다(서버 응답을 기다리지 않음)
        vm.Vnc.CursorShape += shape => Avalonia.Threading.Dispatcher.UIThread.Post(() => _surface.SetCursorShape(shape));
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

    /// <summary>
    ///     사용자가 화면 키보드를 열어 둔 상태 — 입력 칸에 포커스가 있고 키보드가 떠 있다. 키보드를 직접 닫았으면 아니다
    ///     (그때는 화면을 누르면 화면이 포커스를 가져와 물리 키보드가 키 위치(스캔코드)로 바로 간다).
    ///     키보드 상태를 알 수 없는 플랫폼은 포커스만 본다.
    /// </summary>
    private bool IsSoftKeyboardActive()
    {
        if (!_input.IsFocused) return false;

        var pane = TopLevel.GetTopLevel(this)?.InputPane;
        return pane is null || pane.State == Avalonia.Controls.Platform.InputPaneState.Open;
    }

    /// <summary>키보드를 연 동안은 원격 화면이 포커스를 받지 않게 한다(눌러도 키보드가 닫히지 않음).</summary>
    private void UpdateSurfaceFocusable()
    {
        _surface.Focusable = !IsSoftKeyboardActive();
    }

    private void OnInputPaneChanged(object? sender, Avalonia.Controls.Platform.InputPaneStateEventArgs e)
    {
        UpdateSurfaceFocusable(); // 키보드를 직접 닫으면 다시 화면이 포커스를 받는다(물리 키보드 → 스캔코드)
    }

    private void ShowKeyboard()
    {
        Controls.SoftKeyboard.Show(_input, _surface); // 닫힌 키보드도 다시 연다
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

        // 화면 키보드의 Enter 가 글자('\n')로 들어올 수도 있다(여러 줄 칸) — Return 키로
        foreach (var rune in text.EnumerateRunes())
            Tap(rune.Value is '\n' or '\r' ? KeySyms.Return : KeySyms.FromChar(rune.Value));
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

    /// <summary>키보드 단추 내용 — 글꼴에 없을 수 있는 ⌨ 기호 대신 앱 아이콘.</summary>
    private static Avalonia.Controls.Shapes.Path KeyboardIcon()
    {
        var icon = new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Stroke = Brushes.White };
        if (Application.Current?.TryGetResource("IconKeyboard", Application.Current.ActualThemeVariant, out var data) == true)
            icon.Data = data as Geometry;
        return icon;
    }

    private Button KeyboardButton()
    {
        var button = KeyButton(L.T("Console_Keyboard"), (_, _) => ShowKeyboard(), L.T("Console_Keyboard"));
        button.Content = KeyboardIcon();
        return button;
    }

    private static Button KeyButton(string label, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click,
        string? tip = null)
    {
        var button = new Button
        {
            Content = label, MinWidth = 44, MinHeight = 40, Margin = new Thickness(2), Padding = new Thickness(10, 6),
            Foreground = Brushes.White, Background = new SolidColorBrush(Color.Parse("#B03A3A3A")),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false
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
            Foreground = Brushes.White, Background = new SolidColorBrush(Color.Parse("#B03A3A3A")),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false
        };
    }
}
