using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Views.Controls;

namespace ProxmoxClient.UI.Views.Node.Tabs;

/// <summary>
///     노드 창의 셸 탭. 노드 셸은 터미널(에뮬레이터)이 있어야 열 수 있는데 아직 옮기지 않았다 — 그래서 열기 버튼은
///     비활성이고 "이관 대기: 터미널" 안내만 보인다. 터미널이 옮겨지면 이 버튼에 연결한다.
/// </summary>
public sealed class NodeShellTab : UserControl
{
    public NodeShellTab()
    {
        var open = new Button
        {
            Content = Loc.T("NodeShell_Open"), Classes = { "AccentButton" }, MinWidth = 170,
            HorizontalAlignment = HorizontalAlignment.Center, IsEnabled = false
        };
        IconAssist.SetIcon(open, NodeUi.Icon("IconExternal"));

        Content = new StackPanel
        {
            MaxWidth = 420, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Avalonia.Controls.Shapes.Path
                {
                    Classes = { "icon" }, Width = 44, Height = 44, Margin = new Thickness(0, 0, 0, 14),
                    HorizontalAlignment = HorizontalAlignment.Center, Data = NodeUi.Icon("IconTerminal")
                },
                new TextBlock
                {
                    Text = Loc.T("NodeShell_Description"), TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center, Classes = { "subtle" }, Margin = new Thickness(0, 0, 0, 18)
                },
                open,
                new TextBlock
                {
                    Text = NodeUi.TerminalPending, Classes = { "DialogStatus" }, TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 14, 0, 0)
                }
            }
        };
    }
}
