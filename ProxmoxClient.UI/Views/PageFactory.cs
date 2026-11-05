using Avalonia.Controls;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views;

/// <summary>뷰모델 → 페이지. 리플렉션 없이 직접 만든다(트리밍 안전).</summary>
public static class PageFactory
{
    public static Page Create(ViewModelBase viewModel)
    {
        Page page = viewModel switch
        {
            ProfilesViewModel => new ProfilesPage(),
            ProfileEditViewModel => new ProfileEditPage(),
            // 같은 뷰모델을 화면 형태에 따라 다른 배치로 — 데스크톱은 목록+상세 분할, 모바일은 탭
            ClusterViewModel => PlatformInfo.IsDesktop ? new ClusterDesktopPage() : new ClusterPage(),
            GuestDetailViewModel => new GuestDetailPage(),
            NodeDetailViewModel => new NodeDetailPage(),
            TaskLogViewModel => new TaskLogPage(),
            ConsoleViewModel => new Console.ConsolePage(),
            _ => throw new ArgumentOutOfRangeException(nameof(viewModel), viewModel.GetType().Name, null)
        };
        page.DataContext = viewModel;
        page.NavigatedTo += (_, _) => viewModel.NotifyAppearing();
        page.NavigatedFrom += (_, e) => viewModel.NotifyDisappearing(IsRemoval(e.NavigationType));
        return page;
    }

    /// <summary>상세 창에 넣는 화면(모바일 페이지 안과 같은 UserControl). 상세 화면이 아니면 null.</summary>
    public static Control? CreateDetailView(ViewModelBase viewModel)
    {
        Control? view = viewModel switch
        {
            GuestDetailViewModel => new GuestDetailView(),
            NodeDetailViewModel => new NodeDetailView(),
            TaskLogViewModel => new TaskLogView(),
            _ => null
        };
        if (view is not null) view.DataContext = viewModel;
        return view;
    }

    private static bool IsRemoval(NavigationType type)
    {
        return type is NavigationType.Pop or NavigationType.PopToRoot or NavigationType.PopModal
            or NavigationType.Remove or NavigationType.Replace;
    }
}
