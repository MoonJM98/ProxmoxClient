using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Main;

/// <summary>
///     데스크톱 메인 화면 — 줄 더블클릭·오른쪽 클릭 선택·영역 크기 저장 같은 화면 동작만 맡고,
///     규칙과 상태는 <see cref="MainViewModel" /> 에 있다.
/// </summary>
public partial class MainView : ContentPage
{
    public MainView()
    {
        InitializeComponent();

        Loaded += OnPageLoaded;
        // WPF 는 PreviewMouseRightButtonDown 에서 그 행을 고른 뒤 메뉴를 연다 — 같은 순서로 맞춘다
        GuestGrid.AddHandler(InputElement.PointerPressedEvent, OnGuestGridPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        GuestGrid.DoubleTapped += OnGuestGridDoubleTapped;
        NodeList.DoubleTapped += OnNodeListDoubleTapped;
        StorageGrid.DoubleTapped += OnStorageDoubleTapped;
        TaskGrid.DoubleTapped += OnTaskDoubleTapped;
        // 콘솔 ▾ 메뉴를 열 때마다 VM 설정을 읽어 RDP·SPICE 항목을 정한다(WPF OnConsoleMenu 와 같다)
        if (ConsoleSplit.Flyout is { } consoleFlyout)
            consoleFlyout.Opened += (_, _) => _ = Vm?.RefreshConsoleKindsAsync();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>화면이 붙을 때 저장된 영역 크기(상세 높이 · 노드 목록 너비)를 적용한다.</summary>
    private async void OnPageLoaded(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var settings = await vm.LoadSettingsAsync();
        GuestPanel.RowDefinitions[0].Height = new GridLength(settings.GuestDetailHeight);
        NodePanel.ColumnDefinitions[0].Width = new GridLength(settings.NodeListWidth);
    }

    /// <summary>영역 크기 손잡이를 놓았다 — 다음 실행에도 같은 크기로 열리게 저장한다.</summary>
    private async void OnPaneSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (Vm is { } vm) await vm.SaveLayoutAsync(GuestPanel.RowDefinitions[0].ActualHeight, NodePanel.ColumnDefinitions[0].ActualWidth);
    }

    private void OnGuestGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || !e.GetCurrentPoint(GuestGrid).Properties.IsRightButtonPressed) return;

        if (RowItem(e.Source) is PveResource guest) vm.SelectedGuest = guest;
    }

    /// <summary>게스트 행 더블클릭 → 콘솔 열기(헤더·빈 영역 더블클릭은 무시 — WPF 와 같다).</summary>
    private void OnGuestGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm || RowItem(e.Source) is not PveResource guest) return;

        vm.SelectedGuest = guest;
        vm.OpenConsoleCommand.Execute(null);
        e.Handled = true;
    }

    private void OnNodeListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm || ListItem(e.Source) is not PveNode node) return;

        vm.SelectedNode = node;
        _ = vm.OpenNodeAsync(node);
    }

    private void OnStorageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { } vm && RowItem(e.Source) is PveStorage storage) vm.OpenStorageCommand.Execute(storage);
    }

    private void OnTaskDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { } vm && RowItem(e.Source) is PveTask task) vm.OpenTaskCommand.Execute(task);
    }

    /// <summary>"만들기" — 등록된 클러스터 기능(VM·CT 마법사 등)을 메뉴로 보인다. 없으면 버튼이 잠긴다.</summary>
    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Button button) return;

        var menu = new MenuFlyout();
        foreach (var feature in vm.CreateEntries)
        {
            var item = new MenuItem { Header = L.T(feature.LabelKey) };
            if (Application.Current is { } app
                && app.TryGetResource(feature.IconKey, app.ActualThemeVariant, out var icon) && icon is Geometry geometry)
                item.Icon = new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Data = geometry };
            var captured = feature;
            item.Click += (_, _) => _ = vm.RunCreateAsync(captured);
            menu.Items.Add(item);
        }

        FlyoutBase.SetAttachedFlyout(button, menu);
        FlyoutBase.ShowAttachedFlyout(button);
    }

    /// <summary>목록 줄의 항목 — 헤더·빈 영역이면 null.</summary>
    private static object? RowItem(object? source)
    {
        return (source as Visual)?.FindAncestorOfType<DataGridRow>()?.DataContext;
    }

    private static object? ListItem(object? source)
    {
        return (source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext;
    }
}
