using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views.Shared;

/// <summary>설정 파일 같은 긴 텍스트를 읽고(저장 함수가 있으면) 고친다 — WPF TextEditTab 과 같은 생성자.</summary>
public sealed class TextEditTab : UserControl
{
    private readonly Func<Task<(string Text, string Digest)>> _load;
    private readonly Func<string, string, Task>? _save;
    private readonly TextBox _editor;
    private readonly TextBlock _status = new() { Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap };
    private string _digest = string.Empty;
    private bool _busy;

    public TextEditTab(Func<Task<(string Text, string Digest)>> load, Func<string, string, Task>? save,
        string hintKey)
    {
        _load = load;
        _save = save;
        _editor = new TextBox
        {
            AcceptsReturn = true, IsReadOnly = save is null, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), FontSize = 13
        };
        var reload = ActionBar.IconButton(L.T("Common_Refresh"), "IconRefresh", this);
        reload.Click += async (_, _) => await ReloadAsync();
        var bar = new WrapPanel { Children = { reload } };
        if (save is not null)
        {
            var saveButton = ActionBar.IconButton(L.T("Common_Save"), "IconSave", this);
            saveButton.Click += async (_, _) => await SaveAsync();
            bar.Children.Insert(0, saveButton);
        }

        var top = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                bar,
                new TextBlock
                {
                    Text = L.T(hintKey), Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.None, IsVisible = L.Has(hintKey)
                },
                _status
            }
        };
        DockPanel.SetDock(top, Dock.Top);
        Content = new DockPanel { Margin = new Thickness(12), Children = { top, _editor } };
        AttachedToVisualTree += async (_, _) =>
        {
            if (string.IsNullOrEmpty(_editor.Text)) await ReloadAsync();
        };
    }

    private async Task ReloadAsync()
    {
        if (_busy) return;

        _busy = true;
        try
        {
            var (text, digest) = await _load();
            _editor.Text = text.Replace("\r\n", "\n");
            _digest = digest;
            _status.Text = string.Empty;
        }
        catch (Exception ex)
        {
            _status.Text = L.T("MainViewModel_M07", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SaveAsync()
    {
        if (_busy || _save is null) return;

        _busy = true;
        try
        {
            using (WriteConfirmation.Begin(L.T("Common_Save")))
                await _save((_editor.Text ?? string.Empty).Replace("\r\n", "\n"), _digest);
        }
        catch (WriteCancelledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _status.Text = L.T("TableTab_ActionFailed", ex.Message);
            return;
        }
        finally
        {
            _busy = false;
        }

        await ReloadAsync();
        _status.Text = L.T("TextEditTab_Saved");
    }
}

/// <summary>표 하나를 새 화면으로 — WPF TableWindow.ShowModal 대신(모달 창 없이 화면 스택에 쌓는다).</summary>
public static class TableWindow
{
    public static string? ShowModal(Control? owner, string title, Control content)
    {
        _ = AppServices.Navigator.PushViewAsync(title, content);
        return null;
    }
}

/// <summary>긴 텍스트를 새 화면으로 — WPF TextViewWindow.ShowModal 대신.</summary>
public static class TextViewWindow
{
    public static string? ShowModal(Control? owner, string title, string text)
    {
        var view = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new SelectableTextBlock
            {
                Text = text, Margin = new Thickness(16), FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
                FontSize = 13
            }
        };
        _ = AppServices.Navigator.PushViewAsync(title, view);
        return null;
    }
}

/// <summary>작업 로그 화면 — WPF TaskLogViewer.Show 대신.</summary>
public static class TaskLogViewer
{
    public static void Show(ProxmoxApiClient api, PveTask task, Control? owner)
    {
        _ = AppServices.Navigator.PushAsync(new TaskLogViewModel(api, task));
    }
}
