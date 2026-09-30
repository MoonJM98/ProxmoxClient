using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Core.Settings;
using ProxmoxClient.Core.Vpn;

namespace ProxmoxClient.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const int TaskListCapacity = 100;
    private const int VpnLogCapacityChars = 16000;
    /// <summary>전원 등 작업 완료 여부 확인 간격.</summary>
    private static readonly TimeSpan TaskPollInterval = TimeSpan.FromSeconds(1);
    /// <summary>실시간 상태를 클러스터 인덱스보다 우선하는 최대 시간(인덱스가 따라오면 즉시 해제).</summary>
    private static readonly TimeSpan LiveStatusHoldTime = TimeSpan.FromSeconds(20);
    /// <summary>rrd 그래프 재조회 최소 간격 — rrddata 는 시간 범위에서도 60초 해상도라 1초마다 조회하면 낭비.</summary>
    private static readonly TimeSpan GraphRefreshInterval = TimeSpan.FromSeconds(30);
    // 통합 그래프 시리즈 색 — 왼쪽 축(%) 파랑·초록, 오른쪽 축(초당 바이트) 주황·보라
    private static readonly Color CpuSeriesColor = Color.FromRgb(0x4F, 0x8C, 0xFF);
    private static readonly Color MemorySeriesColor = Color.FromRgb(0x3F, 0xB9, 0x6E);
    private static readonly Color NetworkSeriesColor = Color.FromRgb(0xF0, 0x9A, 0x3E);
    private static readonly Color DiskIoSeriesColor = Color.FromRgb(0xB0, 0x7C, 0xF0);
    /// <summary>
    ///     전원 작업 직후 조회한 게스트 실시간 상태(VMID → 상태, 만료 시각). /cluster/resources 는 pvestatd 주기로
    ///     몇 초 늦게 바뀌므로, 뒤이은 새로고침이 늦은 값으로 버튼 표시를 되돌리지 않도록 잠시 실시간 값을 우선한다.
    /// </summary>
    private readonly Dictionary<int, (string Status, long ExpiresAt)> _liveStatusOverrides = [];
    private readonly ProfileStore _store = new();
    private readonly DispatcherTimer _timer;
    private readonly OpenVpnManager _vpn = new();
    [ObservableProperty] private bool _autoRefresh = true;
    [ObservableProperty] private string _connectionStatus = Loc.T("Main_NotConnected");
    private bool _disposed;
    private long _guestGraphLoadedAt;
    [ObservableProperty] private IReadOnlyList<GraphSeries>? _guestGraphSeries;
    private string? _guestGraphSignature;
    [ObservableProperty] private IReadOnlyList<DateTime?>? _guestGraphTimes;
    // 선택 변경과 틱이 겹칠 때 늦게 도착한 이전 요청이 새 그래프를 덮어쓰지 않도록 요청 버전으로 거른다
    private int _guestGraphVersion;
    [ObservableProperty] private IReadOnlyList<string>? _guestGraphXLabels;
    [ObservableProperty] private string _guestTimeframe = "hour";
    [ObservableProperty] private ObservableCollection<PveResource> _guests = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShutdownGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(RebootGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(VpnConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(VpnDisconnectCommand))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShutdownGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(RebootGuestCommand))]
    private bool _isConnected;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGuestsPanel))]
    [NotifyPropertyChangedFor(nameof(ShowNodesPanel))]
    private int _navIndex;
    private long _nodeGraphLoadedAt;
    [ObservableProperty] private IReadOnlyList<GraphSeries>? _nodeGraphSeries;
    private string? _nodeGraphSignature;
    [ObservableProperty] private IReadOnlyList<DateTime?>? _nodeGraphTimes;
    private int _nodeGraphVersion;
    [ObservableProperty] private IReadOnlyList<string>? _nodeGraphXLabels;
    [ObservableProperty] private PveNodeStatus? _nodeStatus;
    [ObservableProperty] private string _nodeTimeframe = "hour";
    [ObservableProperty] private ObservableCollection<PveNode> _nodes = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenConsole))]
    [NotifyPropertyChangedFor(nameof(CanChooseConsole))]
    [NotifyPropertyChangedFor(nameof(CanOpenSnapshots))]
    private PermissionsInfo? _permissions;
    [ObservableProperty] private ObservableCollection<ConnectionProfile> _profiles = [];
    private bool _refreshing;
    /// <summary>rrddata(JSON) 미지원 서버로 확인됨 — 이후 PNG 경로만 사용(매 조회마다 실패 요청 생략). 연결마다 초기화.</summary>
    /// <summary>로그인 화면이 전달한 런타임 프로필(자격 증명 포함). 저장본에는 자격 증명이 없다.</summary>
    private ConnectionProfile? _runtimeProfile;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShutdownGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(RebootGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(SuspendGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(HibernateGuestCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetGuestCommand))]
    [NotifyPropertyChangedFor(nameof(CanOpenConsole))]
    [NotifyPropertyChangedFor(nameof(CanChooseConsole))]
    [NotifyPropertyChangedFor(nameof(CanOpenSnapshots))]
    private PveResource? _selectedGuest;
    [ObservableProperty] private PveNode? _selectedNode;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(VpnConnectCommand))]
    private ConnectionProfile? _selectedProfile;
    [ObservableProperty] private string _serverInfo = "";
    [ObservableProperty] private string _statusMessage = Loc.T("Main_SelectProfile");
    [ObservableProperty] private ObservableCollection<PveStorage> _storages = [];
    [ObservableProperty] private ObservableCollection<PveTask> _tasks = [];
    /// <summary>타이머 틱 재진입 방지 — 서버 응답이 간격보다 느리면 틱마다 작업 상태 조회가 겹겹이 쌓이던 문제 방지.</summary>
    private bool _tickBusy;
    /// <summary>창이 최소화돼 목록을 볼 수 없는 동안 — 자동 새로고침(매 틱 전체 목록 조회)을 쉰다.</summary>
    private bool _backgrounded;
    [ObservableProperty] private long _vpnBytesIn;
    [ObservableProperty] private long _vpnBytesOut;
    [ObservableProperty] private string _vpnLogText = "";
    [ObservableProperty] private VpnState _vpnState = VpnState.Disconnected;
    [ObservableProperty] private string _vpnStatusText = Loc.T("Vpn_NotConnected");
    [ObservableProperty] private string? _vpnVirtualIp;
    public MainViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(AppSettings.DefaultRefreshIntervalSeconds)
        };
        _timer.Tick += async (_, _) => await OnTimerTickAsync();
        _timer.Start();

        _vpn.StateChanged += HandleVpnStateChanged;
        _vpn.LogReceived += OnVpnLogReceived;
        _vpn.StatsUpdated += OnVpnStatsUpdated;
    }
    public bool ShowGuestsPanel => NavIndex == 0;
    public bool ShowNodesPanel => NavIndex == 1;
    public ProxmoxApiClient? Api { get; private set; }
    /// <summary>서버 인증서 신뢰 확인 UI(창이 설정). 신뢰하면 프로필에 지문을 기록하고 true.</summary>
    public Func<CertificateRejection, ConnectionProfile, bool>? ConfirmCertificate { get; set; }
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _timer.Stop();
        try
        {
            if (_vpn.State is not VpnState.Disconnected) _vpn.DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _vpn.Dispose();
        Api?.Dispose();
        GC.SuppressFinalize(this);
    }
    /// <summary>시작 시 설정 적용 — 새로고침 간격과 자동 새로고침 기본 상태.</summary>
    public void ApplyStartupSettings(AppSettings settings)
    {
        var normalized = settings.Normalize();
        ApplyRefreshInterval(normalized.RefreshInterval);
        AutoRefresh = normalized.AutoRefreshOnStart;
    }
    /// <summary>
    ///     창 최소화 여부 — 최소화 동안은 자동 새로고침을 멈추고(실행 중 작업의 완료 확인은 계속),
    ///     다시 보이면 곧바로 한 번 새로고침해 밀린 변경을 반영한다.
    /// </summary>
    public void SetBackgrounded(bool backgrounded)
    {
        if (_backgrounded == backgrounded) return;
        _backgrounded = backgrounded;
        if (!backgrounded && AutoRefresh && IsConnected) _ = RefreshDataAsync();
    }
    /// <summary>자동 새로고침 간격 변경(설정 저장 시 즉시 적용). 사용자가 켜고 끈 자동 새로고침 상태는 건드리지 않는다.</summary>
    public void ApplyRefreshInterval(TimeSpan interval)
    {
        _timer.Interval = interval;
    }
    partial void OnSelectedGuestChanged(PveResource? value)
    {
        _ = LoadGuestGraphAsync();
    }
    partial void OnSelectedNodeChanged(PveNode? value)
    {
        NodeStatus = null;
        _nodeGraphSignature = null;
        _ = LoadNodeStatusAsync();
        _ = LoadNodeGraphAsync();
    }
    partial void OnGuestTimeframeChanged(string value)
    {
        _ = LoadGuestGraphAsync();
    }
    partial void OnNodeTimeframeChanged(string value)
    {
        _ = LoadNodeGraphAsync();
    }
    /// <summary>숨겨진 패널의 그래프·노드 상태는 새로고침에서 건너뛰므로, 패널을 열 때 바로 최신으로 조회.</summary>
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
    /// <summary>기본 선택 노드 — 접속한 서버와 같은 이름의 노드를 우선하고, 없으면 온라인 노드, 그다음 첫 노드.</summary>
    private PveNode? PickDefaultNode()
    {
        if (Nodes.Count == 0) return null;

        var host = Api?.Profile.Host ?? string.Empty;
        var shortHost = host.Split('.')[0]; // pve1.example.com → pve1

        return Nodes.FirstOrDefault(n => string.Equals(n.Node, host, StringComparison.OrdinalIgnoreCase))
               ?? Nodes.FirstOrDefault(n => string.Equals(n.Node, shortHost, StringComparison.OrdinalIgnoreCase))
               ?? Nodes.FirstOrDefault(n => n.IsOnline)
               ?? Nodes[0];
    }
    public async Task LoadProfilesAsync()
    {
        var list = await _store.LoadAsync().ConfigureAwait(true);
        var selectedId = SelectedProfile?.Id;
        Profiles = new ObservableCollection<ConnectionProfile>(list);
        SelectedProfile = list.FirstOrDefault(p => p.Id == selectedId) ?? list.FirstOrDefault();
    }
    public async Task SaveProfileAsync(ConnectionProfile profile)
    {
        await _store.SaveAsync(profile).ConfigureAwait(true);
        await LoadProfilesAsync().ConfigureAwait(true);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id);
        StatusMessage = Loc.T("MainViewModel_M01", profile.Name);
    }
    public async Task ConnectWithProfileAsync(ConnectionProfile profile)
    {
        _runtimeProfile = profile; // 비밀번호가 살아 있는 런타임 복사본으로 연결
        await SaveProfileAsync(profile).ConfigureAwait(true);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id);
        await ConnectAsync().ConfigureAwait(true);
    }
    public async Task DeleteProfileAsync(ConnectionProfile profile)
    {
        await _store.DeleteAsync(profile.Id).ConfigureAwait(true);
        await LoadProfilesAsync().ConfigureAwait(true);
        StatusMessage = Loc.T("MainViewModel_M02", profile.Name);
    }
    private bool CanConnect()
    {
        return SelectedProfile is not null && !IsConnected && !IsBusy;
    }
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var profile = _runtimeProfile ?? SelectedProfile;
        _runtimeProfile = null;
        if (profile is null) return;

        IsBusy = true;
        ConnectionStatus = Loc.T("ConsoleWindow_06");
        try
        {
            if (profile.UseVpn && _vpn.State is not (VpnState.Connected or VpnState.Reconnecting))
            {
                StatusMessage = Loc.T("MainViewModel_M03");
                await _vpn.ConnectAsync(profile.VpnConfigPath!, profile.VpnExePath).ConfigureAwait(true);
            }

            Api?.Dispose();
            var api = new ProxmoxApiClient(profile);
            Api = api;
            // 서버 인증서를 아직 신뢰하지 않았거나 바뀌었으면 사용자에게 지문을 확인받고, 신뢰하면 프로필에 저장 후 재시도
            await CertificateTrust.RunAsync(
                    () => api.LoginAsync(),
                    rejection => ConfirmCertificate?.Invoke(rejection, profile) ?? false,
                    () => _store.SaveAsync(profile))
                .ConfigureAwait(true);

            string versionText;
            try
            {
                var version = await Api.GetVersionAsync().ConfigureAwait(true);
                versionText = $"PVE {version.Version} · ";
            }
            catch (ProxmoxApiException)
            {
                versionText = ""; // /version이 인증을 요구하는 서버가 있음
            }

            IsConnected = true;
            ConnectionStatus = Loc.T("Main_ConnectedTo", profile.Name);
            ServerInfo = $"{profile.Host}:{profile.Port} · {versionText}" +
                         (profile.AuthMode == AuthMode.Password ? Loc.T("Main_AuthPassword") : Loc.T("Main_AuthToken"));

            try
            {
                Permissions = await Api.GetPermissionsSummaryAsync().ConfigureAwait(true);
            }
            catch (ProxmoxApiException)
            {
                Permissions = PermissionsInfo.Admin; // 조회 실패 시 기존 동작 유지(전체 표시)
            }

            StatusMessage = Loc.T("MainViewModel_M04");
            await RefreshDataAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            ConnectionStatus = Loc.T("Main_ConnectFailed");
            await DisconnectCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
    private bool CanDisconnect()
    {
        return IsConnected && !IsBusy;
    }
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(true);
            StatusMessage = Loc.T("MainViewModel_M05");
        }
        finally
        {
            IsBusy = false;
        }
    }
    private async Task DisconnectCoreAsync()
    {
        Api?.Dispose();
        Api = null;
        IsConnected = false;
        ConnectionStatus = Loc.T("Main_NotConnected");
        ServerInfo = "";
        Guests.Clear();
        Nodes.Clear();
        Tasks.Clear();
        NodeStatus = null;
        GuestGraphSeries = null;
        NodeGraphSeries = null;
        // 진행 중이던 그래프 요청 결과가 연결 해제 후 도착해도 버리도록 버전을 올린다
        _guestGraphVersion++;
        _nodeGraphVersion++;
        _guestGraphSignature = null;
        _nodeGraphSignature = null;
        _guestGraphLoadedAt = 0;
        _nodeGraphLoadedAt = 0;
        await Task.CompletedTask.ConfigureAwait(true);
    }
    private bool CanRefresh()
    {
        return IsConnected && !IsBusy;
    }
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RefreshDataAsync().ConfigureAwait(true);
    }
    public async Task RefreshDataAsync()
    {
        if (Api is not { } api || _refreshing) return;

        _refreshing = true;
        try
        {
            // 게스트·노드·스토리지는 /cluster/resources 한 번에 들어온다. 작업 목록만 따로라 둘을 병렬로 조회.
            var overviewTask = api.GetClusterOverviewAsync();
            var tasksTask = api.GetClusterTasksAsync();
            await Task.WhenAll(overviewTask, tasksTask).ConfigureAwait(true);
            if (!ReferenceEquals(api, Api)) return; // 기다리는 사이 연결 해제·재연결 — 이전 서버 결과로 목록을 채우지 않는다

            var overview = await overviewTask.ConfigureAwait(true);
            var tasks = await tasksTask.ConfigureAwait(true);
            var resources = overview.Guests;
            var nodes = overview.Nodes;
            var storages = overview.Storages;

            // 키 기반 차분 갱신 — 선택/스크롤 유지 (컬렉션 재할당 금지)
            var selectedVmId = SelectedGuest?.VmId;
            var selectedNodeName = SelectedNode?.Node;

            SyncByKey(Guests,
                resources.Where(r => r.Kind is ResourceKind.Qemu or ResourceKind.Lxc).OrderBy(r => r.VmId).ToList(),
                r => r.VmId, (oldItem, fresh) => oldItem.CopyFrom(fresh));
            ApplyLiveStatusOverrides();
            // 모든 목록은 안정 정렬(동률 시 고유 키)로 고정 — 서버 응답 순서 변동에 따라 행이 뒤섞이지 않도록
            SyncByKey(Nodes,
                nodes.OrderBy(n => n.Node, StringComparer.OrdinalIgnoreCase).ToList(),
                n => n.Node, (oldItem, fresh) => oldItem.CopyFrom(fresh));
            SyncByKey(Tasks,
                tasks.OrderByDescending(t => t.StartTimeUtc).ThenBy(t => t.Upid, StringComparer.Ordinal)
                    .Take(TaskListCapacity).ToList(),
                t => t.Upid, (oldItem, fresh) => oldItem.CopyFrom(fresh));
            SyncByKey(Storages,
                storages.OrderBy(s => s.Node, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(s => s.Storage, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(s => s.Id, StringComparer.Ordinal).ToList(),
                s => s.Id, (oldItem, fresh) => oldItem.CopyFrom(fresh));

            // 템플릿을 숨긴 채 템플릿이 된 게스트는 표에 없으므로 다시 고르지 않는다
            if (selectedVmId is { } vmId && SelectedGuest?.VmId != vmId)
                SelectedGuest = Guests.FirstOrDefault(g => g.VmId == vmId && (ShowTemplates || !g.IsTemplate));

            if (selectedNodeName is { } nodeName && SelectedNode?.Node != nodeName)
                SelectedNode = Nodes.FirstOrDefault(n => n.Node == nodeName);

            // 첫 로드에는 선택이 비어 있어 노드 정보·그래프가 모두 빈 화면이 된다 — 기본 노드를 골라 준다
            SelectedNode ??= PickDefaultNode();

            // 보이는 패널의 부가 정보만 갱신 — 그래프는 GraphRefreshInterval 주기(선택·기간 변경 시엔 즉시)
            await Task.WhenAll(
                    ShowNodesPanel ? LoadNodeStatusAsync() : Task.CompletedTask,
                    ShowGuestsPanel ? LoadGuestGraphAsync(false) : Task.CompletedTask,
                    ShowNodesPanel ? LoadNodeGraphAsync(false) : Task.CompletedTask)
                .ConfigureAwait(true);
        }
        catch (CertificateTrustException ex)
        {
            // 연결 중 서버 인증서가 바뀜 — 중간자 가능성이 있으므로 조용히 재시도하지 않고 연결을 끊는다
            StatusMessage = Loc.T("MainViewModel_M06", ex.Message);
            await DisconnectCoreAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            StatusMessage = Loc.T("MainViewModel_M07", ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }
    /// <summary>
    ///     키 기반 차분 갱신 — 행 객체를 유지(선택·스크롤·컨테이너 보존)하며 삭제·삽입·이동·값 갱신만 반영한다.
    ///     정렬이 유지되는 일반적인 틱은 위치 i 의 행이 곧 대상이라 탐색 없이 O(n), 그 밖에는 사전으로 찾는다.
    /// </summary>
    private static void SyncByKey<T, TKey>(
        ObservableCollection<T> target,
        IReadOnlyList<T> fresh,
        Func<T, TKey> key,
        Action<T, T> update)
        where T : class
        where TKey : notnull
    {
        var freshKeys = new HashSet<TKey>(fresh.Count);
        for (var i = 0; i < fresh.Count; i++) freshKeys.Add(key(fresh[i]));

        for (var i = target.Count - 1; i >= 0; i--)
            if (!freshKeys.Contains(key(target[i])))
                target.RemoveAt(i);

        var existing = new Dictionary<TKey, T>(target.Count);
        foreach (var item in target) existing.TryAdd(key(item), item);

        for (var i = 0; i < fresh.Count; i++)
        {
            if (!existing.TryGetValue(key(fresh[i]), out var row))
            {
                target.Insert(i, fresh[i]);
                continue;
            }

            if (!ReferenceEquals(target[i], row))
            {
                // 앞쪽 [0, i) 는 이미 fresh 순서와 일치하므로 i 이후만 찾는다
                var from = i + 1;
                while (!ReferenceEquals(target[from], row)) from++;

                target.Move(from, i); // 행 객체 유지 — 깜빡임 없는 위치 이동
            }

            update(row, fresh[i]); // 값만 갱신(바뀐 속성만 알림)
        }
    }
    private async Task OnTimerTickAsync()
    {
        if (Api is not { } api) return;

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
                var fresh = await api.GetTaskStatusAsync(upid).ConfigureAwait(true);
                if (!ReferenceEquals(api, Api)) return;

                UpsertTask(fresh);
            }

            if (AutoRefresh && IsConnected && !_refreshing && !_backgrounded)
                await RefreshDataAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            // async void 타이머 경로 — 여기서 새면 전역 예외 창이 매 틱 쌓인다. 다음 틱에 재시도.
        }
        finally
        {
            _tickBusy = false;
        }
    }
    private async Task LoadNodeStatusAsync()
    {
        if (Api is null || SelectedNode is not { IsOnline: true } node) return;

        try
        {
            var status = await Api.GetNodeStatusAsync(node.Node).ConfigureAwait(true);
            if (ReferenceEquals(SelectedNode, node)) NodeStatus = status; // 기다리는 사이 다른 노드를 선택했으면 이전 노드 상태로 덮지 않는다
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
        }
    }
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
