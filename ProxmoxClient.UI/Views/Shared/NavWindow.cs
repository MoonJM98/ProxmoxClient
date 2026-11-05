using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Shared;

/// <summary>
///     탭 탐색 화면(데이터센터·노드·스토리지) — WPF NavWindow 와 같은 생성자.
///     데스크톱: 왼쪽 탭 목록 + 내용. 모바일: 탭 목록이 보이고, 고르면 그 탭이 새 페이지로 열린다.
///     <see cref="Show" /> 로 앱 화면 스택에 띄운다(별도 창이 아니라 대화상자·확인 창이 같은 화면 위에 뜬다).
/// </summary>
public sealed class NavWindow : UserControl
{
    private readonly Dictionary<string, Control> _content = new(StringComparer.Ordinal);
    private readonly ContentControl _body = new();
    private readonly IReadOnlyList<NavTab> _tabs;

    public NavWindow(string title, string header, string headerIconKey, IReadOnlyList<NavTab> tabs,
        string? initialTabId = null)
    {
        Title = title;
        _tabs = tabs.Where(tab => tab.Requires is not { IsAvailable: false }).ToList();

        var list = new ListBox { Classes = { "nav" }, Margin = new Thickness(8) };
        foreach (var tab in _tabs) list.Items.Add(TabItemView(tab));

        if (PlatformInfo.IsDesktop)
        {
            list.SelectionChanged += (_, _) =>
                _body.Content = list.SelectedIndex >= 0 ? Get(_tabs[list.SelectedIndex]) : null;
            var initial = _tabs.ToList().FindIndex(t => t.Id == initialTabId);
            list.SelectedIndex = initial >= 0 ? initial : _tabs.Count > 0 ? 0 : -1;
            var side = new Border
            {
                Width = 230, BorderThickness = new Thickness(0, 0, 1, 0),
                Child = new DockPanel
                {
                    Children =
                    {
                        Docked(new TextBlock { Text = header, Classes = { "headline" }, Margin = new Thickness(16, 16, 16, 4) }),
                        new ScrollViewer { Content = list }
                    }
                }
            };
            side.Bind(Border.BackgroundProperty, side.GetResourceObservable("CardBackgroundBrush"));
            side.Bind(Border.BorderBrushProperty, side.GetResourceObservable("CardBorderBrush"));
            DockPanel.SetDock(side, Dock.Left);
            Content = new DockPanel { Children = { side, _body } };
        }
        else
        {
            // 모바일: 목록을 누르면 탭 내용을 새 페이지로
            list.SelectionChanged += async (_, _) =>
            {
                if (list.SelectedIndex < 0) return;

                var tab = _tabs[list.SelectedIndex];
                list.SelectedIndex = -1;
                await AppServices.Navigator.PushViewAsync(L.T(tab.LabelKey), Get(tab));
            };
            Content = new ScrollViewer { Content = list };
        }
    }

    public string Title { get; }

    /// <summary>앱 화면 스택에 띄운다(WPF 의 Show 와 같은 이름).</summary>
    public void Show()
    {
        _ = AppServices.Navigator.PushViewAsync(Title, this);
    }

    private static T Docked<T>(T control) where T : Control
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
    }

    private Control TabItemView(NavTab tab)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if ((this.TryFindResource(tab.IconKey, out var icon) ||
             Application.Current?.TryGetResource(tab.IconKey, Application.Current.ActualThemeVariant, out icon) == true) &&
            icon is Geometry geometry)
            row.Children.Add(new Avalonia.Controls.Shapes.Path { Data = geometry, Classes = { "icon" } });
        row.Children.Add(new TextBlock { Text = L.T(tab.LabelKey), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private Control Get(NavTab tab)
    {
        if (!_content.TryGetValue(tab.Id, out var view))
        {
            view = tab.Create();
            _content[tab.Id] = view;
        }

        // 모바일은 같은 내용을 다시 열 수 있으므로 이전 부모에서 떼어 낸다
        if (view.Parent is ContentControl owner) owner.Content = null;
        else if (view.Parent is ContentPage page) page.Content = null;
        return view;
    }
}

/// <summary>위쪽 칩 모양 하위 탭 — WPF SubTabsView 와 같은 생성자.</summary>
public sealed class SubTabsView : UserControl
{
    private readonly Dictionary<int, Control> _built = [];
    private readonly IReadOnlyList<(string LabelKey, Func<Control> Create)> _tabs;
    private readonly ContentControl _body = new();

    public SubTabsView(IReadOnlyList<SubTab> tabs)
        : this(tabs.Where(t => t.Requires is not { IsAvailable: false }).Select(t => (t.LabelKey, t.Create)).ToList())
    {
    }

    public SubTabsView(IReadOnlyList<(string LabelKey, Func<Control> Create)> tabs)
    {
        _tabs = tabs;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 0) };
        var group = "sub" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < tabs.Count; i++)
        {
            var index = i;
            var chip = new RadioButton { Content = L.T(tabs[i].LabelKey), GroupName = group, Classes = { "chip" } };
            chip.IsCheckedChanged += (_, _) =>
            {
                if (chip.IsChecked == true) _body.Content = Get(index);
            };
            bar.Children.Add(chip);
        }

        Content = new DockPanel
        {
            Children =
            {
                Top(new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = bar
                }),
                _body
            }
        };
        if (bar.Children.FirstOrDefault() is RadioButton first) first.IsChecked = true;
    }

    public IReadOnlyList<Control> BuildAll()
    {
        return Enumerable.Range(0, _tabs.Count).Select(Get).ToList();
    }

    private static T Top<T>(T control) where T : Control
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
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
