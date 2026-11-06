using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Core.Settings;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;
using ProxmoxClient.UI.Views.Settings;

namespace ProxmoxClient.UI.Views.Main;

/// <summary>
///     데스크톱 메인 화면(WPF MainWindow 와 같은 배치) 뷰모델. 로그인된 <see cref="ServerSession" /> 을 받아
///     도구 모음·탭(게스트·노드·스토리지·작업)·게스트 상세·전원 동작을 맡는다.
///     자동 새로고침은 WPF 와 같이 설정한 간격의 타이머로 돈다. 화면을 떠나면(제거) 세션을 정리한다.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private const int TaskListCapacity = 100;

    /// <summary>전원 등 작업 완료 여부 확인 간격.</summary>
    private static readonly TimeSpan TaskPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>실시간 상태를 클러스터 인덱스보다 우선하는 최대 시간(인덱스가 따라오면 즉시 해제).</summary>
    private static readonly TimeSpan LiveStatusHoldTime = TimeSpan.FromSeconds(20);

    /// <summary>rrd 그래프 재조회 최소 간격 — rrddata 는 60초 해상도라 1초마다 조회하면 낭비.</summary>
    private static readonly TimeSpan GraphRefreshInterval = TimeSpan.FromSeconds(30);

    // 통합 그래프 시리즈 색 — 왼쪽 축(%) 파랑·초록, 오른쪽 축(초당 바이트) 주황·보라 (WPF 와 같다)
    private static readonly Color CpuSeriesColor = Color.FromRgb(0x4F, 0x8C, 0xFF);
    private static readonly Color MemorySeriesColor = Color.FromRgb(0x3F, 0xB9, 0x6E);
    private static readonly Color NetworkSeriesColor = Color.FromRgb(0xF0, 0x9A, 0x3E);
    private static readonly Color DiskIoSeriesColor = Color.FromRgb(0xB0, 0x7C, 0xF0);

    /// <summary>게스트 선택·상태가 바뀔 때 다시 계산할 표시 값 — 한 번에 알린다.</summary>
    private static readonly string[] GuestStateProperties =
    [
        nameof(HasSelectedGuest), nameof(NoSelectedGuest), nameof(IsSelectedCt), nameof(IsSelectedVm),
        nameof(GuestMemBar), nameof(GuestDiskBar), nameof(GuestPoolText), nameof(CanOpenConsole),
        nameof(ShowConsoleMenuButton), nameof(ShowConsolePlainButton), nameof(CanOpenSnapshots),
        nameof(ShowStartButton), nameof(ShowStopSplit), nameof(ShowShutdownItem), nameof(ShowRebootItem),
        nameof(ShowSuspendItem), nameof(ShowResumeItem), nameof(ShowHibernateItem), nameof(ShowStopItem),
        nameof(ShowResetItem), nameof(CanOpenSettingsTab), nameof(CanOpenCloneTab), nameof(CanOpenBackupTab),
        nameof(CanOpenFirewallTab), nameof(CanOpenSnapshotTab), nameof(IsIdle), nameof(CanRefresh)
    ];

    private readonly AppSettingsStore _settingsStore = new();
    private readonly DispatcherTimer _timer;

    /// <summary>전원 작업 직후 조회한 게스트 실시간 상태(VMID → 상태, 만료 시각).</summary>
    private readonly Dictionary<int, (string Status, long ExpiresAt)> _liveStatusOverrides = [];

    private AppSettings _settings = new();
    private Task<AppSettings>? _settingsTask;
    private PveResource? _watchedGuest;
    private PveNode? _watchedNode;
    private bool _disposed;
    private bool _refreshing;
    private bool _tickBusy;
    /// <summary>화면이 보이는 중(OnAppearing~OnDisappearing) — 앱이 배경에서 돌아올 때 타이머를 다시 켤지.</summary>
    private bool _visible;
    /// <summary>인증서가 바뀌어 자동 새로고침을 멈췄다 — 배경에서 돌아와도 다시 켜지 않는다.</summary>
    private bool _haltedByCertificate;
    /// <summary>설정을 화면에 옮기는 중 — 그때의 템플릿 표시 변화는 저장하지 않는다(사용자가 바꾼 것만 저장).</summary>
    private bool _applyingSettings;

    public MainViewModel(ServerSession session)
    {
        Session = session;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AppSettings.DefaultRefreshIntervalSeconds) };
        _timer.Tick += async (_, _) => await OnTimerTickAsync();
        AppLifecycle.ForegroundChanged += OnForegroundChanged;

        var profile = session.Profile;
        ConnectionStatus = L.T("Main_ConnectedTo", profile.Name);
        ServerInfo = $"{profile.Host}:{profile.Port} · PVE {session.Version} · " +
                     (profile.AuthMode == AuthMode.Password ? L.T("Main_AuthPassword") : L.T("Main_AuthToken"));
        StatusMessage = L.T("MainViewModel_M04");
        Timeframes =
        [
            new TimeframeOption("hour", L.T("MainWindow_24")),
            new TimeframeOption("day", L.T("MainWindow_25")),
            new TimeframeOption("week", L.T("MainWindow_26")),
            new TimeframeOption("month", L.T("MainWindow_27")),
            new TimeframeOption("year", L.T("MainWindow_28"))
        ];
    }

    /// <summary>로그인한 서버 세션 — 이 화면이 떠나면 정리된다.</summary>
    public ServerSession Session { get; }

    public override string Title => Session.DisplayName;

    public IReadOnlyList<TimeframeOption> Timeframes { get; }

    public ObservableCollection<PveResource> Guests { get; } = [];

    /// <summary>게스트 표에 보이는 목록(템플릿 숨김 반영). 선택·조회는 <see cref="Guests" /> 를 쓴다.</summary>
    public ObservableCollection<PveResource> VisibleGuests { get; } = [];

    public ObservableCollection<PveNode> Nodes { get; } = [];

    public ObservableCollection<PveStorage> Storages { get; } = [];

    public ObservableCollection<PveTask> Tasks { get; } = [];

    [ObservableProperty] public partial bool AutoRefresh { get; set; } = true;

    [ObservableProperty] public partial string ConnectionStatus { get; set; } = string.Empty;

    [ObservableProperty] public partial string ServerInfo { get; set; } = string.Empty;

    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGuestsPanel), nameof(ShowNodesPanel), nameof(ShowStoragePanel),
        nameof(ShowTasksPanel))]
    public partial int NavIndex { get; set; }

    public bool ShowGuestsPanel => NavIndex == 0;

    public bool ShowNodesPanel => NavIndex == 1;

    public bool ShowStoragePanel => NavIndex == 2;

    public bool ShowTasksPanel => NavIndex == 3;

    /// <summary>상태 표시줄 개수 — 전체 게스트·노드 수(WPF Guests.Count · Nodes.Count).</summary>
    [ObservableProperty] public partial int GuestTotal { get; set; }

    [ObservableProperty] public partial int NodeTotal { get; set; }

    /// <summary>게스트 표에 보이는 줄 수(템플릿 숨김 반영) — WPF GuestsView.Count.</summary>
    [ObservableProperty] public partial int VisibleGuestCount { get; set; }

    /// <summary>템플릿 표시 — 끄면 목록에서 템플릿과 '템플릿' 열을 숨긴다(설정에 저장).</summary>
    [ObservableProperty] public partial bool ShowTemplates { get; set; } = true;

    /// <summary>전원 작업 중 — 명령 버튼을 잠근다(WPF IsBusy 와 같은 역할).</summary>
    public bool IsIdle => !IsBusy;

    public bool CanRefresh => !IsBusy;

    [ObservableProperty] public partial PveResource? SelectedGuest { get; set; }

    [ObservableProperty] public partial PveNode? SelectedNode { get; set; }

    [ObservableProperty] public partial PveNodeStatus? NodeStatus { get; set; }

    /// <summary>게스트 표에서 고른 행의 상세 — WPF SelectedGuest 와 같다.</summary>
    public bool HasSelectedGuest => SelectedGuest is not null;

    public bool NoSelectedGuest => SelectedGuest is null;

    /// <summary>"만들기" 메뉴 — 등록된 클러스터 기능(마법사 등). 없으면 버튼을 잠근다.</summary>
    public IReadOnlyList<ClusterFeature> CreateEntries => FeatureRegistry.ClusterFeatures
        .Where(f => f.Id != SettingsModule.AppSettingsFeatureId) // 앱 설정은 "관리" 항목이라 만들기 메뉴에는 없다
        .OrderBy(f => f.Order).ToList();

    public bool HasCreateEntries => CreateEntries.Count > 0;

    public bool HasDatacenter => FeatureRegistry.Datacenter is not null;

    public bool HasNodeManagement => FeatureRegistry.Node is not null;

    /// <summary>"만들기" 버튼 — 권한이 있을 때만 보인다(WPF Permissions.CanAllocate).</summary>
    public bool CanCreate => Session.Permissions.CanAllocate;

    public override void OnAppearing()
    {
        if (_disposed) return;

        _visible = true;
        if (AppLifecycle.IsForeground && !_haltedByCertificate) _timer.Start();
        _ = InitializeAsync();
    }

    public override void OnDisappearing(bool removed)
    {
        _visible = false;
        _timer.Stop();
        if (removed) Dispose();
    }

    /// <summary>앱이 배경으로 가면 주기 새로고침을 멈추고(배터리·데이터), 돌아오면 바로 한 번 새로 고친 뒤 다시 돈다.</summary>
    private void OnForegroundChanged(bool foreground)
    {
        if (_disposed || !_visible || _haltedByCertificate) return;

        if (!foreground)
        {
            _timer.Stop();
            return;
        }

        _timer.Start();
        _ = OnTimerTickAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _timer.Stop();
        AppLifecycle.ForegroundChanged -= OnForegroundChanged;
        DetachGuest();
        DetachNode();
        Session.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>자동 새로고침 간격 변경(설정 저장 시 즉시 적용).</summary>
    public void ApplyRefreshInterval(TimeSpan interval)
    {
        _timer.Interval = interval;
    }

    /// <summary>설정 파일을 한 번 읽는다 — 새로고침 간격·자동 새로고침·용량 단위·템플릿 표시·영역 크기.</summary>
    public Task<AppSettings> LoadSettingsAsync()
    {
        return _settingsTask ??= LoadSettingsCoreAsync();
    }

    /// <summary>
    ///     앱 설정 화면에서 저장한 뒤 다시 읽는다 — 캐시를 버려야 이후 영역 크기 저장이 옛 값을 되돌리지 않는다.
    ///     새로고침 간격·자동 새로고침·템플릿 표시·용량 단위도 곧바로 적용된다.
    /// </summary>
    internal Task ReloadSettingsAsync()
    {
        _settingsTask = LoadSettingsCoreAsync();
        return _settingsTask;
    }

    /// <summary>영역 크기 손잡이를 놓았다 — 다음 실행에도 같은 크기로 열리게 저장한다(WPF 와 같다).</summary>
    public Task SaveLayoutAsync(double guestDetailHeight, double nodeListWidth)
    {
        return UpdateSettingsAsync(s => s with { GuestDetailHeight = guestDetailHeight, NodeListWidth = nodeListWidth });
    }

    private async Task<AppSettings> LoadSettingsCoreAsync()
    {
        _settings = (await _settingsStore.LoadAsync().ConfigureAwait(true)).Normalize();
        ByteFormatter.DisplayUnit = _settings.ByteUnit;
        ApplyRefreshInterval(_settings.RefreshInterval);
        AutoRefresh = _settings.AutoRefreshOnStart;
        _applyingSettings = true;
        try
        {
            ShowTemplates = _settings.ShowTemplates;
        }
        finally
        {
            _applyingSettings = false;
        }
        return _settings;
    }

    private async Task UpdateSettingsAsync(Func<AppSettings, AppSettings> change)
    {
        _settings = change(_settings).Normalize();
        try
        {
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 저장 실패는 다음 실행 때 원래 크기로 열릴 뿐 — 화면 동작에는 영향이 없다
            System.Diagnostics.Debug.WriteLine($"[설정] 저장 실패: {ex.Message}");
        }
    }

    private async Task InitializeAsync()
    {
        await LoadSettingsAsync().ConfigureAwait(true);
        await RefreshDataAsync().ConfigureAwait(true);
    }

    /// <summary>도구 모음 새로고침 · 주기 새로고침 — 클러스터 현황과 최근 작업을 다시 읽는다.</summary>
    public async Task RefreshDataAsync()
    {
        if (_disposed || IsRemoved || _refreshing) return;

        _refreshing = true;
        var api = Session.Api;
        try
        {
            var overviewTask = Session.TakePrefetchedOverview() ?? api.GetClusterOverviewAsync();
            var tasksTask = api.GetClusterTasksAsync();
            var overview = await overviewTask.ConfigureAwait(true);
            IReadOnlyList<PveTask> tasks;
            try
            {
                tasks = await tasksTask.ConfigureAwait(true);
            }
            catch (Exception ex) when (IsTransientError(ex) || ex is ProxmoxApiException)
            {
                tasks = Tasks.ToList(); // 작업 목록 권한이 없어도 나머지는 보여 준다
            }

            if (_disposed) return;

            var selectedId = SelectedGuest?.Id;
            var selectedNodeName = SelectedNode?.Node;

            CollectionSync.Sync(Guests,
                overview.Guests.Where(r => r.Kind is ResourceKind.Qemu or ResourceKind.Lxc)
                    .OrderBy(r => r.VmId).ToList(),
                g => g.Id, (current, fresh) => current.CopyFrom(fresh));
            ApplyLiveStatusOverrides();
            CollectionSync.Sync(Nodes,
                overview.Nodes.OrderBy(n => n.Node, StringComparer.OrdinalIgnoreCase).ToList(),
                n => n.Node, (current, fresh) => current.CopyFrom(fresh));
            CollectionSync.Sync(Storages,
                overview.Storages.OrderBy(s => s.Node, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(s => s.Storage, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(s => s.Id, StringComparer.Ordinal).ToList(),
                s => s.Id, (current, fresh) => current.CopyFrom(fresh));
            CollectionSync.Sync(Tasks,
                tasks.OrderByDescending(t => t.StartTimeUtc).ThenBy(t => t.Upid, StringComparer.Ordinal)
                    .Take(TaskListCapacity).ToList(),
                t => t.Upid, (current, fresh) => current.CopyFrom(fresh));

            ArrangeVisibleGuests();
            GuestTotal = Guests.Count;
            NodeTotal = Nodes.Count;

            // 템플릿을 숨긴 채 템플릿이 된 게스트는 표에 없으므로 다시 고르지 않는다
            if (selectedId is { } id && SelectedGuest?.Id != id)
                SelectedGuest = Guests.FirstOrDefault(g => g.Id == id && (ShowTemplates || !g.IsTemplate));

            if (selectedNodeName is { } nodeName && SelectedNode?.Node != nodeName)
                SelectedNode = Nodes.FirstOrDefault(n => n.Node == nodeName);

            // 첫 로드에는 선택이 비어 있어 노드 정보·그래프가 빈 화면이 된다 — 기본 노드를 골라 준다
            SelectedNode ??= PickDefaultNode();

            // 보이는 탭의 부가 정보만 갱신 — 그래프는 GraphRefreshInterval 주기(선택·기간 변경 시엔 즉시)
            await Task.WhenAll(
                    ShowNodesPanel ? LoadNodeStatusAsync() : Task.CompletedTask,
                    ShowGuestsPanel ? LoadGuestGraphAsync(false) : Task.CompletedTask,
                    ShowNodesPanel ? LoadNodeGraphAsync(false) : Task.CompletedTask)
                .ConfigureAwait(true);
            StatusMessage = L.T("MainViewModel_M04");
        }
        catch (CertificateTrustException ex)
        {
            // 연결 중 서버 인증서가 바뀜 — 중간자 가능성이 있으므로 조용히 재시도하지 않고 자동 새로고침을 멈춘다
            StatusMessage = L.T("MainViewModel_M06", ex.Message);
            _haltedByCertificate = true;
            _timer.Stop();
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            StatusMessage = L.T("MainViewModel_M07", ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>화면 주기 틱 — 업타임 갱신, 실행 중 작업 상태, 자동 새로고침.</summary>
    private async Task OnTimerTickAsync()
    {
        if (_disposed || IsRemoved) return;

        // 업타임은 서버 값이 수 초 주기라 그대로 두면 멈춘 것처럼 보인다 — 매 틱 경과 시간을 다시 계산
        foreach (var guest in Guests) guest.TickUptime();
        foreach (var node in Nodes) node.TickUptime();

        if (_tickBusy) return;

        _tickBusy = true;
        try
        {
            var running = Tasks.Where(t => t.IsRunning).Select(t => t.Upid).ToArray();
            foreach (var upid in running)
            {
                var fresh = await Session.Api.GetTaskStatusAsync(upid).ConfigureAwait(true);
                if (_disposed) return;

                UpsertTask(fresh);
            }

            if (AutoRefresh && !_refreshing) await RefreshDataAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            // 타이머 경로 — 여기서 새면 전역 예외 창이 매 틱 쌓인다. 다음 틱에 재시도.
        }
        finally
        {
            _tickBusy = false;
        }
    }

    /// <summary>
    ///     서버 목록 기준으로 게스트 표 보기를 맞춘다 — 템플릿 숨김이면 템플릿을 뺀다.
    ///     객체는 그대로 두므로 선택·스크롤이 유지된다.
    /// </summary>
    private void ArrangeVisibleGuests()
    {
        var visible = Guests.Where(g => ShowTemplates || !g.IsTemplate).ToList();
        CollectionSync.Arrange(VisibleGuests, visible);
        VisibleGuestCount = visible.Count;
    }

    /// <summary>기본 선택 노드 — 접속한 서버와 같은 이름의 노드를 우선하고, 없으면 온라인 노드, 그다음 첫 노드.</summary>
    private PveNode? PickDefaultNode()
    {
        if (Nodes.Count == 0) return null;

        var host = Session.Profile.Host;
        var shortHost = host.Split('.')[0]; // pve1.example.com → pve1

        return Nodes.FirstOrDefault(n => string.Equals(n.Node, host, StringComparison.OrdinalIgnoreCase))
               ?? Nodes.FirstOrDefault(n => string.Equals(n.Node, shortHost, StringComparison.OrdinalIgnoreCase))
               ?? Nodes.FirstOrDefault(n => n.IsOnline)
               ?? Nodes[0];
    }

    /// <summary>새로고침·조회 경로에서 삼키고 다음 주기에 재시도할 오류(연결 끊김·타임아웃·연결 해제 중 Dispose 등).</summary>
    private static bool IsTransientError(Exception ex)
    {
        return ex is ProxmoxApiException or CertificateTrustException or HttpRequestException or IOException
            or OperationCanceledException or ObjectDisposedException;
    }

    /// <summary>도구 모음 "서버 전환" · "연결 끊기" — 프로필 목록으로 돌아간다(세션은 화면이 떠날 때 정리된다).</summary>
    [RelayCommand]
    private Task LeaveServerAsync()
    {
        return AppServices.Navigator.PopToRootAsync();
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        return RefreshDataAsync();
    }

    /// <summary>도구 모음 "데이터센터" — 등록된 데이터센터 화면을 연다(없으면 버튼이 잠겨 있다).</summary>
    [RelayCommand]
    private Task OpenDatacenterAsync()
    {
        return FeatureRegistry.Datacenter is { } create
            ? AppServices.Navigator.PushViewAsync(L.T("DcWindow_Title"), create(Session))
            : Task.CompletedTask;
    }

    /// <summary>"만들기" 메뉴 항목 — 기능을 실행하고 목록을 다시 읽는다(WPF 와 같다).</summary>
    public async Task RunCreateAsync(ClusterFeature feature)
    {
        try
        {
            await feature.Run(Session);
        }
        catch (WriteCancelledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = ErrorText.Describe(ex);
        }

        await RefreshDataAsync();
    }

    /// <summary>선택한 게스트의 상태(전원·템플릿·이름)가 바뀌면 버튼 표시를 다시 계산하도록 구독한다.</summary>
    private void AttachGuest(PveResource? guest)
    {
        DetachGuest();
        if (guest is null) return;

        _watchedGuest = guest;
        guest.PropertyChanged += OnWatchedGuestChanged;
    }

    private void DetachGuest()
    {
        if (_watchedGuest is null) return;

        _watchedGuest.PropertyChanged -= OnWatchedGuestChanged;
        _watchedGuest = null;
    }

    private void OnWatchedGuestChanged(object? sender, PropertyChangedEventArgs e)
    {
        RaiseGuestState();
    }

    private void AttachNode(PveNode? node)
    {
        DetachNode();
        if (node is null) return;

        _watchedNode = node;
        node.PropertyChanged += OnWatchedNodePropertyChanged;
    }

    private void DetachNode()
    {
        if (_watchedNode is null) return;

        _watchedNode.PropertyChanged -= OnWatchedNodePropertyChanged;
        _watchedNode = null;
    }

    private void OnWatchedNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowWakeButton));
    }

    /// <summary>선택 게스트의 표시 값을 모두 다시 알린다(상태·템플릿·선택 변화).</summary>
    private void RaiseGuestState()
    {
        foreach (var name in GuestStateProperties) OnPropertyChanged(name);
        OnPropertyChanged(nameof(SelectedGuest));
    }

    partial void OnSelectedGuestChanged(PveResource? value)
    {
        AttachGuest(value);
        RaiseGuestState();
        _ = LoadGuestGraphAsync();
    }

    partial void OnSelectedNodeChanged(PveNode? value)
    {
        AttachNode(value);
        NodeStatus = null;
        _nodeGraphSignature = null;
        OnPropertyChanged(nameof(ShowWakeButton));
        _ = LoadNodeStatusAsync();
        _ = LoadNodeGraphAsync();
    }

    partial void OnNavIndexChanged(int value)
    {
        if (ShowGuestsPanel)
        {
            _ = LoadGuestGraphAsync();
        }
        else if (ShowNodesPanel)
        {
            SelectedNode ??= PickDefaultNode();
            _ = LoadNodeStatusAsync();
            _ = LoadNodeGraphAsync();
        }
    }

    partial void OnShowTemplatesChanged(bool value)
    {
        ArrangeVisibleGuests();
        if (!value && SelectedGuest is { IsTemplate: true }) SelectedGuest = null; // 숨긴 행을 선택한 채 두지 않는다
        if (!_applyingSettings) _ = UpdateSettingsAsync(s => s with { ShowTemplates = value });
    }

    /// <summary>전원 작업 중 표시 전환 — 버튼 잠금과 게스트 표시 값을 함께 다시 계산한다.</summary>
    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanRefresh));
        RaiseGuestState();
    }

    partial void OnNodeStatusChanged(PveNodeStatus? value)
    {
        foreach (var name in NodeStatusProperties) OnPropertyChanged(name);
    }

    private static readonly string[] NodeStatusProperties =
    [
        nameof(NodeKernelText), nameof(NodeCoresText), nameof(NodeLoad1Text), nameof(NodeLoad5Text),
        nameof(NodeLoad15Text), nameof(NodeUptimeText), nameof(NodeMemBar), nameof(NodeSwapBar), nameof(NodeRootBar)
    ];

    /// <summary>노드 옆에 깨우기 버튼은 노드가 꺼져 있을 때만 — 켜진 노드가 Wake-on-LAN 패킷을 보낸다.</summary>
    public bool ShowWakeButton => SelectedNode is { IsOnline: false };
}

/// <summary>그래프 기간 선택지(WPF 의 시간 범위 목록).</summary>
public sealed record TimeframeOption(string Key, string Label);

/// <summary>사용량 막대 한 칸 — 값·최대·막대 위 글자(WPF 의 ProgressBar + 겹친 TextBlock).</summary>
public sealed record UsageBar(double Value, double Maximum, string Text)
{
    public static UsageBar Empty { get; } = new(0, 1, "-");
}
