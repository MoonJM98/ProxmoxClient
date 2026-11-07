using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ProxmoxClient.Core.Vnc;
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

    /// <summary>게스트 벨 — 화면 가장자리를 잠깐 밝게 테두리(진동 API 가 플랫폼 공통이 아니다).</summary>
    private readonly Border _bellFlash;

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
                // 한/영 — 게스트 입력기를 한글로 바꾼 뒤 화면 키보드 한글이 두벌식 키로 들어가 조합된다
                KeyButton(L.T("Console_HangulKey"), (_, _) => TapKey(SoftKeys.XtHangul, KeySyms.Hangul), L.T("Console_HangulTip")),
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
        // 오른쪽 Alt — 한국어 Windows 는 이것을 한/영으로 쓰는 경우가 많다
        extra.Children.Add(KeyButton("R-Alt", (_, _) => TapKey(SoftKeys.XtAltR, KeySyms.AltR), L.T("Console_RightAltTip")));
        // 클립보드 — 기기 → 게스트(클립보드로 / 키 입력으로), 게스트 → 기기
        extra.Children.Add(KeyButton(L.T("Console_PasteToGuest"), async (_, _) => await PasteToGuestAsync(), L.T("Console_PasteToGuestTip")));
        extra.Children.Add(KeyButton(L.T("Console_TypeClipboard"), async (_, _) => await TypeClipboardAsync(), L.T("Console_TypeClipboardTip")));
        extra.Children.Add(KeyButton(L.T("Console_CopyFromGuest"), async (_, _) => await CopyFromGuestAsync(), L.T("Console_CopyFromGuestTip")));
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
        _bellFlash = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#D0FFD24A")), BorderThickness = new Thickness(6),
            IsHitTestVisible = false, IsVisible = false
        };
        Content = new Grid { Children = { _surface, _bellFlash, status, reconnect, _input, _top, _bar, _bubble } };
        Background = Brushes.Black;
        DataContextChanged += (_, _) => AttachViewModel(DataContext as ConsoleViewModel);
        // 앱이 배경으로 가면 눌린 키를 모두 뗀다(게스트에 Alt 등이 눌린 채 남지 않게)
        Action<bool> onForeground = foreground =>
        {
            if (foreground) return;

            _surface.ReleaseAllKeys();
            CancelRdpResize();
        };
        Services.AppLifecycle.ForegroundChanged += onForeground;
        NavigatedFrom += (_, e) =>
        {
            if (e.NavigationType is NavigationType.Pop or NavigationType.PopToRoot or NavigationType.Remove)
                Services.AppLifecycle.ForegroundChanged -= onForeground;
            _surface.Gestures.Cancel();
            _surface.ReleaseAllKeys();
            CancelRdpResize();
        };
        // RDP: 화면 크기가 바뀌면(회전·창 크기) 잠시 뒤 게스트 해상도를 맞춘다
        _surface.SizeChanged += (_, _) => ScheduleRdpResize();
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
        if (_vm is not null)
        {
            _vm.SessionChanged -= HookSession;
            _vm.BeforeConnect = null;
        }

        _vm = vm;
        if (vm is null) return;

        Header = vm.Title;
        _title.Text = vm.Title;
        // 다시 연결하면 세션만 바뀐다 — 화면(확대·이동·모드·메뉴)은 그대로 두고 새 세션에 다시 잇는다
        vm.SessionChanged += HookSession;
        vm.BeforeConnect = PrepareSession;
        HookSession(vm.Vnc);
        _ = LoadSettingsAsync();
        // 데스크톱은 마우스로 바로 다루므로 터치 모드(누른 곳 클릭)가 기본
        if (PlatformInfo.IsDesktop) _surface.Gestures.Mode = TouchInputMode.Touch;
        _mode.Content = ModeLabel();
    }

    /// <summary>원격 해상도가 바뀌어 화면을 다시 만들도록 이미 부탁했다 — 그 사이 오는 프레임마다 또 부탁하지 않게.</summary>
    private int _resizePending;

    private void HookSession(IConsoleSession session)
    {
        bool Current() => _vm?.Vnc == session;

        _guestClipboard = null;
        Interlocked.Exchange(ref _resizePending, 0);
        session.Connected += (w, h) => Dispatcher.UIThread.Post(() =>
        {
            if (!Current()) return;

            AttachSurface(session, w, h);
            _ = OnConnectedAsync();
        });
        session.FrameReceived += (x, y, w, h) =>
        {
            if (!Current()) return;

            // 해상도가 바뀌었으면(게스트 화면 크기 변경) 비트맵을 다시 만든다 — 한 번만 부탁하고, 다시 만들 때 전체를 올린다
            if (session.Width != (int)_surface.Viewport.Remote.Width || session.Height != (int)_surface.Viewport.Remote.Height)
            {
                if (Interlocked.Exchange(ref _resizePending, 1) == 0)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (Current()) AttachSurface(session, session.Width, session.Height);
                        else Interlocked.Exchange(ref _resizePending, 0);
                    });
            }
            else
            {
                _surface.MarkDirty(x, y, w, h);
            }
        };
        // 게스트 커서 모양 — 이후 커서는 이 모양으로 손가락·마우스 자리에 바로 그린다(서버 응답을 기다리지 않음)
        session.CursorShape += shape => Dispatcher.UIThread.Post(() =>
        {
            if (Current()) _surface.SetCursorShape(shape);
        });
        // RDP: 게스트가 기본 커서로 돌아갔다 — 받은 모양을 지운다
        session.CursorDefault += () => Dispatcher.UIThread.Post(() =>
        {
            if (Current()) _surface.ClearCursorShape();
        });
        session.ClipboardReceived += text => Dispatcher.UIThread.Post(() =>
        {
            if (Current()) OnGuestClipboard(text);
        });
        if (session is ProxmoxVncSession vnc)
            vnc.Bell += () => Dispatcher.UIThread.Post(() =>
            {
                if (Current()) FlashBell();
            });
    }

    /// <summary>원격 화면 크기에 맞춰 화면을 (다시) 만든다 — 해상도 변경 부탁 표시도 여기서 지운다.</summary>
    private void AttachSurface(IConsoleSession session, int width, int height)
    {
        Interlocked.Exchange(ref _resizePending, 0);
        _surface.Attach(width, height, () => session.Framebuffer);
    }

    private async Task OnConnectedAsync()
    {
        await LoadSettingsAsync(); // 설정 화면에서 바꿨을 수 있다
        ScheduleRdpResize();
    }

    // ------------------------------------------------------------- 벨

    private int _bellGeneration;

    /// <summary>받은 벨 수(검사용).</summary>
    public int BellCount { get; private set; }

    private void FlashBell()
    {
        BellCount++;
        _bellFlash.IsVisible = true;
        var generation = ++_bellGeneration;
        // 헤드리스 검사에서도 돌도록 타이머 대신 Task.Delay + Post
        _ = Task.Delay(200).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (generation == _bellGeneration) _bellFlash.IsVisible = false;
        }), TaskScheduler.Default);
    }

    // ------------------------------------------------------------- RDP 해상도 맞춤

    public const int RdpResizeDelayMs = 400;

    private ConsoleSettings _settings = new();
    private int _rdpResizeGeneration;

    /// <summary>마지막으로 게스트에 부탁한 해상도(검사용).</summary>
    public (int Width, int Height)? LastRdpResize { get; private set; }

    private async Task LoadSettingsAsync()
    {
        _settings = await new ConsoleSettingsStore().LoadAsync(); // 읽기 실패는 기본값
    }

    /// <summary>
    ///     보기 크기(논리 단위) × 화면 배율 → RDP 해상도(물리 픽셀). 가로·세로 짝수, 200~8192.
    ///     너무 작은(아직 배치 전) 크기는 null.
    /// </summary>
    public static (int Width, int Height)? RdpSizeFor(Size view, double renderScaling)
    {
        if (view.Width < 100 || view.Height < 100 || renderScaling <= 0) return null;

        var width = Math.Clamp((int)Math.Round(view.Width * renderScaling), 200, 8192) & ~1;
        var height = Math.Clamp((int)Math.Round(view.Height * renderScaling), 200, 8192) & ~1;
        return (width, height);
    }

    /// <summary>연결 직전 — RDP 는 지금 화면 크기를 처음 해상도로(연결 뒤 바꾸면 게스트가 화면을 다시 그린다).</summary>
    private void PrepareSession(IConsoleSession session)
    {
        if (session is Core.Rdp.ProxmoxRdpSession rdp && _settings.RdpDynamicResolution
                                                       && RdpSizeFor(_surface.Bounds.Size, _surface.RenderScaling) is { } size)
            rdp.InitialSize = size;
    }

    /// <summary>
    ///     화면 크기가 바뀌었다 — 크기가 자리 잡도록 잠시(<see cref="RdpResizeDelayMs" />) 기다렸다 게스트 해상도를 맞춘다.
    ///     DispatcherTimer 는 헤드리스 검사에서 돌지 않아 Task.Delay + Post 와 세대 번호로 마지막 것만 적용한다.
    /// </summary>
    private void ScheduleRdpResize()
    {
        if (_vm?.Protocol != ConsoleProtocol.Rdp) return;

        var generation = ++_rdpResizeGeneration;
        _ = Task.Delay(RdpResizeDelayMs).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (generation == _rdpResizeGeneration) ApplyRdpResize();
        }), TaskScheduler.Default);
    }

    private void CancelRdpResize()
    {
        _rdpResizeGeneration++;
    }

    private void ApplyRdpResize()
    {
        if (_vm is not { Protocol: ConsoleProtocol.Rdp, IsConnected: true } vm || !_settings.RdpDynamicResolution) return;
        // 직접 확대·채우기·1:1 은 사용자가 고른 배율 — 맞춤일 때만. 화면 키보드로 줄어든 크기는 맞추지 않는다
        if (_surface.Viewport.Mode != ViewMode.Fit || IsInputPaneOpen() || !Services.AppLifecycle.IsForeground) return;
        if (TopLevel.GetTopLevel(this) is null) return; // 다른 화면이 위에 있다
        if (RdpSizeFor(_surface.Bounds.Size, _surface.RenderScaling) is not { } size) return;
        if (size.Width == vm.Vnc.Width && size.Height == vm.Vnc.Height) return;

        LastRdpResize = size;
        vm.Vnc.RequestDesktopSize(size.Width, size.Height);
    }

    private bool IsInputPaneOpen()
    {
        return TopLevel.GetTopLevel(this)?.InputPane?.State == Avalonia.Controls.Platform.InputPaneState.Open;
    }

    // ------------------------------------------------------------- 클립보드

    /// <summary>클립보드 입력으로 한 번에 보낼 최대 글자 수(게스트 입력 폭주 방지).</summary>
    public const int MaxTypedChars = 4000;

    /// <summary>
    ///     마지막으로 받은 게스트 클립보드. 받을 때마다 기기 클립보드에 넣지 않고 [복사(게스트에서)]를 누를 때 넣는다 —
    ///     게스트에서 복사할 때마다 기기 클립보드가 몰래 바뀌면(Android 13+ 는 그때마다 알림) 당황스럽다.
    /// </summary>
    private string? _guestClipboard;

    private TaskCompletionSource<string>? _clipboardWait;

    private void OnGuestClipboard(string text)
    {
        _guestClipboard = text;
        _clipboardWait?.TrySetResult(text);
    }

    private async Task<string?> ReadDeviceClipboardAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return null;

        return await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard);
    }

    /// <summary>기기 클립보드 → 게스트 클립보드(VM 디스플레이 clipboard=vnc + 게스트 에이전트일 때 게스트에서 붙여넣기).</summary>
    private async Task PasteToGuestAsync()
    {
        if (_vm is not { } vm) return;

        var text = await ReadDeviceClipboardAsync();
        if (string.IsNullOrEmpty(text))
        {
            Services.AppServices.Dialogs.Toast(L.T("Console_ClipboardEmpty"));
            return;
        }

        await vm.Vnc.SendClipboardAsync(text);
        Services.AppServices.Dialogs.Toast(L.T("Console_PastedToGuest"));
    }

    /// <summary>기기 클립보드 글을 키 입력으로 — 게스트 클립보드가 이어져 있지 않아도(로그인 화면 등) 들어간다.</summary>
    private async Task TypeClipboardAsync()
    {
        var text = await ReadDeviceClipboardAsync();
        if (string.IsNullOrEmpty(text))
        {
            Services.AppServices.Dialogs.Toast(L.T("Console_ClipboardEmpty"));
            return;
        }

        text = text.ReplaceLineEndings("\n");
        TypeText(text.Length > MaxTypedChars ? text[..MaxTypedChars] : text);
    }

    /// <summary>게스트 클립보드 → 기기 클립보드. 지금 것을 달라고 요청하고 잠깐 기다린다(못 받으면 마지막으로 받은 것).</summary>
    private async Task CopyFromGuestAsync()
    {
        if (_vm is not { } vm) return;

        var wait = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _clipboardWait = wait;
        if (vm.IsConnected)
        {
            await vm.Vnc.RequestClipboardAsync();
            await Task.WhenAny(wait.Task, Task.Delay(700));
        }

        _clipboardWait = null;
        var text = wait.Task.IsCompletedSuccessfully ? wait.Task.Result : _guestClipboard;
        if (string.IsNullOrEmpty(text))
        {
            Services.AppServices.Dialogs.Toast(L.T("Console_GuestClipboardEmpty"));
            return;
        }

        await ClipboardHelper.SetTextAsync(text);
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
        TapKey(SoftKeys.XtForKeysym(keysym), keysym);
    }

    /// <summary>키 하나(스캔코드 + keysym)를 눌렀다 뗀다 — 서버가 확장 키를 알면 키 위치로, 모르면 keysym 으로.</summary>
    public void TapKey(int xt, int keysym)
    {
        SendWithModifiers([(xt, keysym, true), (xt, keysym, false)]);
    }

    /// <summary>고정해 둔 Ctrl·Alt·Win 으로 감싸 보내고 고정을 푼다.</summary>
    private void SendWithModifiers(List<(int Xt, int Keysym, bool Down)> events)
    {
        if (_vm is null) return;

        var modifiers = new List<(int Xt, int Keysym)>();
        if (_ctrl.IsChecked == true) modifiers.Add((SoftKeys.XtControlL, KeySyms.ControlL));
        if (_alt.IsChecked == true) modifiers.Add((SoftKeys.XtAltL, KeySyms.AltL));
        if (_super.IsChecked == true) modifiers.Add((SoftKeys.XtSuperL, KeySyms.SuperL));
        foreach (var (xt, keysym) in modifiers) _vm.SendScanKey(xt, keysym, true);
        foreach (var (xt, keysym, down) in events) _vm.SendScanKey(xt, keysym, down);
        for (var i = modifiers.Count - 1; i >= 0; i--) _vm.SendScanKey(modifiers[i].Xt, modifiers[i].Keysym, false);
        if (modifiers.Count > 0) _ctrl.IsChecked = _alt.IsChecked = _super.IsChecked = false;
    }

    /// <summary>
    ///     글을 키 입력으로 보낸다 — 글자마다 미국 배열 키 + Shift, 한글은 두벌식 키(<see cref="SoftKeys" />).
    ///     고정 키는 첫 글자에만. 키로 바꿀 수 없는 글자는 알림으로 알려 클립보드 붙여넣기를 권한다.
    /// </summary>
    private void TypeText(string text)
    {
        if (_vm is null) return;

        var events = new List<(int Xt, int Keysym, bool Down)>();
        var unsent = new List<int>();
        var first = true;
        foreach (var rune in text.EnumerateRunes())
        {
            events.Clear();
            SoftKeys.CharEvents(rune.Value, _vm.GuestCapsLock, events, unsent, _vm.Protocol == ConsoleProtocol.Rdp);
            if (first)
                SendWithModifiers(events);
            else
                foreach (var (xt, keysym, down) in events)
                    _vm.SendScanKey(xt, keysym, down);
            first = false;
        }

        if (unsent.Count > 0)
            Services.AppServices.Dialogs.Toast(L.T("Console_CharNotSent",
                string.Concat(unsent.Distinct().Take(8).Select(char.ConvertFromUtf32))));
    }

    private void OnInputText(object? sender, TextChangedEventArgs e)
    {
        var text = _input.Text;
        if (string.IsNullOrEmpty(text)) return;

        // 화면 키보드의 Enter 가 글자('\n')로 들어올 수도 있다(여러 줄 칸) — Return 키로(SoftKeys)
        _input.Text = string.Empty;
        TypeText(text);
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
