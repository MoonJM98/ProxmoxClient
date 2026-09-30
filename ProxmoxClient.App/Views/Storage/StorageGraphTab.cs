using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Storage;

/// <summary>
///     저장소 사용량 그래프(웹 UI 저장소 요약의 Usage) — 기간(시간·일·주·월·년)을 고르면 사용률(%)을 선으로 그린다.
/// </summary>
internal sealed class StorageGraphTab : UserControl
{
    private static readonly (string Tag, string LabelKey)[] Timeframes =
    [
        ("hour", "MainWindow_24"), ("day", "MainWindow_25"), ("week", "MainWindow_26"), ("month", "MainWindow_27"),
        ("year", "MainWindow_28")
    ];

    private static readonly Color UsageColor = Color.FromRgb(0x4F, 0xA3, 0xF7);

    private readonly ProxmoxApiClient _api;
    private readonly string _node;
    private readonly string _storage;
    private readonly ListBox _frames = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly GraphLine _graph = new() { Height = 260, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private int _loadVersion;

    public StorageGraphTab(ProxmoxApiClient api, string node, string storage)
    {
        _api = api;
        _node = node;
        _storage = storage;
        _frames.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        if (TryFindResource("SubTabItem") is Style tabStyle) _frames.ItemContainerStyle = tabStyle;
        foreach (var (tag, labelKey) in Timeframes)
            _frames.Items.Add(new ListBoxItem { Content = Loc.T(labelKey), Tag = tag });
        _frames.SelectionChanged += async (_, _) => await LoadAsync();

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        panel.Children.Add(new InfoTip { Text = Loc.T("StorageGraph_Hint"), Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(_frames);
        panel.Children.Add(_graph);
        panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) =>
        {
            if (_frames.SelectedIndex < 0) _frames.SelectedIndex = 0;
        };
    }

    private async Task LoadAsync()
    {
        if (_frames.SelectedItem is not ListBoxItem { Tag: string timeframe }) return;

        var version = ++_loadVersion; // 기간을 빨리 바꾸면 늦게 온 이전 응답은 버린다
        _status.Text = Loc.T("StorageGraph_Loading");
        try
        {
            var rows = await _api.Storage.RrdDataAsync(_node, _storage, timeframe);
            if (version != _loadVersion) return;

            Show(rows, timeframe);
        }
        catch (Exception ex) when (ex is ProxmoxApiException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException)
        {
            if (version == _loadVersion) _status.Text = Loc.T("StorageGraph_Failed", ex.Message);
        }
    }

    private void Show(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string timeframe)
    {
        var times = rows.Select(r => Number(r, "time") is { } t ? (long)t : 0L).ToList();
        var usage = rows.Select(r => Number(r, "used") is { } used && Number(r, "total") is { } total && total > 0
            ? 100.0 * used / total
            : (double?)null).ToList();
        _graph.Series = [new GraphSeries(Loc.T("StorageGraph_Usage"), usage, UsageColor, GraphAxis.Left,
            GraphValueUnit.Percent)];
        _graph.Timestamps = times.Select(t => t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime
            : (DateTime?)null).ToList();
        var valid = times.Where(t => t > 0).ToList();
        _graph.XLabels = valid.Count >= 2
            ? [Label(valid[0], timeframe), Label(valid[valid.Count / 2], timeframe), Label(valid[^1], timeframe)]
            : [];
        _status.Text = usage.Any(u => u is not null) ? string.Empty : Loc.T("StorageGraph_NoData");
    }

    private static double? Number(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var raw)
               && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string Label(long unixSeconds, string timeframe)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        return timeframe switch
        {
            "hour" or "day" => time.ToString("HH:mm", CultureInfo.CurrentCulture),
            "year" => time.ToString("yy/MM", CultureInfo.CurrentCulture),
            _ => time.ToString("MM/dd", CultureInfo.CurrentCulture)
        };
    }
}
