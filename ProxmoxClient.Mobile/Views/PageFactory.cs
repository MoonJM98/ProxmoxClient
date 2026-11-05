using Avalonia.Controls;
using ProxmoxClient.Mobile.ViewModels;

namespace ProxmoxClient.Mobile.Views;

/// <summary>뷰모델 → 페이지. 리플렉션 없이 직접 만든다(트리밍 안전).</summary>
public static class PageFactory
{
    public static Page Create(ViewModelBase viewModel)
    {
        Page page = viewModel switch
        {
            ProfilesViewModel => new ProfilesPage(),
            ProfileEditViewModel => new ProfileEditPage(),
            ClusterViewModel => new ClusterPage(),
            GuestDetailViewModel => new GuestDetailPage(),
            NodeDetailViewModel => new NodeDetailPage(),
            TaskLogViewModel => new TaskLogPage(),
            _ => throw new ArgumentOutOfRangeException(nameof(viewModel), viewModel.GetType().Name, null)
        };
        page.DataContext = viewModel;
        page.NavigatedTo += (_, _) => viewModel.NotifyAppearing();
        page.NavigatedFrom += (_, e) => viewModel.NotifyDisappearing(IsRemoval(e.NavigationType));
        return page;
    }

    private static bool IsRemoval(NavigationType type)
    {
        return type is NavigationType.Pop or NavigationType.PopToRoot or NavigationType.PopModal
            or NavigationType.Remove or NavigationType.Replace;
    }
}
