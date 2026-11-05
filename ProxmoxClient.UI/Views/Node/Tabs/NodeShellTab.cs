using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Views.Controls;

namespace ProxmoxClient.UI.Views.Node.Tabs;

/// <summary>노드 창의 셸 탭 — [셸 열기] 로 노드 셸 터미널(termproxy)을 연다(WPF 는 따로 뜬 창).</summary>
public sealed class NodeShellTab : UserControl
{
    public NodeShellTab(ProxmoxClient.Core.Api.ProxmoxApiClient api, string node)
    {
        var open = new Button
        {
            Content = Loc.T("NodeShell_Open"), Classes = { "AccentButton" }, MinWidth = 170,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        open.Click += async (_, _) => await Services.ConsoleLauncher.OpenNodeShellAsync(api, node);
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
                open
            }
        };
    }
}
