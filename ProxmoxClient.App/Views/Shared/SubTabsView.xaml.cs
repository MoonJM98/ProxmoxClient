using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>탭 안의 하위 탭 — 웹 UI 의 '디스크 › LVM/ZFS…' 처럼 한 화면을 몇 갈래로 나눈다.</summary>
public partial class SubTabsView : UserControl
{
    private readonly Dictionary<int, UIElement> _built = [];
    private readonly IReadOnlyList<(string LabelKey, Func<UIElement> Create)> _tabs;

    /// <summary>하위 탭마다 쓰는 API 기능을 알리면, 서버가 못 쓰는 탭은 두지 않는다.</summary>
    public SubTabsView(IReadOnlyList<SubTab> tabs)
        : this(tabs.Where(t => t.Requires is not { IsAvailable: false }).Select(t => (t.LabelKey, t.Create)).ToList())
    {
    }

    public SubTabsView(IReadOnlyList<(string LabelKey, Func<UIElement> Create)> tabs)
    {
        InitializeComponent();
        _tabs = tabs;
        foreach (var (labelKey, _) in tabs)
            TabBar.Items.Add(new ListBoxItem { Content = Loc.T(labelKey) });
        TabBar.SelectedIndex = 0;
    }

    /// <summary>하위 탭 내용 — 처음 고를 때 만든다.</summary>
    public IReadOnlyList<UIElement> BuildAll()
    {
        return Enumerable.Range(0, _tabs.Count).Select(Get).ToList();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        Body.Content = TabBar.SelectedIndex >= 0 ? Get(TabBar.SelectedIndex) : null;
    }

    private UIElement Get(int index)
    {
        if (!_built.TryGetValue(index, out var view))
        {
            view = _tabs[index].Create();
            _built[index] = view;
        }

        return view;
    }
}
