using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Node.Tabs;

/// <summary>노드 창의 요약 탭 — 커널·부하·자원 사용량을 주기적으로 새로 읽는다.</summary>
public sealed class NodeSummaryTab : UserControl
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly ProxmoxApiClient _api;
    private readonly string _node;
    private readonly DispatcherTimer _timer = new() { Interval = RefreshInterval };
    private readonly TextBlock _kernel = ValueText();
    private readonly TextBlock _cores = ValueText();
    private readonly TextBlock _load = ValueText();
    private readonly TextBlock _uptime = ValueText();
    private readonly ProgressBar _cpuBar = UsageBar();
    private readonly ProgressBar _memBar = UsageBar();
    private readonly ProgressBar _swapBar = UsageBar();
    private readonly ProgressBar _rootBar = UsageBar();
    private readonly TextBlock _cpuText = SubtleText();
    private readonly TextBlock _memText = SubtleText();
    private readonly TextBlock _swapText = SubtleText();
    private readonly TextBlock _rootText = SubtleText();
    private readonly TextBlock _status = new() { Classes = { "DialogStatus" }, Margin = new Thickness(0, 12, 0, 0) };

    /// <param name="actions">위쪽 작업 버튼(전원·일괄 작업 — 권한에 맞게 호출자가 고른다).</param>
    public NodeSummaryTab(ProxmoxApiClient api, string node, IReadOnlyList<TableAction> actions)
    {
        _api = api;
        _node = node;

        var actionPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        ActionBar.Populate(actionPanel, actions, this, _status);

        _timer.Tick += async (_, _) => await RefreshAsync();
        AttachedToVisualTree += async (_, _) =>
        {
            _timer.Start();
            await RefreshAsync();
        };
        DetachedFromVisualTree += (_, _) => _timer.Stop();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = PlatformInfo.IsDesktop ? new Thickness(20, 16) : new Thickness(16),
                Children =
                {
                    actionPanel,
                    FormCard(Loc.T("MainWindow_NodeDetail"), InfoGrid()),
                    UsageCard(),
                    _status
                }
            }
        };
    }

    private static TextBlock ValueText()
    {
        return new TextBlock { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
    }

    private static TextBlock SubtleText()
    {
        return new TextBlock { Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
    }

    private static ProgressBar UsageBar()
    {
        return new ProgressBar { Classes = { "usage" }, Minimum = 0, Maximum = 100, Height = 16 };
    }

    private static Border FormCard(string title, Control body)
    {
        return new Border
        {
            Classes = { "FormPanel" }, Margin = new Thickness(0, 0, 0, 12),
            Child = new StackPanel { Children = { new TextBlock { Text = title, Classes = { "FormSection" } }, body } }
        };
    }

    private Grid InfoGrid()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto")
        };
        AddRow(grid, 0, "MainWindow_46", _kernel);
        AddRow(grid, 1, "MainWindow_47", _cores);
        AddRow(grid, 2, "MainWindow_48", _load);
        AddRow(grid, 3, "MainWindow_21", _uptime);
        return grid;
    }

    private static void AddRow(Grid grid, int row, string labelKey, Control value)
    {
        var label = new TextBlock { Text = Loc.T(labelKey), Classes = { "SummaryLabel" } };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
    }

    private Border UsageCard()
    {
        return FormCard(Loc.T("MainWindow_56"), new StackPanel
        {
            Children =
            {
                Usage("CPU", _cpuBar, _cpuText, bottom: 10),
                Usage(Loc.T("MainWindow_19"), _memBar, _memText, bottom: 10),
                Usage(Loc.T("MainWindow_50"), _swapBar, _swapText, bottom: 10),
                Usage(Loc.T("MainWindow_51"), _rootBar, _rootText, bottom: 0)
            }
        });
    }

    /// <summary>라벨·막대·수치 한 묶음 — WPF 와 같은 순서(라벨 → 막대 → 글자 줄).</summary>
    private static StackPanel Usage(string title, ProgressBar bar, TextBlock text, double bottom)
    {
        return new StackPanel
        {
            Margin = new Thickness(0, 0, 0, bottom),
            Children =
            {
                new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 3) },
                bar,
                new Border { Margin = new Thickness(0, 3, 0, 0), Child = text }
            }
        };
    }

    private async Task RefreshAsync()
    {
        try
        {
            var s = await _api.GetNodeStatusAsync(_node);

            _kernel.Text = s.KernelVersion;
            _cores.Text = s.CpuCores.ToString(CultureInfo.CurrentCulture);
            _load.Text = string.Format(CultureInfo.CurrentCulture, "{0:F2} · {1:F2} · {2:F2}",
                s.LoadAverage1, s.LoadAverage5, s.LoadAverage15);
            _uptime.Text = s.UptimeSeconds > 0 ? TableFormats.FormatUptime(s.UptimeSeconds) : "-";

            _cpuBar.Value = Math.Clamp(s.CpuUsagePercent, 0, 100);
            _cpuText.Text = Loc.T("SummaryTab_CpuUsage", s.CpuUsagePercent, s.CpuCores);

            SetUsage(_memBar, _memText, s.MemUsedBytes, s.MemTotalBytes);
            SetUsage(_swapBar, _swapText, s.SwapUsedBytes, s.SwapTotalBytes);
            SetUsage(_rootBar, _rootText, s.RootFsUsedBytes, s.RootFsTotalBytes);

            _status.Text = string.Empty;
        }
        catch (Exception ex)
        {
            _status.Text = Loc.T("MainViewModel_M07", ex.Message);
        }
    }

    private static void SetUsage(ProgressBar bar, TextBlock text, long used, long total)
    {
        bar.Value = total > 0 ? Math.Clamp(used * 100.0 / total, 0, 100) : 0;
        text.Text = total > 0
            ? Loc.T("SummaryTab_Usage", Format.Bytes(used), Format.Bytes(total), bar.Value)
            : Format.Bytes(used);
    }
}
