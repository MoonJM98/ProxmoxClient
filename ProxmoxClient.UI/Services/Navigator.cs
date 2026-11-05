using Avalonia.Controls;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Services;

/// <summary>
///     화면 이동 — 뷰모델을 넘기면 맞는 화면을 보여 준다. 뷰모델은 화면 형식을 몰라도 된다.
///     모바일: 페이지를 스택에 쌓는다. 데스크톱: 현재 페이지가 상세 창(<see cref="IDetailHost" />)을 가지고 있으면
///     새 페이지 대신 그 창에 띄운다(목록+상세 분할 화면).
/// </summary>
public sealed class Navigator
{
    private NavigationPage? _navigation;

    internal void Attach(NavigationPage navigation)
    {
        _navigation = navigation;
    }

    public Task PushAsync(ViewModelBase viewModel)
    {
        if (Navigation.CurrentPage is IDetailHost host && host.TryShowDetail(viewModel)) return Task.CompletedTask;

        return Navigation.PushAsync(Views.PageFactory.Create(viewModel));
    }

    /// <summary>
    ///     화면 하나를 새 페이지로 쌓는다(옮겨 온 탭·표·편집 화면). 데스크톱도 같은 화면 스택을 쓴다 —
    ///     대화상자·확인 창이 늘 같은 창 위에 뜬다.
    /// </summary>
    public Task PushViewAsync(string title, Avalonia.Controls.Control content)
    {
        return Navigation.PushAsync(new ContentPage { Header = title, Content = content });
    }

    public Task PopAsync()
    {
        return Navigation.PopAsync();
    }

    public Task PopToRootAsync()
    {
        return Navigation.PopToRootAsync();
    }

    private NavigationPage Navigation =>
        _navigation ?? throw new InvalidOperationException("Navigator is not attached.");
}

/// <summary>상세 화면을 자기 안에 띄울 수 있는 페이지(데스크톱 분할 화면).</summary>
public interface IDetailHost
{
    /// <summary>이 뷰모델을 상세 창에 띄웠으면 true. 받지 않는 형식이면 false(페이지로 쌓인다).</summary>
    bool TryShowDetail(ViewModelBase viewModel);
}
