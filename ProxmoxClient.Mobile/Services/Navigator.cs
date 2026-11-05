using Avalonia.Controls;
using ProxmoxClient.Mobile.ViewModels;

namespace ProxmoxClient.Mobile.Services;

/// <summary>
///     화면 이동 — 뷰모델을 넘기면 맞는 페이지를 만들어 <see cref="NavigationPage" /> 에 쌓는다.
///     뷰모델은 페이지 형식을 몰라도 된다(페이지 생성은 <see cref="Views.PageFactory" />).
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
        return Navigation.PushAsync(Views.PageFactory.Create(viewModel));
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
