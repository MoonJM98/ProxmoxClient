using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.SpiceSetup;

/// <summary>
///     SPICE 클라이언트 설치 화면(WPF VirtViewerSetupWindow 와 같은 내용) — 설치 상태, OS 별 설치 안내,
///     Windows 는 자동 설치, 받을 곳 열기, 다시 검색. 설치가 확인되면 화면을 닫고 콘솔 연결을 이어 간다.
/// </summary>
public sealed class SpiceSetupPage : UserControl
{
    private readonly ISpiceLauncher _launcher;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 14, IsVisible = false };
    private readonly TextBlock _progressText = new()
    {
        Classes = { "subtle" }, FontSize = 11, IsVisible = false, TextWrapping = TextWrapping.Wrap
    };
    private readonly Button? _install;
    private readonly Button _rescan;
    private readonly Button? _openPage;
    private readonly Button _close;
    private bool _busy;

    public SpiceSetupPage() : this(SpiceLaunchers.Current)
    {
    }

    public SpiceSetupPage(ISpiceLauncher launcher)
    {
        _launcher = launcher;
        var guide = launcher.Guide;

        var body = new StackPanel { Spacing = 12, MaxWidth = 720, Margin = new Thickness(16, 14, 16, 14) };
        body.Children.Add(new TextBlock
        {
            Text = Loc.T(guide.TitleKey), FontSize = 15, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        // 설치 상태 칸(WPF FormPanel)
        var statusBox = new StackPanel { Spacing = 4 };
        statusBox.Children.Add(new TextBlock
        {
            Text = Loc.T("VirtViewerSetupWindow_03"), Foreground = BrushOf("BrushDim"), Margin = new Thickness(0, 0, 0, 4)
        });
        statusBox.Children.Add(_status);
        body.Children.Add(new Border { Classes = { "FormPanel" }, Child = statusBox });

        foreach (var stepKey in guide.StepKeys)
            body.Children.Add(new TextBlock
            {
                Text = Loc.T(stepKey), Foreground = BrushOf("BrushDim"), TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            });

        body.Children.Add(_progress);
        body.Children.Add(_progressText);

        // 단추 줄 — WPF 순서: 자동 설치 · 다시 검색 · 받을 곳 · 닫기
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        if (launcher.Installer is not null)
        {
            _install = ActionBar.IconButton(Loc.T("VirtViewerSetupWindow_06"), "IconDownload", this);
            _install.Classes.Add("AccentButton");
            _install.Click += async (_, _) => await InstallAsync(launcher.Installer);
            buttons.Children.Add(_install);
        }

        _rescan = ActionBar.IconButton(Loc.T("VirtViewerSetupWindow_07"), "IconSearch", this);
        _rescan.Click += (_, _) => Rescan();
        buttons.Children.Add(_rescan);

        if (guide.PageUrl is { } url)
        {
            _openPage = ActionBar.IconButton(Loc.T(guide.PageButtonKey), "IconExternal", this);
            _openPage.Click += async (_, _) => await OpenPageAsync(url);
            buttons.Children.Add(_openPage);
        }

        _close = ActionBar.IconButton(Loc.T("FirewallWindow_18"), "IconX", this);
        _close.Click += (_, _) => Close();
        buttons.Children.Add(_close);
        body.Children.Add(buttons);

        Content = new ScrollViewer { Content = body };
        Unloaded += (_, _) => _closed.TrySetResult(); // 뒤로 가기로 떠나도 기다리는 흐름이 풀린다
        Refresh();
    }

    /// <summary>화면이 떠날 때 끝나는 작업 — 콘솔 흐름이 설치 뒤 이어 가는 데 쓴다.</summary>
    public Task Closed => _closed.Task;

    private void Refresh()
    {
        var status = _launcher.Probe();
        _status.Text = status.Available
            ? Loc.T("VirtViewerSetupWindow_M02", status.Location ?? string.Empty)
            : Loc.T("Spice_StatusMissing");
        if (_install is not null) _install.IsEnabled = !status.Available && !_busy;
    }

    private void Rescan()
    {
        Refresh();
        if (_launcher.Probe().Available) _progressText.Text = Loc.T("VirtViewerSetupWindow_M10");
    }

    private async Task InstallAsync(ISpiceViewerInstaller installer)
    {
        if (_busy) return;

        _busy = true;
        _install!.IsEnabled = false;
        if (_openPage is not null) _openPage.IsEnabled = false;
        _progress.IsVisible = true;
        _progressText.IsVisible = true;
        _progress.IsIndeterminate = true;
        _progressText.Text = Loc.T("VirtViewer_FetchingList");

        try
        {
            var exit = await installer.InstallAsync(
                state => Dispatcher.UIThread.Post(() => _progressText.Text = state),
                (read, total) => Dispatcher.UIThread.Post(() => ShowDownload(read, total)),
                CancellationToken.None);

            _progress.IsVisible = false;
            _progressText.IsVisible = false;
            Refresh();
            if (_launcher.Probe().Available)
            {
                Close(); // WPF 와 같이 설치를 확인하면 바로 닫고 콘솔 연결을 이어 간다
                return;
            }

            _status.Text = exit == 0 ? Loc.T("VirtViewerSetupWindow_M07") : Loc.T("VirtViewerSetupWindow_M08", exit);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or OperationCanceledException)
        {
            _progress.IsVisible = false;
            _progressText.IsVisible = true;
            _progressText.Text = Loc.T("VirtViewerSetupWindow_M09", ex.Message);
        }
        finally
        {
            _busy = false;
            _rescan.IsEnabled = true;
            if (_openPage is not null) _openPage.IsEnabled = true;
            Refresh();
        }
    }

    private void ShowDownload(long read, long total)
    {
        var mb = read / 1024.0 / 1024.0;
        if (total > 0)
        {
            _progress.IsIndeterminate = false;
            _progress.Value = (double)read / total * 100;
            _progressText.Text = Loc.T("VirtViewer_Downloading", $"{mb:F1} / {total / 1024.0 / 1024.0:F1} MB");
        }
        else
        {
            _progressText.Text = Loc.T("VirtViewer_Downloading", $"{mb:F1} MB");
        }
    }

    private async Task OpenPageAsync(string url)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(url));
    }

    /// <summary>화면을 닫는다 — 스택에서 빼면 <see cref="Closed" /> 가 끝난다.</summary>
    private void Close()
    {
        _closed.TrySetResult();
        _ = AppServices.Navigator.PopAsync();
    }

    /// <summary>테마 브러시(WpfTheme 의 BrushDim 등) — 없으면 null(기본 글자색).</summary>
    private static IBrush? BrushOf(string key)
    {
        return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            ? value as IBrush
            : null;
    }
}
