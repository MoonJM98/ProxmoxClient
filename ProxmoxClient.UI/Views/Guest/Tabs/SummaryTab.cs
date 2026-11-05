using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;
using ProxmoxClient.UI.Views.Controls;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Guest.Tabs;

/// <summary>
///     게스트 요약 탭 — 상태·자원 사용량·RRD 그래프. 화면이 보이는 동안만 주기적으로 새로 읽는다
///     (상태·사용량은 /cluster/resources 한 번, 그래프는 rrddata).
/// </summary>
public sealed class SummaryTab : UserControl
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GraphInterval = TimeSpan.FromSeconds(30);

    private static readonly (string Id, string LabelKey)[] Timeframes =
    [
        ("hour", "MainWindow_24"), ("day", "MainWindow_25"), ("week", "MainWindow_26"),
        ("month", "MainWindow_27"), ("year", "MainWindow_28")
    ];

    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = RefreshInterval };
    private readonly DispatcherTimer _graphTimer = new() { Interval = GraphInterval };
    private readonly string _timeframeGroup = "guest-summary-timeframe-" + Guid.NewGuid().ToString("N");
    private readonly Ellipse _dot = new() { Classes = { "dot" } };
    private readonly TextBlock _statusName = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _node = ValueText();
    private readonly TextBlock _cores = ValueText();
    private readonly TextBlock _uptime = ValueText();
    private readonly TextBlock _pool = ValueText();
    private readonly TextBlock _tags = ValueText();
    private readonly ProgressBar _cpuBar = UsageBar();
    private readonly ProgressBar _memBar = UsageBar();
    private readonly ProgressBar _diskBar = UsageBar();
    private readonly TextBlock _cpuText = SubtleText();
    private readonly TextBlock _memText = SubtleText();
    private readonly TextBlock _diskText = SubtleText();
    private readonly GraphLine _graph = new() { MinHeight = 200 };
    private readonly TextBlock _graphStatus = SubtleText();
    private readonly TextBlock _status = new() { Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap };
    private string _timeframe = "hour";
    private int _graphVersion;
    private CancellationTokenSource? _lifetime;

    /// <param name="actions">상단 작업 버튼(이전·템플릿 변환·삭제 등 — 권한에 맞게 호출자가 고른다).</param>
    public SummaryTab(ProxmoxApiClient api, PveResource guest, IReadOnlyList<TableAction> actions)
    {
        _api = api;
        _guest = guest;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _graphTimer.Tick += async (_, _) => await LoadGraphAsync();

        var stack = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        if (actions.Count > 0)
        {
            var actionPanel = new WrapPanel();
            ActionBar.Populate(actionPanel, actions, this, _status);
            stack.Children.Add(actionPanel);
        }

        stack.Children.Add(IdentityCard());
        stack.Children.Add(UsageCard());
        stack.Children.Add(GraphCard());
        stack.Children.Add(_status);
        Content = new ScrollViewer { Content = stack };

        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => Stop();
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
        return new ProgressBar { Classes = { "usage" }, Minimum = 0, Maximum = 100 };
    }

    private static TextBlock Section(string labelKey)
    {
        return new TextBlock { Text = Loc.T(labelKey), Classes = { "section" } };
    }

    /// <summary>라벨 한 줄 — 왼쪽 라벨, 오른쪽 값(값이 길면 줄바꿈).</summary>
    private static void AddInfoRow(Grid grid, int row, string labelKey, Control value)
    {
        var label = new TextBlock { Text = Loc.T(labelKey), Classes = { "subtle" }, Margin = new Thickness(0, 0, 12, 6) };
        value.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
    }

    private Border IdentityCard()
    {
        var info = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto")
        };
        AddInfoRow(info, 0, "MainWindow_41", _node);
        AddInfoRow(info, 1, "MainWindow_47", _cores);
        AddInfoRow(info, 2, "MainWindow_21", _uptime);
        AddInfoRow(info, 3, "MainWindow_22", _pool);
        AddInfoRow(info, 4, "MainWindow_23", _tags);

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 6),
            Children = { _dot, _statusName }
        };
        return new Border
        {
            Classes = { "card" },
            Child = new StackPanel { Spacing = 8, Children = { Section("MainWindow_42"), status, info } }
        };
    }

    private Border UsageCard()
    {
        return new Border
        {
            Classes = { "card" },
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Section("MainWindow_56"),
                    new StackPanel { Spacing = 3, Children = { new TextBlock { Text = "CPU" }, _cpuBar, _cpuText } },
                    new StackPanel { Spacing = 3, Children = { new TextBlock { Text = Loc.T("MainWindow_19") }, _memBar, _memText } },
                    new StackPanel { Spacing = 3, Children = { new TextBlock { Text = Loc.T("MainWindow_20") }, _diskBar, _diskText } }
                }
            }
        };
    }

    private Border GraphCard()
    {
        var chips = new WrapPanel();
        foreach (var (id, labelKey) in Timeframes)
        {
            var chip = new RadioButton
            {
                Content = Loc.T(labelKey), GroupName = _timeframeGroup, Classes = { "chip" }, IsChecked = id == _timeframe
            };
            chip.IsCheckedChanged += (_, _) =>
            {
                if (chip.IsChecked != true || _timeframe == id) return;

                _timeframe = id;
                _ = LoadGraphAsync();
            };
            chips.Children.Add(chip);
        }

        return new Border
        {
            Classes = { "card" },
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { Section("MainWindow_29"), chips, _graph, _graphStatus }
            }
        };
    }

    private void Start()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = new CancellationTokenSource();
        Apply();
        _refreshTimer.Start();
        _graphTimer.Start();
        _ = RefreshAsync();
        _ = LoadGraphAsync();
    }

    /// <summary>화면을 벗어나면 주기 조회를 멈추고 진행 중인 요청을 취소한다.</summary>
    private void Stop()
    {
        _refreshTimer.Stop();
        _graphTimer.Stop();
        _lifetime?.Cancel();
    }

    private CancellationToken Token => _lifetime?.Token ?? CancellationToken.None;

    private async Task RefreshAsync()
    {
        try
        {
            var resources = await _api.GetClusterResourcesAsync(Token);
            if (resources.FirstOrDefault(r => r.Id == _guest.Id) is not { } fresh) return;

            _guest.CopyFrom(fresh);
            Apply();
            _status.Text = string.Empty;
        }
        catch (Exception ex) when (!Token.IsCancellationRequested)
        {
            _status.Text = Loc.T("MainViewModel_M07", ex.Message);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>선택한 기간의 rrddata 를 읽어 그래프를 다시 그린다. 더 새 요청이 있으면 결과를 버린다.</summary>
    private async Task LoadGraphAsync()
    {
        var version = ++_graphVersion;
        var timeframe = _timeframe;
        try
        {
            var samples = await _api.GetGuestRrdDataAsync(_guest.Node, _guest.Kind, _guest.VmId, timeframe, Token);
            if (version != _graphVersion) return;

            var chart = GuestRrdChart.Build(timeframe, samples);
            _graph.Series = chart.Series;
            _graph.XLabels = chart.XLabels;
            _graph.Timestamps = chart.Times;
            _graphStatus.Text = samples.Count == 0 ? Loc.T("TableTab_Empty") : string.Empty;
        }
        catch (Exception ex) when (!Token.IsCancellationRequested)
        {
            // 일시 오류는 마지막 그래프를 두고 다음 주기에 다시 — 문구만 알린다
            _graphStatus.Text = Loc.T("MainViewModel_M07", ex.Message);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>진행 막대와 글자는 값 두 개(사용량·전체)를 함께 써야 해서 바인딩 대신 코드로 채운다.</summary>
    private void Apply()
    {
        _dot.Fill = StatusBrush(_guest.Status);
        _statusName.Text = GuestDetailViewModel.StatusLabel(_guest.Status);
        _node.Text = _guest.Node;
        _cores.Text = _guest.CpuCount.ToString(CultureInfo.InvariantCulture);
        _uptime.Text = Format.Uptime(_guest.UptimeDisplaySeconds);
        _pool.Text = string.IsNullOrEmpty(_guest.Pool) ? "-" : _guest.Pool;
        _tags.Text = _guest.Tags.Length == 0 ? "-" : string.Join(", ", _guest.Tags);

        _cpuBar.Value = Math.Clamp(_guest.CpuUsagePercent, 0, 100);
        _cpuText.Text = Loc.T("SummaryTab_CpuUsage", _guest.CpuUsagePercent, _guest.CpuCount);
        SetUsage(_memBar, _memText, _guest.MemBytes, _guest.MaxMemBytes);
        SetUsage(_diskBar, _diskText, _guest.DiskBytes, _guest.MaxDiskBytes);
    }

    private static void SetUsage(ProgressBar bar, TextBlock text, long used, long total)
    {
        bar.Value = total > 0 ? Math.Clamp(used * 100.0 / total, 0, 100) : 0;
        text.Text = total > 0
            ? Loc.T("SummaryTab_Usage", Format.Bytes(used), Format.Bytes(total), bar.Value)
            : Format.Bytes(used);
    }

    private static IBrush StatusBrush(string status)
    {
        return status switch
        {
            "running" => new SolidColorBrush(Color.Parse("#3FB96E")),
            "paused" or "suspended" => new SolidColorBrush(Color.Parse("#F0B43E")),
            _ => new SolidColorBrush(Color.Parse("#8A8A8A"))
        };
    }
}
