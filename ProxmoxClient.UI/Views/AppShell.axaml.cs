using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views;

public partial class AppShell : UserControl
{
    private readonly NavigationPage _navigation = new();
    private TopLevel? _topLevel;

    public AppShell()
    {
        InitializeComponent();
        DataContext = AppServices.Dialogs;
        // 안전 영역(상태 표시줄·탐색 막대)은 페이지(PageNavigationHost)와 아래 가림막이 각자 피한다 —
        // 최상위가 셸 전체에 또 여백을 주면 두 번 띄워진다
        TopLevel.SetAutoSafeAreaPadding(this, false);
        AddHandler(GotFocusEvent, OnFocusChanged, RoutingStrategies.Bubble);
        AppServices.Navigator.Attach(_navigation);
        Host.Page = _navigation;
    }

    /// <summary>
    ///     셸은 앱에 하나 — Android 는 액티비티가 다시 만들어져도(화면 회전 외 설정 변경, 시스템이 액티비티만 정리 등)
    ///     같은 셸을 새 화면에 다시 붙인다. 그래야 연결된 터미널·콘솔 페이지 스택이 그대로 남는다.
    ///     이전 화면에 아직 붙어 있으면 떼어 낸다.
    /// </summary>
    public static AppShell Shared
    {
        get
        {
            var shell = _shared ??= new AppShell();
            switch (shell.Parent)
            {
                case ContentControl owner when ReferenceEquals(owner.Content, shell):
                    owner.Content = null;
                    break;
                case Decorator decorator when ReferenceEquals(decorator.Child, shell):
                    decorator.Child = null;
                    break;
            }

            return shell;
        }
    }

    private static AppShell? _shared;

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 다른 화면에서 옮겨 왔으면 페이지 스택을 다시 넣는다(떼어 낼 때 뺐다 — OnDetachedFromVisualTree)
        if (Host.Content is null) Host.SetCurrentValue(ContentProperty, _navigation);
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.BackRequested += OnBackRequested;
        if (_topLevel?.InputPane is { } pane) pane.StateChanged += OnInputPaneChanged;
        if (_topLevel?.InsetsManager is { } insets)
        {
            insets.SafeAreaChanged += OnSafeAreaChanged;
            ApplySafeArea(insets.SafeAreaPadding);
        }

        AppServices.Dialogs.PropertyChanged += OnDialogsChanged;
        SheetOverlay.PointerPressed += OnSheetScrimPressed;
        PlaceSheet();
        ClipboardHelper.TopLevel = _topLevel;

        if (_navigation.CurrentPage is null) await AppServices.Navigator.PushAsync(new ProfilesViewModel());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null) _topLevel.BackRequested -= OnBackRequested;
        if (_topLevel?.InputPane is { } pane) pane.StateChanged -= OnInputPaneChanged;
        if (_topLevel?.InsetsManager is { } insets) insets.SafeAreaChanged -= OnSafeAreaChanged;
        AppServices.Dialogs.PropertyChanged -= OnDialogsChanged;
        SheetOverlay.PointerPressed -= OnSheetScrimPressed;
        if (ReferenceEquals(ClipboardHelper.TopLevel, _topLevel)) ClipboardHelper.TopLevel = null;
        _topLevel = null;
        ApplyKeyboardInset(0); // 새 화면에 붙을 때 이전 키보드 여백이 남지 않게
        base.OnDetachedFromVisualTree(e);
        // 페이지 스택을 잠깐 뺀다 — 그대로 두면 새 화면에 붙을 때 PageNavigationHost 의 내용 틀(DefaultPageDataTemplate)이
        // 페이지가 이미 같은 presenter 에 붙어 있다고 보고 내용을 비워, 빈 화면이 된다(Avalonia 12.1)
        Host.SetCurrentValue(ContentProperty, null);
    }

    /// <summary>입력 대화상자가 열리면 입력란에 바로 포커스(키보드가 올라온다).</summary>
    private void OnDialogsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DialogService.Sheet)) PlaceSheet();
        if (e.PropertyName == nameof(DialogService.Current) && AppServices.Dialogs.Current is { HasInput: true })
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                DialogInput.Focus();
                DialogInput.SelectAll();
            }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e)
    {
        ApplySafeArea(e.SafeAreaPadding);
    }

    /// <summary>안전 영역 — 가림막 안의 카드·알림을 상태 표시줄·탐색 막대·노치 밖으로.</summary>
    private void ApplySafeArea(Thickness safe)
    {
        _safeArea = safe;
        BusyOverlay.Padding = safe;
        DialogOverlay.Padding = safe;
        PlaceSheet();
        ToastBubble.Margin = new Thickness(24 + safe.Left, 0, 24 + safe.Right, 48 + safe.Bottom);
        if (KeyboardInset > 0) ApplyKeyboardInset(KeyboardInset);
    }

    private Thickness _safeArea;

    /// <summary>
    ///     시트 자리 — 바텀 시트(<see cref="IBottomSheet" />)는 화면 아래 끝에 붙여 위 모서리만 둥글게(탐색 막대 몫은 안쪽 여백),
    ///     나머지는 가운데 카드.
    /// </summary>
    private void PlaceSheet()
    {
        var safe = _safeArea;
        if (AppServices.Dialogs.Sheet is IBottomSheet)
        {
            SheetOverlay.Padding = new Thickness(safe.Left, safe.Top + 24, safe.Right, 0);
            SheetCard.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            SheetCard.Margin = new Thickness(0);
            SheetCard.MaxWidth = 640;
            SheetCard.CornerRadius = new CornerRadius(16, 16, 0, 0);
            SheetCard.Padding = new Thickness(0, 0, 0, safe.Bottom);
        }
        else
        {
            SheetOverlay.Padding = safe;
            SheetCard.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            SheetCard.Margin = new Thickness(16, 24);
            SheetCard.MaxWidth = 560;
            SheetCard.ClearValue(Border.CornerRadiusProperty);
            SheetCard.Padding = new Thickness(0);
        }
    }

    /// <summary>바텀 시트 바깥(어두운 곳)을 누르면 닫는다 — 휴대폰 관례.</summary>
    private void OnSheetScrimPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, SheetOverlay) && AppServices.Dialogs.Sheet is IBottomSheet sheet)
        {
            sheet.Dismiss();
            e.Handled = true;
        }
    }

    /// <summary>
    ///     화면 키보드가 올라오거나 내려갔다. 앱은 화면 끝까지 그리므로(edge-to-edge) Android 가 창을 줄여 주지 않는다 —
    ///     키보드가 가린 높이만큼 아래를 비워 화면을 줄이고, 입력 중인 칸을 보이는 곳으로 끌어온다.
    /// </summary>
    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        var inset = e.NewState == InputPaneState.Open ? Math.Max(0, Bounds.Height - e.EndRect.Top) : 0;
        ApplyKeyboardInset(inset);
    }

    /// <summary>
    ///     키보드가 가린 높이(화면 아래 끝부터, 0 이면 키보드 없음) — 그만큼 아래를 비우고 포커스 칸을 보이게 한다.
    ///     페이지와 가림막은 이미 탐색 막대(안전 영역 아래)만큼 띄우므로 그 몫은 빼고 비운다(두 번 띄우지 않게).
    /// </summary>
    public void ApplyKeyboardInset(double inset)
    {
        KeyboardInset = inset;
        Padding = new Thickness(0, 0, 0, inset > 0 ? Math.Max(0, inset - _safeArea.Bottom) : 0);
        if (inset > 0) RevealFocused();
    }

    public double KeyboardInset { get; private set; }

    /// <summary>키보드가 떠 있는 동안 다른 칸으로 옮겨도 그 칸을 보이게.</summary>
    private void OnFocusChanged(object? sender, FocusChangedEventArgs e)
    {
        if (KeyboardInset > 0) RevealFocused();
    }

    /// <summary>줄어든 화면이 다시 배치된 뒤 포커스 칸을 스크롤 영역 안으로(여백을 조금 둔다).</summary>
    private void RevealFocused()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control focused)
                focused.BringIntoView(new Rect(-8, -8, focused.Bounds.Width + 16, focused.Bounds.Height + 24));
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>뒤로 가기 — 대화상자가 열려 있으면 닫기만 한다. 진행 중이면 취소할 수 있을 때 취소(화면은 그대로).</summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (AppServices.Dialogs.HandleBack()) e.Handled = true;
    }
}
