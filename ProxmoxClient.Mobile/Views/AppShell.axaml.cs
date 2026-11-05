using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ProxmoxClient.Mobile.Services;
using ProxmoxClient.Mobile.ViewModels;

namespace ProxmoxClient.Mobile.Views;

public partial class AppShell : UserControl
{
    private readonly NavigationPage _navigation = new();
    private TopLevel? _topLevel;

    public AppShell()
    {
        InitializeComponent();
        DataContext = AppServices.Dialogs;
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
        ClipboardHelper.TopLevel = _topLevel;

        if (_navigation.CurrentPage is null) await AppServices.Navigator.PushAsync(new ProfilesViewModel());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null) _topLevel.BackRequested -= OnBackRequested;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>뒤로 가기 — 대화상자가 열려 있으면 닫기만 한다. 진행 중에는 무시.</summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (AppServices.Dialogs.IsBusyVisible || AppServices.Dialogs.TryCancelCurrent()) e.Handled = true;
    }
}
