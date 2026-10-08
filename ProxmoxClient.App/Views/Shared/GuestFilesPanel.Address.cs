using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Files;

namespace ProxmoxClient.App.Views.Shared;

public partial class GuestFilesPanel
{
    private const double CrumbLabelMaxWidth = 220;
    private const double ChevronSize = 10;

    private string? _addressPath;
    private IReadOnlyList<string> _crumbPaths = [];
    private Button? _trailingArrow;

    /// <summary>
    ///     주소 표시줄을 고친다 — 폴더가 바뀌면 경로 조각을 다시 만들고,
    ///     마지막 폴더 뒤의 &gt; 는 하위 폴더가 있을 때만 보인다(탐색기처럼).
    /// </summary>
    private void UpdateAddress(string directory, bool hasSubfolders)
    {
        if (_addressPath != directory) BuildCrumbs(directory);
        if (_trailingArrow is { } arrow) arrow.Visibility = hasSubfolders ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildCrumbs(string directory)
    {
        _addressPath = directory;
        PathBox.Text = directory;
        Breadcrumbs.Children.Clear();
        _trailingArrow = null;
        if (_files is not { } files) return;

        _crumbPaths = Hierarchy(files, directory);
        // 자리가 모자라면 BreadcrumbPanel 이 앞 폴더를 접고 이 … 를 보인다
        var overflow = CrumbButton(Icon("IconMore", 14), Loc.T("GuestFiles_MoreFolders"));
        overflow.Click += (_, _) => ShowCollapsedMenu(overflow);
        Breadcrumbs.Children.Add(overflow);
        for (var i = 0; i < _crumbPaths.Count; i++)
        {
            var part = _crumbPaths[i];
            var label = new TextBlock
            {
                Text = CrumbLabel(files, part, i == 0),
                MaxWidth = CrumbLabelMaxWidth, TextTrimming = TextTrimming.CharacterEllipsis
            };
            var button = CrumbButton(label, part);
            button.Click += async (_, _) => { EndAddressEdit(); await NavigateAsync(part); };
            Breadcrumbs.Children.Add(button);

            var chevron = Icon("IconChevronRight", ChevronSize);
            var arrow = CrumbButton(chevron, Loc.T("GuestFiles_Subfolders"));
            arrow.Padding = new Thickness(4, 0, 4, 0);
            arrow.Click += async (_, _) => await ShowFolderMenuAsync(arrow, chevron, part);
            Breadcrumbs.Children.Add(arrow);
            _trailingArrow = arrow;
        }
    }

    /// <summary>뿌리부터 지금 폴더까지 — 파일 시스템의 부모 규칙으로 Windows 드라이브·UNC 와 POSIX 를 함께.</summary>
    private static List<string> Hierarchy(IGuestFileSystem files, string directory)
    {
        var hierarchy = new List<string>();
        var path = directory;
        var seen = new HashSet<string>(files.NameComparer);
        while (seen.Add(path))
        {
            hierarchy.Add(path);
            var parent = files.Parent(path);
            if (files.NameComparer.Equals(parent, path)) break;
            path = parent;
        }

        hierarchy.Reverse();
        return hierarchy;
    }

    private static string CrumbLabel(IGuestFileSystem files, string part, bool isRoot)
    {
        if (isRoot) return part;
        var label = part.TrimEnd(files.Separator);
        var cut = label.LastIndexOf(files.Separator);
        return cut >= 0 ? label[(cut + 1)..] : label;
    }

    private Button CrumbButton(object content, string tooltip) =>
        new() { Content = content, ToolTip = tooltip, Style = (Style)FindResource("BreadcrumbButton") };

    private PathIcon Icon(string key, double size) =>
        new() { Data = (Geometry)FindResource(key), Width = size, Height = size };

    /// <summary>… — 접힌 상위 폴더를 가까운 것부터 보여 준다.</summary>
    private void ShowCollapsedMenu(Button anchor)
    {
        var count = Math.Min(Breadcrumbs.CollapsedPairs, _crumbPaths.Count);
        if (count == 0) return;

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            Style = (Style)FindResource("BreadcrumbMenu") };
        for (var i = count - 1; i >= 0; i--)
        {
            var target = _crumbPaths[i];
            var item = FolderMenuItem(_files is { } files ? CrumbLabel(files, target, i == 0) : target, true, target);
            item.Click += async (_, _) => { EndAddressEdit(); await NavigateAsync(target); };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private async Task ShowFolderMenuAsync(Button anchor, PathIcon chevron, string directory)
    {
        if (_files is not { } files || _busy is not null || _closed) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            Style = (Style)FindResource("BreadcrumbMenu") };
        // 펼친 동안은 ∨ 로(탐색기처럼)
        chevron.Data = (Geometry)FindResource("IconChevronDown");
        menu.Closed += (_, _) => chevron.Data = (Geometry)FindResource("IconChevronRight");
        menu.Items.Add(FolderMenuItem(Loc.T("GuestFiles_Loading"), false));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_loadCancel.Token);
        RoutedEventHandler onClosed = (_, _) => cancel.Cancel();
        menu.Closed += onClosed;
        menu.IsOpen = true;
        try
        {
            var entries = await files.ListAsync(directory, cancel.Token);
            if (!menu.IsOpen || _closed) return;
            menu.Items.Clear();
            var folders = entries.Where(e => e.OpensAsFolder && (_showHidden || !e.IsHidden)).Order(DefaultOrder);
            foreach (var entry in folders)
            {
                var target = files.Combine(directory, entry.Name); // 폴더 링크도 링크 경로 그대로
                var item = FolderMenuItem(entry.Name, true, target);
                item.Click += async (_, _) => { EndAddressEdit(); await NavigateAsync(target); };
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(FolderMenuItem(Loc.T("GuestFiles_NoSubfolders"), false));
        }
        catch (OperationCanceledException) { menu.IsOpen = false; }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (!menu.IsOpen) return;
            menu.Items.Clear();
            var error = Loc.T("GuestFiles_Error", ex.Message);
            menu.Items.Add(FolderMenuItem(error, false, error));
        }
        finally { menu.Closed -= onClosed; }
    }

    private static MenuItem FolderMenuItem(string label, bool enabled, string? tooltip = null) => new()
    {
        Header = new TextBlock { Text = label, MaxWidth = 280,
            TextTrimming = enabled ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            TextWrapping = enabled ? TextWrapping.NoWrap : TextWrapping.Wrap },
        IsEnabled = enabled,
        ToolTip = tooltip
    };

    private void BeginAddressEdit()
    {
        PathBox.Text = _current;
        Breadcrumbs.Visibility = Visibility.Collapsed;
        PathBox.Visibility = Visibility.Visible;
        PathBox.Focus();
        PathBox.SelectAll();
    }
    private void EndAddressEdit()
    {
        PathBox.Visibility = Visibility.Collapsed;
        Breadcrumbs.Visibility = Visibility.Visible;
    }
    private void OnAddressClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || PathBox.Visibility == Visibility.Visible) return;
        // 버튼이 아닌 곳(오른쪽·아래 빈 자리)을 누르면 편집한다. 경로·화살표 버튼은 그대로 동작한다.
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && !ReferenceEquals(source, sender))
        {
            if (source is ButtonBase or TextBoxBase) return;
            source = source is ContentElement content
                ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(source);
        }
        BeginAddressEdit();
        e.Handled = true;
    }
    private void OnPathLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndAddressEdit();
    private void OnAddressShortcut(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if ((key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) ||
            (key == Key.D && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            e.Handled = true;
            BeginAddressEdit();
        }
    }
}
