using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Guest.Hardware;

/// <summary>
///     하드웨어 하위 탭 — WPF GuestNavigator 의 "hardware" 칸(SubTabsView)과 같다: 위에 밑줄 탭 줄(ListBox.SubTabs,
///     WrapPanel), 아래에 고른 탭의 내용. 내용은 처음 고를 때 만든다. 서버가 못 쓰는 탭은 두지 않는다.
/// </summary>
public sealed class HardwareSubTabs : DockPanel
{
    private readonly IReadOnlyList<SubTab> _tabs;
    private readonly Dictionary<int, Control> _built = [];
    private readonly ListBox _bar = new()
    {
        Classes = { "SubTabs" },
        Margin = new Thickness(16, 10, 16, 4),
        BorderThickness = new Thickness(0, 0, 0, 1),
        ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel())
    };
    private readonly ContentControl _body = new();

    public HardwareSubTabs(IReadOnlyList<SubTab> tabs)
    {
        _tabs = tabs.Where(t => t.Requires is not { IsAvailable: false }).ToList();
        _bar.BorderBrush = HardwareInput.Brush("BrushBorder");
        ScrollViewer.SetHorizontalScrollBarVisibility(_bar, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        foreach (var tab in _tabs) _bar.Items.Add(new ListBoxItem { Content = Loc.T(tab.LabelKey) });

        _bar.SelectionChanged += (_, _) => _body.Content = _bar.SelectedIndex >= 0 ? Get(_bar.SelectedIndex) : null;
        DockPanel.SetDock(_bar, Dock.Top);
        Children.Add(_bar);
        Children.Add(_body);
        if (_tabs.Count > 0) _bar.SelectedIndex = 0;
    }

    /// <summary>게스트 하드웨어 하위 탭 — "간편"(HardwareTab) · "목록"(HardwareView, 웹 UI 와 같은 화면).</summary>
    public static HardwareSubTabs For(ProxmoxApiClient api, PveResource guest)
    {
        return new HardwareSubTabs(
        [
            new SubTab("GuestHardware_Quick", () => new Tabs.HardwareTab(api, guest)),
            // 웹 UI 와 같은 하드웨어(VM)·리소스(CT) 화면
            new SubTab("GuestHardware_All", () => new HardwareView(api, guest))
        ]);
    }

    private Control Get(int index)
    {
        if (!_built.TryGetValue(index, out var view))
        {
            view = _tabs[index].Create();
            _built[index] = view;
        }

        return view;
    }
}
