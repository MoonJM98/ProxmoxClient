using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Controls;

/// <summary>콘솔 창에서 게스트가 정지 상태일 때 보여주는 안내 + [게스트 시작] 화면.</summary>
public partial class GuestStoppedPanel : UserControl
{
    public GuestStoppedPanel()
    {
        InitializeComponent();
    }

    /// <summary>[게스트 시작] 클릭.</summary>
    public event EventHandler? StartRequested;

    /// <summary>정지 상태 안내. 전원 권한이 없으면 시작 버튼 없이 대기 안내만 표시.</summary>
    public void ShowStopped(bool canStart)
    {
        TitleText.Text = Loc.T("GuestStopped_Title");
        DetailText.Text = Loc.T(canStart ? "GuestStopped_HintCanStart" : "GuestStopped_HintWait");
        BtnStart.Visibility = canStart ? Visibility.Visible : Visibility.Collapsed;
        BtnStart.IsEnabled = true;
        Visibility = Visibility.Visible;
    }

    /// <summary>시작 요청 후 부팅·연결 대기 안내(시작 버튼 비활성).</summary>
    public void ShowWaiting(string message)
    {
        TitleText.Text = Loc.T("GuestStopped_Starting");
        DetailText.Text = message;
        BtnStart.IsEnabled = false;
        Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        StartRequested?.Invoke(this, EventArgs.Empty);
    }
}