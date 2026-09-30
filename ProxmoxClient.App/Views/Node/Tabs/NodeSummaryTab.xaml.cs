using System.Globalization;
using System.Windows.Controls;
using System.Windows.Threading;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Node.Tabs;

/// <summary>노드 창의 요약 탭 — 커널·부하·자원 사용량을 주기적으로 새로 읽는다.</summary>
public partial class NodeSummaryTab : UserControl
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly UptimeConverter Uptime = new();

    private readonly ProxmoxApiClient _api;
    private readonly string _node;
    private readonly DispatcherTimer _timer;

    public NodeSummaryTab(ProxmoxApiClient api, string node, IReadOnlyList<TableAction> actions)
    {
        InitializeComponent();
        _api = api;
        _node = node;
        ActionBar.Populate(ActionPanel, actions, this, StatusText);

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();

        Loaded += async (_, _) =>
        {
            _timer.Start();
            await RefreshAsync();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var s = await _api.GetNodeStatusAsync(_node);

            KernelText.Text = s.KernelVersion;
            CoresText.Text = s.CpuCores.ToString(CultureInfo.CurrentCulture);
            LoadText.Text = string.Format(CultureInfo.CurrentCulture, "{0:F2} · {1:F2} · {2:F2}",
                s.LoadAverage1, s.LoadAverage5, s.LoadAverage15);
            UptimeText.Text = Uptime.Convert(s.UptimeSeconds, typeof(string), null, CultureInfo.CurrentCulture)
                as string ?? string.Empty;

            CpuBar.Value = Math.Clamp(s.CpuUsagePercent, 0, 100);
            CpuText.Text = Loc.T("SummaryTab_CpuUsage", s.CpuUsagePercent, s.CpuCores);

            SetUsage(MemBar, MemText, s.MemUsedBytes, s.MemTotalBytes);
            SetUsage(SwapBar, SwapText, s.SwapUsedBytes, s.SwapTotalBytes);
            SetUsage(RootBar, RootText, s.RootFsUsedBytes, s.RootFsTotalBytes);

            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("MainViewModel_M07", ex.Message);
        }
    }

    private static void SetUsage(ProgressBar bar, TextBlock text, long used, long total)
    {
        bar.Value = total > 0 ? Math.Clamp(used * 100.0 / total, 0, 100) : 0;
        text.Text = total > 0
            ? Loc.T("SummaryTab_Usage", ByteFormatter.Format(used), ByteFormatter.Format(total), bar.Value)
            : ByteFormatter.Format(used);
    }
}
