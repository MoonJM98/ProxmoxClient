using System.Windows.Controls;
using System.Windows.Threading;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>
///     게스트 창의 요약 탭 — 상태·자원 사용량을 보여 주고 주기적으로 새로 읽는다.
///     /cluster/resources 한 번으로 모든 값을 얻으므로 추가 호출이 없다.
/// </summary>
public partial class SummaryTab : UserControl
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private readonly DispatcherTimer _timer;

    public SummaryTab(ProxmoxApiClient api, PveResource guest, IReadOnlyList<TableAction> actions)
    {
        InitializeComponent();
        ActionBar.Populate(ActionPanel, actions, this, StatusText);
        _api = api;
        _guest = guest;
        DataContext = guest;

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();

        Loaded += async (_, _) =>
        {
            Apply();
            _timer.Start();
            await RefreshAsync();
        };
        // 탭을 벗어나거나 창이 닫히면 타이머를 멈춰 쓸데없는 호출을 막는다
        Unloaded += (_, _) => _timer.Stop();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var resources = await _api.GetClusterResourcesAsync();
            if (resources.FirstOrDefault(r => r.Id == _guest.Id) is { } fresh)
            {
                _guest.CopyFrom(fresh);
                Apply();
                StatusText.Text = string.Empty;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("MainViewModel_M07", ex.Message);
        }
    }

    /// <summary>진행 막대는 바인딩 대신 코드로 채운다 — 백분율 계산이 값 두 개에 걸쳐 있어서다.</summary>
    private void Apply()
    {
        TagsText.Text = string.Join(", ", _guest.Tags);

        CpuBar.Value = Math.Clamp(_guest.CpuUsagePercent, 0, 100);
        CpuText.Text = Loc.T("SummaryTab_CpuUsage", _guest.CpuUsagePercent, _guest.CpuCount);

        SetUsage(MemBar, MemText, _guest.MemBytes, _guest.MaxMemBytes);
        SetUsage(DiskBar, DiskText, _guest.DiskBytes, _guest.MaxDiskBytes);
    }

    private static void SetUsage(ProgressBar bar, TextBlock text, long used, long total)
    {
        bar.Value = total > 0 ? Math.Clamp(used * 100.0 / total, 0, 100) : 0;
        text.Text = total > 0
            ? Loc.T("SummaryTab_Usage", ByteFormatter.Format(used), ByteFormatter.Format(total), bar.Value)
            : ByteFormatter.Format(used);
    }
}
