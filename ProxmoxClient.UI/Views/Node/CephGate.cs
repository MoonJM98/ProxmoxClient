using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Views.Controls;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Node;

/// <summary>
///     Ceph 화면 앞의 확인 — Ceph 가 설치되지 않았으면(서버가 "binary not installed" HTTP 500) 원문 오류 대신
///     웹 UI 처럼 가운데에 "Ceph 가 설치되지 않았습니다" 를 띄운다. 설치는 root@pam 만 할 수 있으므로 root 에게만 설치 버튼을,
///     다른 사용자에게는 root 로 로그인하라고 알린다. 설치돼 있으면 원래 화면을 그대로 만든다.
/// </summary>
internal static class CephGate
{
    private const string RootUser = "root@pam";

    /// <param name="node">설치할 노드(설치 마법사가 옮겨지면 쓴다).</param>
    /// <param name="probe">설치 여부를 확인할 가벼운 조회(설치 안 됐으면 500 "not installed").</param>
    public static Control Wrap(ProxmoxApiClient api, string? node, Func<Task> probe, Func<Control> content)
    {
        var host = new Grid();
        var notice = Notice(api);
        notice.IsVisible = false;
        host.Children.Add(notice);
        Control? built = null;
        var probing = false;
        host.Loaded += async (_, _) =>
        {
            // 확인 중에 탭을 오가면 Loaded 가 다시 온다 — 화면을 두 번 만들지 않게 한 번만 확인한다
            if (built is not null || probing) return;
            probing = true;
            try
            {
                await probe();
            }
            catch (ProxmoxApiException ex) when (CephTabs.IsNotInstalled(ex))
            {
                notice.IsVisible = true; // 다시 이 화면을 열면 다시 확인한다(설치 뒤 바로 보이게)
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 다른 오류(연결 끊김 등)는 원래 화면이 자기 상태 줄에 알린다
                Debug.WriteLine($"[Ceph] 설치 확인 실패: {ex.Message}");
            }
            finally
            {
                probing = false;
            }

            notice.IsVisible = false;
            built = content();
            host.Children.Insert(0, built);
        };
        return host;
    }

    private static Border Notice(ProxmoxApiClient api)
    {
        var isRoot = string.Equals(api.Profile.UserName, RootUser, StringComparison.OrdinalIgnoreCase);
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T(isRoot ? "CephGate_NotInstalledRoot" : "CephGate_NotInstalledUser"),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 380
        });
        if (isRoot)
        {
            // 설치 마법사는 터미널(노드 셸)에서 도므로 아직 옮기지 않았다 — 버튼은 두고 안내만 보여 준다
            var install = new Button
            {
                Content = Loc.T("CephTab_Install"), Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, Classes = { "AccentButton" }
            };
            IconAssist.SetIcon(install, NodeUi.Icon("IconTerminal"));
            install.Click += async (_, _) => await Ask.InfoAsync(NodeUi.TerminalPending);
            panel.Children.Add(install);
        }

        return new Border
        {
            Child = panel, Padding = new Thickness(22, 16), CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Classes = { "FormPanel" }
        };
    }
}
