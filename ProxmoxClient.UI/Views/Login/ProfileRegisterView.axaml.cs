using Avalonia.Controls;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.UI.Views.Login;

/// <summary>새 서버 등록 화면. <paramref name="registered" /> 은 저장이 끝난 프로필을 받는다(목록 갱신용).</summary>
public partial class ProfileRegisterView : UserControl
{
    /// <summary>XAML 로더용 — 등록 뒤 할 일이 없는 빈 화면(실제 화면은 <see cref="ProfileRegisterView(Action{ConnectionProfile}?)" /> 로 만든다).</summary>
    public ProfileRegisterView() : this(null)
    {
    }

    public ProfileRegisterView(Action<ConnectionProfile>? registered)
    {
        InitializeComponent();
        DataContext = new ProfileRegisterViewModel(registered);
    }
}
