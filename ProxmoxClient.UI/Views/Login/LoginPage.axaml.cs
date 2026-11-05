using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ProxmoxClient.UI.Views.Login;

/// <summary>
///     데스크톱 로그인 화면(WPF LoginWindow). 창 머리글 대신 브랜드 머리글을 쓰므로 페이지 막대는 숨긴다.
///     "종료"는 창을 닫는다(데스크톱 앱은 메인 창을 닫으면 끝난다).
/// </summary>
public partial class LoginPage : ContentPage
{
    public LoginPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        (TopLevel.GetTopLevel(this) as Window)?.Close();
    }
}
