using Avalonia.Controls;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views;

/// <summary>모바일 서버 편집 — PC 서버 설정의 인증서·프록시 상태를 읽고, 열리면 인증 영역을 서버에서 가져온다.</summary>
public partial class ProfileEditPage : ContentPage
{
    public ProfileEditPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => (DataContext as ProfileEditViewModel)?.LoadDesktopState();
        Loaded += (_, _) => (DataContext as ProfileEditViewModel)?.AutoFetchRealms();
    }
}
