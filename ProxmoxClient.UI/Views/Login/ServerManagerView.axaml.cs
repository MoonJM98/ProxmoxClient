using Avalonia.Controls;

namespace ProxmoxClient.UI.Views.Login;

/// <summary>서버 관리 화면 — 열 때마다 저장된 서버 목록을 다시 읽는다.</summary>
public partial class ServerManagerView : UserControl
{
    public ServerManagerView()
    {
        InitializeComponent();
        var model = new ServerManagerViewModel();
        DataContext = model;
        Loaded += (_, _) => _ = model.RefreshAsync();
    }
}
