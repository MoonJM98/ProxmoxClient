using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Files;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Storage;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>
///     주소 표시줄 — 뿌리부터 지금 폴더까지 경로 조각(누르면 그 폴더로), 조각마다 ›(하위 폴더 목록).
///     마지막 폴더 뒤의 › 는 하위 폴더가 있을 때만 보인다(탐색기처럼). 빈 곳을 누르거나 Ctrl+L·Alt+D 로 경로를 직접 적는다.
/// </summary>
public partial class GuestFilesPanel
{
    private const double CrumbLabelMaxWidth = 220;

    private string? _addressPath;
    private IReadOnlyList<string> _crumbPaths = [];
    private Button? _trailingArrow;

    /// <summary>주소 표시줄을 고친다 — 폴더가 바뀌면 경로 조각을 다시 만든다.</summary>
    private void UpdateAddress(string directory, bool hasSubfolders)
    {
        if (_addressPath != directory) BuildCrumbs(directory);
        if (_trailingArrow is { } arrow) arrow.IsVisible = hasSubfolders;
    }

    private void BuildCrumbs(string directory)
    {
        _addressPath = directory;
        _pathBox.Text = directory;
        _crumbs.Children.Clear();
        _trailingArrow = null;
        if (_files is not { } files) return;

        _crumbPaths = Hierarchy(files, directory);
        for (var i = 0; i < _crumbPaths.Count; i++)
        {
            var part = _crumbPaths[i];
            var label = new TextBlock
            {
                Text = CrumbLabel(files, part, i == 0), MaxWidth = CrumbLabelMaxWidth,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var button = CrumbButton(label, part);
            button.Click += async (_, _) => { EndAddressEdit(); await NavigateAsync(part); };
            _crumbs.Children.Add(button);

            var chevron = ChevronIcon("IconChevronRight");
            var arrow = CrumbButton(chevron, Loc.T("GuestFiles_Subfolders"));
            arrow.Padding = new Thickness(4, 0, 4, 0);
            arrow.Click += async (_, _) => await ShowFolderMenuAsync(arrow, chevron, part);
            _crumbs.Children.Add(arrow);
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

    private static Button CrumbButton(Control content, string tooltip)
    {
        var button = new Button
        {
            Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 0), Height = 23, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            CornerRadius = new CornerRadius(3)
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Avalonia.Controls.Shapes.Path ChevronIcon(string key) => new()
    {
        Data = Icon(key), Width = 10, Height = 10, Classes = { "icon" }, Stretch = Stretch.Uniform,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    /// <summary>하위 폴더 목록 — 눌렀을 때 읽어 펼친다(읽는 동안 "불러오는 중").</summary>
    private async Task ShowFolderMenuAsync(Button anchor, Avalonia.Controls.Shapes.Path chevron, string directory)
    {
        if (_files is not { } files || _busy is not null || _closed) return;

        var menu = new MenuFlyout();
        // 펼친 동안은 ∨ 로(탐색기처럼)
        chevron.Data = Icon("IconChevronDown");
        menu.Items.Add(FolderMenuItem(Loc.T("GuestFiles_Loading"), false));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_loadCancel.Token);
        void OnClosed(object? _, EventArgs __)
        {
            chevron.Data = Icon("IconChevronRight");
            cancel.Cancel();
        }

        menu.Closed += OnClosed;
        menu.ShowAt(anchor);
        try
        {
            using var flow = WriteConfirmation.Begin(FlowName, _readsApproved);
            var entries = await files.ListAsync(directory, cancel.Token);
            if (!menu.IsOpen || _closed) return;

            menu.Items.Clear();
            var folders = entries.Where(e => e.OpensAsFolder && (_showHidden || !e.IsHidden)).Order(DefaultFolderOrder);
            foreach (var entry in folders)
            {
                var target = files.Combine(directory, entry.Name); // 폴더 링크도 링크 경로 그대로
                var item = FolderMenuItem(entry.Name, true);
                item.Click += async (_, _) => { EndAddressEdit(); await NavigateAsync(target); };
                menu.Items.Add(item);
            }

            if (menu.Items.Count == 0) menu.Items.Add(FolderMenuItem(Loc.T("GuestFiles_NoSubfolders"), false));
        }
        catch (OperationCanceledException)
        {
            menu.Hide();
        }
        catch (Exception ex) when (IsExpected(ex) || ex is WriteCancelledException)
        {
            if (!menu.IsOpen) return;

            menu.Items.Clear();
            menu.Items.Add(FolderMenuItem(Loc.T("GuestFiles_Error", ex.Message), false));
        }
        finally
        {
            menu.Closed -= OnClosed;
        }
    }

    private static readonly IComparer<GuestFileEntry> DefaultFolderOrder = Comparer<GuestFileEntry>.Create((a, b) =>
        StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));

    private static MenuItem FolderMenuItem(string label, bool enabled) => new()
    {
        Header = new TextBlock { Text = label, MaxWidth = 280, TextTrimming = enabled ? TextTrimming.CharacterEllipsis : TextTrimming.None, TextWrapping = enabled ? TextWrapping.NoWrap : TextWrapping.Wrap },
        IsEnabled = enabled
    };

    private void BeginAddressEdit()
    {
        _pathBox.Text = _current;
        _crumbScroll.IsVisible = false;
        _pathBox.IsVisible = true;
        _pathBox.Focus();
        _pathBox.SelectAll();
    }

    private void EndAddressEdit()
    {
        _pathBox.IsVisible = false;
        _crumbScroll.IsVisible = true;
    }

    private void OnAddressPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || _pathBox.IsVisible) return;

        // 버튼·입력 칸을 누른 것이면 그대로 둔다(경로·화살표 버튼은 제 동작을 한다). 빈 자리면 편집한다.
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().Any(a => a is Button or TextBox)) return;

        BeginAddressEdit();
        e.Handled = true;
    }

    private async void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndAddressEdit();
            return;
        }

        if (e.Key != Key.Enter || _files is null) return;

        e.Handled = true;
        var path = _pathBox.Text?.Trim() ?? string.Empty;
        EndAddressEdit();
        await NavigateAsync(path.Length == 0 ? _files.HomePath : path);
    }
}
