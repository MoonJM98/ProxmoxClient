using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     웹 UI 처럼 왼쪽 탭으로 화면을 바꾸는 창 — 게스트·노드·데이터센터가 같은 틀을 쓴다.
///     탭 목록은 만드는 쪽이 권한에 맞게 걸러서 넘기므로, 여기서는 받은 것만 보여 준다.
/// </summary>
public partial class NavWindow : Window, IWindowFooterHost, IRefreshHost
{
    /// <summary>탭 내용은 한 번 만들면 다시 쓴다 — 탭을 오갈 때마다 서버를 다시 부르지 않게.</summary>
    private readonly Dictionary<string, UIElement> _content = new(StringComparer.Ordinal);

    /// <summary>화면에 올라와 있는 탭(원래 부모 패널)별 꼬리말 — 하위 탭이 겹치면 여럿이 함께 보인다.</summary>
    private readonly Dictionary<FrameworkElement, FrameworkElement> _footers = new(ReferenceEqualityComparer.Instance);

    public NavWindow(string title, string header, string headerIconKey, IReadOnlyList<NavTab> tabs,
        string? initialTabId = null)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);

        Title = title;
        HeaderText.Text = header;
        HeaderIcon.Data = TryFindResource(headerIconKey) as Geometry;

        var entries = tabs.Where(tab => tab.Requires is not { IsAvailable: false })
            .Select(tab => new TabEntry(tab)).ToList();
        TabList.ItemsSource = entries;
        TabList.SelectedItem = entries.FirstOrDefault(t => t.Tab.Id == initialTabId) ?? entries.FirstOrDefault();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabList.SelectedItem is not TabEntry entry)
        {
            TabContent.Content = null;
            return;
        }

        if (!_content.TryGetValue(entry.Tab.Id, out var view))
        {
            view = entry.Tab.Create();
            _content[entry.Tab.Id] = view;
        }

        TabContent.Content = view;
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshButton.RefreshVisible(TabContent);
    }

    /// <summary>F5 — 머리말 새로고침과 같다.</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F5 && RefreshButton.RefreshVisible(TabContent))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    public void ShowFooter(FrameworkElement owner, FrameworkElement footer)
    {
        if (_footers.ContainsKey(owner) || footer.Parent is not null) return;

        _footers[owner] = footer;
        FooterSlot.Children.Add(footer);
    }

    public void HideFooter(FrameworkElement owner)
    {
        if (!_footers.Remove(owner, out var footer)) return;

        FooterSlot.Children.Remove(footer);
    }

    /// <summary>목록 바인딩용 래퍼 — 탭 이름은 현재 언어로 조회한다.</summary>
    private sealed class TabEntry(NavTab tab)
    {
        public NavTab Tab { get; } = tab;
        public string IconKey => Tab.IconKey;
        public string Label => Loc.T(Tab.LabelKey);
    }
}
