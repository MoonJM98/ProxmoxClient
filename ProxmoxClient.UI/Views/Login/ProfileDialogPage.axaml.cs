using Avalonia.Controls;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views.Login;

/// <summary>
///     서버 설정 화면(WPF ProfileDialog). 화면이 뷰모델을 받으면 데스크톱 칸을 채우고, 열릴 때 자체 서명 인증서를 자동 확인한다.
/// </summary>
public partial class ProfileDialogPage : ContentPage
{
    public ProfileDialogPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ProfileEditViewModel model) model.LoadDesktopState();
        };
        Loaded += (_, _) => (DataContext as ProfileEditViewModel)?.TryAutoVerifyCertificate();
    }
}
