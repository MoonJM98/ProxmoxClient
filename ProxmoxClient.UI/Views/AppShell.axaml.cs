using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        AddHandler(GotFocusEvent, OnFocusChanged, RoutingStrategies.Bubble);
        AppServices.Navigator.Attach(_navigation);
        Host.Page = _navigation;
        AppServices.Dialogs.PropertyChanged += (_, e) =>
        {
            // 입력 대화상자가 열리면 입력란에 바로 포커스(키보드가 올라온다)
            if (e.PropertyName == nameof(DialogService.Current) && AppServices.Dialogs.Current is { HasInput: true })
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    DialogInput.Focus();
                    DialogInput.SelectAll();
                }, Avalonia.Threading.DispatcherPriority.Input);
        };
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.BackRequested += OnBackRequested;
        if (_topLevel?.InputPane is { } pane) pane.StateChanged += OnInputPaneChanged;
        ClipboardHelper.TopLevel = _topLevel;

        if (_navigation.CurrentPage is null) await AppServices.Navigator.PushAsync(new ProfilesViewModel());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null) _topLevel.BackRequested -= OnBackRequested;
        if (_topLevel?.InputPane is { } pane) pane.StateChanged -= OnInputPaneChanged;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
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

    /// <summary>키보드가 가린 높이(0 이면 키보드 없음) — 그만큼 아래를 비우고 포커스 칸을 보이게 한다.</summary>
    public void ApplyKeyboardInset(double inset)
    {
        KeyboardInset = inset;
        Padding = new Thickness(0, 0, 0, inset);
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
