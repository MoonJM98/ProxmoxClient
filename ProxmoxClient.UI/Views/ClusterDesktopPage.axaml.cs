using Avalonia.Controls;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views;

/// <summary>데스크톱 클러스터 화면 — 고른 항목의 상세를 오른쪽 창에 띄운다(모바일은 새 페이지).</summary>
public partial class ClusterDesktopPage : ContentPage, IDetailHost
{
    private ViewModelBase? _detail;

    public ClusterDesktopPage()
    {
        InitializeComponent();
        NavigatedFrom += (_, e) =>
        {
            if (e.NavigationType is NavigationType.Pop or NavigationType.PopToRoot or NavigationType.Remove
                or NavigationType.Replace)
                ShowDetail(null, null);
        };
    }

    public bool TryShowDetail(ViewModelBase viewModel)
    {
        var view = PageFactory.CreateDetailView(viewModel);
        if (view is null) return false;

        ShowDetail(viewModel, view);
        return true;
    }

    private void ShowDetail(ViewModelBase? viewModel, Control? view)
    {
        // 이전 상세는 스택에서 빠진 페이지처럼 정리한다(주기 새로 고침 중지·요청 취소)
        _detail?.NotifyDisappearing(true);
        _detail = viewModel;

        DetailContent.Content = view;
        DetailPlaceholder.IsVisible = view is null;
        DetailHeader.IsVisible = view is not null;
        DetailTitle.Text = viewModel?.Title;
        DetailReload.IsVisible = viewModel is IReloadable;
        DetailReload.Command = (viewModel as IReloadable)?.ReloadCommand;

        viewModel?.NotifyAppearing();
    }
}
