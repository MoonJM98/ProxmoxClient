using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Node;

/// <summary>
///     Ceph 화면 앞의 확인 — Ceph 가 설치되지 않았으면(서버가 "binary not installed" HTTP 500) 원문 오류 대신
///     웹 UI 처럼 가운데에 "Ceph 가 설치되지 않았습니다" 를 띄운다. 설치는 root@pam 만 할 수 있으므로 root 에게만 설치 버튼을,
///     다른 사용자에게는 root 로 로그인하라고 알린다. 설치돼 있으면 원래 화면을 그대로 만든다.
/// </summary>
internal static class CephGate
{
    private const string RootUser = "root@pam";

    /// <param name="node">설치할 노드 — 데이터센터 화면처럼 정해지지 않았으면 온라인 노드 중 첫 번째.</param>
    /// <param name="probe">설치 여부를 확인할 가벼운 조회(설치 안 됐으면 500 "not installed").</param>
    public static UIElement Wrap(ProxmoxApiClient api, string? node, Func<Task> probe, Func<UIElement> content)
    {
        var host = new Grid();
        var notice = Notice(api, node);
        notice.Visibility = Visibility.Collapsed;
        host.Children.Add(notice);
        UIElement? built = null;
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
                notice.Visibility = Visibility.Visible; // 다시 이 화면을 열면 다시 확인한다(설치 뒤 바로 보이게)
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 다른 오류(연결 끊김 등)는 원래 화면이 자기 상태 줄에 알린다
                App.Log($"[Ceph] 설치 확인 실패: {ex.Message}");
            }
            finally
            {
                probing = false;
            }

            notice.Visibility = Visibility.Collapsed;
            built = content();
            host.Children.Insert(0, built);
        };
        return host;
    }

    private static Border Notice(ProxmoxApiClient api, string? node)
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
            var install = new Button
            {
                Content = Loc.T("CephTab_Install"), Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = Application.Current.TryFindResource("AccentButton") as Style
            };
            if (Application.Current.TryFindResource("IconTerminal") is Geometry icon) IconAssist.SetIcon(install, icon);
            install.Click += async (_, _) => await InstallAsync(api, node);
            panel.Children.Add(install);
        }

        return new Border
        {
            Child = panel, Padding = new Thickness(22, 16, 22, 16), CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            Background = Application.Current.TryFindResource("BrushPanel") as Brush,
            BorderBrush = Application.Current.TryFindResource("BrushBorder") as Brush
        };
    }

    /// <summary>웹 UI 처럼 노드 셸에서 설치 마법사(pveceph install)를 띄운다.</summary>
    private static async Task InstallAsync(ProxmoxApiClient api, string? node)
    {
        try
        {
            node ??= (await api.GetNodesAsync())
                .FirstOrDefault(n => string.Equals(n.Status, "online", StringComparison.OrdinalIgnoreCase))?.Node;
        }
        catch (Exception ex) when (ex is ProxmoxApiException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException)
        {
            App.Log($"[Ceph] 노드 목록 읽기 실패: {ex.Message}");
        }

        if (node is not null) ConsoleWindows.ShowNodeShell(api, node, "ceph_install");
    }
}
