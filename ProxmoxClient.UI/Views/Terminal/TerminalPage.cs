using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;
using ProxmoxClient.UI.Views.Controls;

namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>
///     터미널 화면(WPF TerminalWindow) — 위 도구 막대(재연결/연결 끊기 · 복사 · 붙여넣기 · 글자 크기),
///     가운데 터미널, 아래 상태 줄. 터치 기기는 특수 키 막대(Esc·Tab·Ctrl/Alt 고정·방향키·기호·F1~F12)를 더한다.
/// </summary>
public sealed partial class TerminalPage : ContentPage, IOwnsTouchDrag
{
    private readonly TerminalView _terminal;
    private readonly Button _reconnect;
    private readonly Button _disconnect;
    private readonly Button _copy;
    private readonly Button _paste;
    private readonly ToggleButton _ctrl;
    private readonly ToggleButton _alt;
    private readonly Border _more;
    private TerminalViewModel? _vm;
    private (int Columns, int Rows) _pendingSize;

    /// <summary>PTY 크기를 보내기 전 기다리는 시간 — 크기 변경이 이만큼 멈추면 보낸다.</summary>
    private const int PtyResizeDelayMs = 120;

    private int _ptyGeneration;
    private bool _ptyPending;
    private bool _ptySizedSinceConnect;

    public TerminalPage()
    {
        _terminal = new TerminalView(PlatformInfo.IsTouch && !PlatformInfo.IsDesktop ? 12 : 14);
        _terminal.Input = text => _vm?.Send(text);
        _terminal.SizeChanged2 += (columns, rows) =>
        {
            // 화면은 바로 바뀐다. 서버 PTY 크기는 창을 끄는 동안(창 끌기·키보드 애니메이션) 멈춘 뒤 한 번만 보낸다 — 크기마다
            // 보내면 셸·편집기가 그때마다 다시 그려 화면이 어지럽다. 연결 직후 첫 크기는 바로(같은 크기는 세션이 거른다)
            _pendingSize = (columns, rows);
            if (_vm is not { IsConnected: true } || !_ptySizedSinceConnect)
            {
                _ptySizedSinceConnect = _vm?.IsConnected == true;
                FlushPtySize();
                return;
            }

            // 마지막 변경만 보낸다(DispatcherTimer 는 헤드리스 검사에서 돌지 않아 지연 후 Post 로)
            var generation = ++_ptyGeneration;
            _ptyPending = true;
            _ = Task.Delay(PtyResizeDelayMs).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                if (generation == _ptyGeneration && _ptyPending) FlushPtySize();
            }), TaskScheduler.Default);
        };
        _terminal.SelectionChanged += has => _copy!.IsEnabled = has;
        // 원격 복사(OSC 52 — tmux·vim·neovim 에서 복사) → 이 기기 클립보드
        _terminal.Buffer.ClipboardWrite += text => Dispatcher.UIThread.Post(async () =>
        {
            await ClipboardHelper.SetTextAsync(text);
            AppServices.Dialogs.Toast(L.T("Terminal_RemoteCopied", text.Length));
        });
        _terminal.TakeStickyModifiers = TakeSticky;
        _terminal.LineSubmitted += command =>
        {
            _history?.Add(command);
            HideAssist();
        };

        _reconnect = ToolButton(L.T("ConsoleWindow_02"), "IconRefresh", async () =>
        {
            if (_vm is not null) await _vm.ConnectAsync();
        });
        _disconnect = ToolButton(L.T("MainWindow_02"), "IconLogOut", async () =>
        {
            if (_vm is not null) await _vm.DisconnectAsync();
        });
        _copy = ToolButton(L.T("TerminalWindow_02"), "IconCopy", async () =>
        {
            if (!await _terminal.CopyAsync()) AppServices.Dialogs.Toast(L.T("TerminalWindow_M02"));
            else AppServices.Dialogs.Toast(L.T("TerminalWindow_M03"));
            _terminal.FocusInput();
        });
        _copy.IsEnabled = false;
        _paste = ToolButton(L.T("TerminalWindow_04"), "IconClipboard", async () =>
        {
            await _terminal.PasteFromClipboardAsync();
            _terminal.FocusInput();
        });
        var smaller = ToolButton("A−", null, () =>
        {
            _terminal.SetFontSize(_terminal.TerminalFontSize - 1);
            return Task.CompletedTask;
        }, L.T("Terminal_FontSmaller"));
        var larger = ToolButton("A+", null, () =>
        {
            _terminal.SetFontSize(_terminal.TerminalFontSize + 1);
            return Task.CompletedTask;
        }, L.T("Terminal_FontLarger"));

        var toolbar = new Border
        {
            Classes = { "Toolbar" }, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 6),
            Child = new WrapPanel
            {
                Children =
                {
                    _reconnect, _disconnect, Separator(), _copy, _paste, Separator(), smaller, larger
                }
            }
        };
        toolbar.Bind(Border.BackgroundProperty, toolbar.GetResourceObservable("BrushPanel"));
        toolbar.Bind(Border.BorderBrushProperty, toolbar.GetResourceObservable("BrushBorder"));
        DockPanel.SetDock(toolbar, Dock.Top);

        var stateText = new TextBlock { Classes = { "subtle" }, TextTrimming = TextTrimming.CharacterEllipsis };
        stateText.Bind(TextBlock.TextProperty, new Binding(nameof(TerminalViewModel.StateText)));
        var statusBar = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 6), Child = stateText,
            IsVisible = PlatformInfo.IsDesktop
        };
        statusBar.Bind(Border.BackgroundProperty, statusBar.GetResourceObservable("BrushPanel"));
        statusBar.Bind(Border.BorderBrushProperty, statusBar.GetResourceObservable("BrushBorder"));
        DockPanel.SetDock(statusBar, Dock.Bottom);

        // 연결 중·끊김 안내 — 터미널 위 가운데
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#CC202020")), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Child = new TextBlock { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 }
        };
        overlay.Bind(IsVisibleProperty, new Binding(nameof(TerminalViewModel.HasStatus)));
        ((TextBlock)overlay.Child).Bind(TextBlock.TextProperty, new Binding(nameof(TerminalViewModel.Status)));

        // 특수 키 막대(터치)
        _ctrl = Toggle("Ctrl");
        _alt = Toggle("Alt");
        var main = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                WithContent(KeyButton(L.T("Console_Keyboard"), () => _terminal.FocusInput(), L.T("Console_Keyboard")), KeyboardIcon()),
                Key("Esc", Avalonia.Input.Key.Escape),
                KeyButton("Tab", () => _ = CompleteAsync(), L.T("Terminal_Completions", "…")), // 후보가 여럿이면 목록으로
                KeyButton(L.T("Terminal_HistoryButton"), ShowHistory, L.T("Terminal_History")),
                _ctrl, _alt,
                Key("←", Avalonia.Input.Key.Left), Key("↑", Avalonia.Input.Key.Up),
                Key("↓", Avalonia.Input.Key.Down), Key("→", Avalonia.Input.Key.Right),
                Text("|"), Text("-"), Text("/"), Text("~"),
                KeyButton("⋯", () => _more!.IsVisible = !_more.IsVisible, L.T("Console_MoreKeys"))
            }
        };
        var extra = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, key) in new[]
                 {
                     ("Home", Avalonia.Input.Key.Home), ("End", Avalonia.Input.Key.End),
                     ("PgUp", Avalonia.Input.Key.PageUp), ("PgDn", Avalonia.Input.Key.PageDown),
                     ("Ins", Avalonia.Input.Key.Insert), ("Del", Avalonia.Input.Key.Delete)
                 })
            extra.Children.Add(Key(label, key));
        foreach (var symbol in new[] { "`", "\\", "*", "&", "$", ">", "<", "\"", "'", ";", ":" })
            extra.Children.Add(Text(symbol));
        for (var i = 0; i < 12; i++) extra.Children.Add(Key($"F{i + 1}", Avalonia.Input.Key.F1 + i));
        foreach (var (label, letter) in new[] { ("^C", 'c'), ("^D", 'd'), ("^Z", 'z'), ("^L", 'l'), ("^R", 'r') })
            extra.Children.Add(KeyButton(label, () => _terminal.Send(((char)(letter - 'a' + 1)).ToString(), false)));
        _more = new Border { IsVisible = false, Child = Row(extra) };

        var keysBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E0181818")), Padding = new Thickness(4, 4, 4, 6),
            // 한 줄 — 넘치면 옆으로 밀어 본다(콘솔과 같은 규칙, 두 줄로 꺾이지 않게)
            Child = new StackPanel { Spacing = 2, Children = { _more, Row(main) } }, IsVisible = PlatformInfo.IsTouch
        };
        DockPanel.SetDock(keysBar, Dock.Bottom);

        _terminalHost = new Grid { Children = { _terminal, overlay } };
        Content = new DockPanel
        {
            Children = { toolbar, statusBar, keysBar, _terminalHost }
        };
        Background = Brushes.Black;
        DataContextChanged += (_, _) => Attach(DataContext as TerminalViewModel);
        Loaded += (_, _) => _terminal.FocusInput();
    }

    public TerminalView Terminal => _terminal;

    /// <summary>터미널과 그 위 겹침(상태 안내·도우미 목록).</summary>
    private readonly Grid _terminalHost;

    private void Attach(TerminalViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (vm is null) return;

        Header = vm.Title;
        vm.Output = _terminal.Write;
        // 최근 명령 기록 — 서버·대상마다 따로(이 기기에만)
        AttachHistory(new TerminalViewModelKey($"{vm.Api.Profile.Host}:{vm.Api.Profile.Port}|{vm.Target.BasePath}"));
        vm.PropertyChanged += OnVmChanged;
        _ptyPending = false;
        _ptySizedSinceConnect = false;
        _pendingSize = (_terminal.Buffer.Columns, _terminal.Buffer.Rows);
        vm.Resize(_terminal.Buffer.Columns, _terminal.Buffer.Rows);
        UpdateButtons();
        _ = ApplySettingsAsync();
    }

    /// <summary>콘솔 설정의 터미널 글꼴·크기(데스크톱) — 모바일은 화면에 맞춘 크기를 그대로 쓰고 손가락으로 바꾼다.</summary>
    private async Task ApplySettingsAsync()
    {
        var settings = await new Core.Vnc.ConsoleSettingsStore().LoadAsync();
        _terminal.SetFontFamily(settings.TerminalFontFamily);
        if (PlatformInfo.IsDesktop) _terminal.SetFontSize(settings.TerminalFontSize + 1); // 설정은 pt, 화면은 px
    }

    /// <summary>기다리던 PTY 크기를 지금 보낸다.</summary>
    private void FlushPtySize()
    {
        _ptyPending = false;
        _vm?.Resize(_pendingSize.Columns, _pendingSize.Rows);
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.IsConnected))
        {
            // 새로 연결됨 — 기다리던 크기는 바로 보내고, 다음 첫 크기 변경도 바로
            _ptySizedSinceConnect = false;
            if (_ptyPending) FlushPtySize();
        }

        if (e.PropertyName is nameof(TerminalViewModel.IsConnected) or nameof(TerminalViewModel.IsConnecting))
            UpdateButtons();
        if (e.PropertyName == nameof(TerminalViewModel.IsConnected) && _vm?.IsConnected == true) _terminal.FocusInput();
    }

    /// <summary>연결 중이면 '연결 끊기', 끊겼으면 '재연결' — 같은 자리에서 바뀐다(WPF 와 같다).</summary>
    private void UpdateButtons()
    {
        var connected = _vm?.IsConnected == true;
        _disconnect.IsVisible = connected;
        _reconnect.IsVisible = !connected;
        _reconnect.IsEnabled = _vm?.IsConnecting != true;
        _paste.IsEnabled = connected;
    }

    private (bool Ctrl, bool Alt) TakeSticky()
    {
        var result = (_ctrl.IsChecked == true, _alt.IsChecked == true);
        _ctrl.IsChecked = _alt.IsChecked = false;
        return result;
    }

    /// <summary>한 줄 가로 스크롤(스크롤 막대는 숨기고 손가락으로 민다).</summary>
    private static ScrollViewer Row(Control content)
    {
        return new ScrollViewer
        {
            Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
    }

    private static Button ToolButton(string label, string? iconKey, Func<Task> run, string? tip = null)
    {
        var button = new Button
        {
            Content = label, MinWidth = PlatformInfo.IsDesktop && iconKey is not null ? 84 : 0,
            Margin = new Thickness(0, 0, 6, 0), Focusable = false
        };
        if (iconKey is not null)
        {
            if (Application.Current?.TryGetResource(iconKey, Application.Current.ActualThemeVariant, out var icon) == true)
                IconAssist.SetIcon(button, icon as Geometry);
            CompactButton.Apply(button, label);
        }

        if (tip is not null) ToolTip.SetTip(button, tip);
        button.Click += async (_, _) => await run();
        return button;
    }

    private static Border Separator()
    {
        var border = new Border { Width = 1, Margin = new Thickness(4, 4, 10, 4) };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("BrushBorder"));
        return border;
    }

    private Button Key(string label, Key key)
    {
        return KeyButton(label, () => _terminal.SendKey(key));
    }

    private Button Text(string text)
    {
        return KeyButton(text, () => _terminal.Send(text));
    }

    /// <summary>키보드 단추 내용 — 글꼴에 없을 수 있는 ⌨ 기호 대신 앱 아이콘.</summary>
    private static Avalonia.Controls.Shapes.Path KeyboardIcon()
    {
        var icon = new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Stroke = Brushes.White };
        if (Application.Current?.TryGetResource("IconKeyboard", Application.Current.ActualThemeVariant, out var data) == true)
            icon.Data = data as Geometry;
        return icon;
    }

    private static Button WithContent(Button button, Control content)
    {
        button.Content = content;
        return button;
    }

    private static Button KeyButton(string label, Action click, string? tip = null)
    {
        var button = new Button
        {
            Content = label, MinWidth = 40, MinHeight = 40, Margin = new Thickness(2), Padding = new Thickness(8, 4),
            Foreground = Brushes.White, Background = new SolidColorBrush(Color.Parse("#3A3A3A")),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false
        };
        if (tip is not null) ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static ToggleButton Toggle(string label)
    {
        return new ToggleButton
        {
            Content = label, MinWidth = 46, MinHeight = 40, Margin = new Thickness(2), Padding = new Thickness(8, 4),
            Foreground = Brushes.White, Background = new SolidColorBrush(Color.Parse("#3A3A3A")),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false
        };
    }
}
