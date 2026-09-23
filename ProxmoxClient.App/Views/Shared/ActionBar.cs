using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>표가 없는 화면(요약 탭 등) 위에 놓는 작업 버튼 줄 — 선택 행 없이 실행하고 결과를 상태 줄에 쓴다.</summary>
public static class ActionBar
{
    public static void Populate(Panel panel, IEnumerable<TableAction> actions, FrameworkElement host,
        TextBlock status)
    {
        foreach (var action in actions)
        {
            var button = new Button { Content = Loc.T(action.LabelKey), Margin = new Thickness(0, 0, 8, 4) };
            if (host.TryFindResource(action.IconKey) is Geometry icon) IconAssist.SetIcon(button, icon);
            button.Click += async (_, _) =>
            {
                if (action.Confirm is { } confirm && !Confirm(host, confirm(null))) return;

                button.IsEnabled = false;
                try
                {
                    if (await action.Run(null, Window.GetWindow(host)) is { } result) status.Text = result;
                }
                catch (Exception ex)
                {
                    status.Text = Loc.T("TableTab_ActionFailed", ex.Message);
                }
                finally
                {
                    button.IsEnabled = true;
                }
            };
            panel.Children.Add(button);
        }
    }

    private static bool Confirm(FrameworkElement host, string text)
    {
        var title = Loc.T("TableTab_ConfirmTitle");
        var answer = Window.GetWindow(host) is { } owner
            ? ThemedMessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : ThemedMessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes;
    }
}
