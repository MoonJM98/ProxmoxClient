using Avalonia.Controls;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views;

/// <summary>뷰모델 → 페이지. 리플렉션 없이 직접 만든다(트리밍 안전).</summary>
public static class PageFactory
{
    public static Page Create(ViewModelBase viewModel)
    {
        // 데스크톱 로그인 후 화면 — WPF 메인 창과 같은 배치(Views/Main). 같은 세션을 뷰모델이 이어받는다
        if (viewModel is ClusterViewModel cluster && PlatformInfo.IsDesktop)
        {
            cluster.HandOff();
            return Host(new Main.MainView(), new Main.MainViewModel(cluster.Session));
        }

        Page page = viewModel switch
        {
            // 데스크톱·태블릿은 WPF 와 같은 로그인·서버 설정 화면, 휴대폰은 모바일 화면
            ProfilesViewModel => PlatformInfo.IsDesktop ? new Login.LoginPage() : new ProfilesPage(),
            ProfileEditViewModel => PlatformInfo.IsDesktop ? new Login.ProfileDialogPage() : new ProfileEditPage(),
            // 휴대폰 탭 화면(데스크톱·태블릿은 위에서 메인 창으로)
            ClusterViewModel => new ClusterPage(),
            GuestDetailViewModel => new GuestDetailPage(),
            NodeDetailViewModel => new NodeDetailPage(),
            TaskLogViewModel => new TaskLogPage(),
            ConsoleViewModel => new Console.ConsolePage(),
            TerminalViewModel => new Terminal.TerminalPage(),
            _ => throw new ArgumentOutOfRangeException(nameof(viewModel), viewModel.GetType().Name, null)
        };
        return Host(page, viewModel);
    }

    /// <summary>화면에 뷰모델을 붙이고, 화면 진입·이탈을 뷰모델에 알린다.</summary>
    private static Page Host(Page page, ViewModelBase viewModel)
    {
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
