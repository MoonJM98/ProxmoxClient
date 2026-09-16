using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    [ObservableProperty] private ImageSource? _guestGraph;
    [ObservableProperty] private string _guestGraphDs = "cpu";
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

    [ObservableProperty] private ImageSource? _nodeGraph;
    [ObservableProperty] private string _nodeGraphDs = "cpu";
    private long _nodeGraphLoadedAt;
    [ObservableProperty] private IReadOnlyList<GraphSeries>? _nodeGraphSeries;
    private string? _nodeGraphSignature;
    [ObservableProperty] private IReadOnlyList<DateTime?>? _nodeGraphTimes;
    private int _nodeGraphVersion;
    [ObservableProperty] private IReadOnlyList<string>? _nodeGraphXLabels;
    [ObservableProperty] private PveNodeStatus? _nodeStatus;
    [ObservableProperty] private string _nodeTimeframe = "hour";
    [ObservableProperty] private ObservableCollection<PveNode> _nodes = [];
    [ObservableProperty] private PermissionsInfo? _permissions;

    [ObservableProperty] private ObservableCollection<ConnectionProfile> _profiles = [];
    private bool _refreshing;

    /// <summary>rrddata(JSON) 미지원 서버로 확인됨 — 이후 PNG 경로만 사용(매 조회마다 실패 요청 생략). 연결마다 초기화.</summary>
    private bool _rrdDataUnsupported;

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

    partial void OnGuestGraphDsChanged(string value)
    {
        _ = LoadGuestGraphAsync();
    }

    partial void OnGuestTimeframeChanged(string value)
    {
        _ = LoadGuestGraphAsync();
    }

    partial void OnNodeGraphDsChanged(string value)
    {
        _ = LoadNodeGraphAsync();
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
            _rrdDataUnsupported = false;
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
        GuestGraph = null;
        GuestGraphSeries = null;
        NodeGraph = null;
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

            if (selectedVmId is { } vmId && SelectedGuest?.VmId != vmId)
                SelectedGuest = Guests.FirstOrDefault(g => g.VmId == vmId);

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

            if (AutoRefresh && IsConnected && !_refreshing) await RefreshDataAsync().ConfigureAwait(true);
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

    private void UpsertTask(PveTask task)
    {
        var index = -1;
        for (var i = 0; i < Tasks.Count; i++)
            if (string.Equals(Tasks[i].Upid, task.Upid, StringComparison.Ordinal))
            {
                index = i;
                break;
            }

        if (index >= 0)
        {
            Tasks[index].CopyFrom(task); // 행 교체(Replace) 대신 값 갱신 — 행 컨테이너·선택 유지
        }
        else
        {
            Tasks.Insert(0, task);
            while (Tasks.Count > TaskListCapacity) Tasks.RemoveAt(Tasks.Count - 1);
        }
    }

    private bool CanRunGuestPower()
    {
        return IsConnected && !IsBusy && SelectedGuest is { IsTemplate: false };
    }

    [RelayCommand(CanExecute = nameof(CanRunGuestPower))]
    private Task StartGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.StartGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Start"), SelectedGuest!);
    }

    [RelayCommand(CanExecute = nameof(CanRunGuestPower))]
    private Task StopGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.StopGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Stop"), SelectedGuest!);
    }

    [RelayCommand(CanExecute = nameof(CanRunGuestPower))]
    private Task ShutdownGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.ShutdownGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Shutdown"), SelectedGuest!);
    }

    [RelayCommand(CanExecute = nameof(CanRunGuestPower))]
    private Task RebootGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.RebootGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Reboot"), SelectedGuest!);
    }

    [RelayCommand(CanExecute = nameof(CanSuspendGuest))]
    private Task SuspendGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.SuspendGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Suspend"), SelectedGuest!);
    }

    private bool CanSuspendGuest()
    {
        return IsConnected && SelectedGuest is { IsRunning: true, Kind: ResourceKind.Qemu };
    }

    [RelayCommand(CanExecute = nameof(CanResumeGuest))]
    private Task ResumeGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.ResumeGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Resume"), SelectedGuest!);
    }

    private bool CanResumeGuest()
    {
        return IsConnected && SelectedGuest is { Status: "paused", Kind: ResourceKind.Qemu };
    }

    [RelayCommand(CanExecute = nameof(CanHibernateGuest))]
    private Task HibernateGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.HibernateGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Hibernate"), SelectedGuest!);
    }

    private bool CanHibernateGuest()
    {
        return IsConnected && SelectedGuest is { IsRunning: true, Kind: ResourceKind.Qemu };
    }

    /// <summary>콘솔 창 등 외부에서 특정 게스트의 전원 동작 실행(선택된 게스트와 무관). 상태상 불가능한 동작은 무시.</summary>
    public Task RunGuestPowerForAsync(PveResource guest, GuestPowerAction action)
    {
        if (Api is not { } api || !IsConnected || !GuestPowerRules.IsAvailable(action, guest))
            return Task.CompletedTask;

        Func<PveResource, Task<string>>? operation = action switch
        {
            GuestPowerAction.Start => g => api.StartGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Shutdown => g => api.ShutdownGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Stop => g => api.StopGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Reboot => g => api.RebootGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Suspend => g => api.SuspendGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Resume => g => api.ResumeGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Hibernate => g => api.HibernateGuestAsync(g.Node, g.Kind, g.VmId),
            _ => null
        };

        return operation is null
            ? Task.CompletedTask
            : RunGuestPowerAsync(operation, GuestPowerRules.Label(action), guest);
    }

    private async Task RunGuestPowerAsync(
        Func<PveResource, Task<string>> operation, string label, PveResource guest)
    {
        if (Api is null) return;

        IsBusy = true;
        try
        {
            var upid = await operation(guest).ConfigureAwait(true);
            StatusMessage = Loc.T("MainViewModel_M08", guest.Kind.Label(), guest.VmId, guest.Name, label);
            await TrackTaskAsync(upid, guest).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.T("MainViewModel_M09", label, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    ///     게스트 실시간 상태를 조회해 즉시 반영하고, 클러스터 인덱스가 따라올 때까지 우선 적용 대상으로 등록한다.
    ///     콘솔 창·상세 패널의 전원 버튼이 다음 새로고침을 기다리지 않고 바로 바뀐다.
    /// </summary>
    private async Task ApplyLiveGuestStatusAsync(PveResource guest)
    {
        if (Api is null) return;

        try
        {
            var status = await Api.GetGuestCurrentStatusAsync(guest.Node, guest.Kind, guest.VmId).ConfigureAwait(true);
            if (status.Length == 0) return;

            _liveStatusOverrides[guest.VmId] =
                (status, Environment.TickCount64 + (long)LiveStatusHoldTime.TotalMilliseconds);
            if (!string.Equals(guest.Status, status, StringComparison.OrdinalIgnoreCase)) guest.UpdateStatus(status);
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            App.Log($"[상태] {guest.Kind.Label()} {guest.VmId} 실시간 상태 조회 실패: {ex.Message}");
        }
    }

    /// <summary>새로고침 직후 호출 — 인덱스가 아직 늦은 게스트는 실시간 상태로 되돌리고, 따라왔거나 만료된 항목은 해제.</summary>
    private void ApplyLiveStatusOverrides()
    {
        if (_liveStatusOverrides.Count == 0) return;

        var now = Environment.TickCount64;
        var released = new List<int>();
        foreach (var (vmid, live) in _liveStatusOverrides)
        {
            var guest = Guests.FirstOrDefault(g => g.VmId == vmid);
            if (guest is null || now > live.ExpiresAt
                              || string.Equals(guest.Status, live.Status, StringComparison.OrdinalIgnoreCase))
            {
                released.Add(vmid);
                continue;
            }

            guest.UpdateStatus(live.Status);
        }

        foreach (var vmid in released) _liveStatusOverrides.Remove(vmid);
    }

    private async Task TrackTaskAsync(string upid, PveResource? guest = null)
    {
        if (Api is null) return;

        try
        {
            var task = await Api.GetTaskStatusAsync(upid).ConfigureAwait(true);
            UpsertTask(task);
            while (task.IsRunning)
            {
                await Task.Delay(TaskPollInterval).ConfigureAwait(true);
                if (Api is null) return;

                task = await Api.GetTaskStatusAsync(upid).ConfigureAwait(true);
                UpsertTask(task);
            }

            StatusMessage = task.IsOk
                ? Loc.T("MainViewModel_M10", task.Type, task.Id)
                : Loc.T("MainViewModel_M11", task.Type, task.Id, task.Status);

            // 전체 새로고침(진행 중이면 건너뜀)을 기다리지 않고 해당 게스트 상태부터 실시간으로 반영
            if (guest is not null) await ApplyLiveGuestStatusAsync(guest).ConfigureAwait(true);

            await RefreshDataAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            StatusMessage = Loc.T("MainViewModel_M12", ex.Message);
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

    /// <param name="force">false 면(주기 새로고침) 마지막 조회 후 <see cref="GraphRefreshInterval" /> 가 지나야 조회.</param>
    private async Task LoadGuestGraphAsync(bool force = true)
    {
        if (Api is not { } api || SelectedGuest is not { } guest)
        {
            _guestGraphVersion++;
            _guestGraphSignature = null;
            _guestGraphLoadedAt = 0;
            GuestGraph = null;
            GuestGraphSeries = null;
            return;
        }

        if (!force && !IsGraphDue(_guestGraphLoadedAt)) return;

        _guestGraphLoadedAt = Environment.TickCount64;
        var version = ++_guestGraphVersion;
        var timeframe = GuestTimeframe;
        try
        {
            // rrddata JSON → 클라이언트 차트. 미지원 서버: rrdtool PNG 폴백.
            if (!_rrdDataUnsupported)
            {
                var samples = await api.GetGuestRrdDataAsync(guest.Node, guest.Kind, guest.VmId, timeframe)
                    .ConfigureAwait(true);
                if (version != _guestGraphVersion) return; // 더 새 요청(선택·기간 변경·연결 해제)이 있음

                // 시그니처가 같으면(새 표본 없음) 시리즈 목록을 만들지 않는다
                var signature = GraphSignature($"{guest.Node}:{guest.VmId}", timeframe, samples);
                if (_guestGraphSignature != signature)
                {
                    var graph = BuildGraph(timeframe, samples);
                    _guestGraphSignature = signature;
                    GuestGraphXLabels = graph.XLabels;
                    GuestGraphTimes = graph.Times;
                    GuestGraphSeries = graph.Series;
                }

                GuestGraph = null;
                return;
            }

            var png = await api.GetGuestRrdPngAsync(guest.Node, guest.Kind, guest.VmId, timeframe, GuestGraphDs)
                .ConfigureAwait(true);
            if (version == _guestGraphVersion)
            {
                GuestGraph = LoadPng(png);
                GuestGraphSeries = null;
                _guestGraphSignature = null;
            }
        }
        catch (ProxmoxApiException ex) when (!_rrdDataUnsupported && IsRrdDataUnsupported(ex))
        {
            _rrdDataUnsupported = true;
            if (version == _guestGraphVersion)
                await LoadGuestGraphAsync().ConfigureAwait(true); // 곧바로 PNG 경로로 한 번 더(플래그로 재귀 1회 한정)
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            // 일시 오류 — 마지막 그래프를 유지하고 다음 주기에 재시도
        }
    }

    /// <param name="force">false 면(주기 새로고침) 마지막 조회 후 <see cref="GraphRefreshInterval" /> 가 지나야 조회.</param>
    private async Task LoadNodeGraphAsync(bool force = true)
    {
        if (Api is not { } api || SelectedNode is not { } node)
        {
            _nodeGraphVersion++;
            _nodeGraphSignature = null;
            _nodeGraphLoadedAt = 0;
            NodeGraph = null;
            NodeGraphSeries = null;
            return;
        }

        if (!force && !IsGraphDue(_nodeGraphLoadedAt)) return;

        _nodeGraphLoadedAt = Environment.TickCount64;
        var version = ++_nodeGraphVersion;
        var timeframe = NodeTimeframe;
        try
        {
            if (!_rrdDataUnsupported)
            {
                var samples = await api.GetNodeRrdDataAsync(node.Node, timeframe).ConfigureAwait(true);
                if (version != _nodeGraphVersion) return;

                var signature = GraphSignature(node.Node, timeframe, samples);
                if (_nodeGraphSignature != signature)
                {
                    var graph = BuildGraph(timeframe, samples);
                    _nodeGraphSignature = signature;
                    NodeGraphXLabels = graph.XLabels;
                    NodeGraphTimes = graph.Times;
                    NodeGraphSeries = graph.Series;
                }

                NodeGraph = null;
                return;
            }

            var png = await api.GetNodeRrdPngAsync(node.Node, timeframe, NodeGraphDs).ConfigureAwait(true);
            if (version == _nodeGraphVersion)
            {
                NodeGraph = LoadPng(png);
                NodeGraphSeries = null;
                _nodeGraphSignature = null;
            }
        }
        catch (ProxmoxApiException ex) when (!_rrdDataUnsupported && IsRrdDataUnsupported(ex))
        {
            _rrdDataUnsupported = true;
            if (version == _nodeGraphVersion) await LoadNodeGraphAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
        }
    }

    private static bool IsGraphDue(long loadedAt)
    {
        return Environment.TickCount64 - loadedAt >= (long)GraphRefreshInterval.TotalMilliseconds;
    }

    /// <summary>rrddata 엔드포인트가 없는 구버전 서버 응답(권한·일시 오류는 제외).</summary>
    private static bool IsRrdDataUnsupported(ProxmoxApiException ex)
    {
        return ex.StatusCode is 400 or 404 or 501;
    }

    /// <summary>새로고침·조회 경로에서 삼키고 다음 주기에 재시도할 오류(연결 끊김·타임아웃·연결 해제 중 Dispose 등).</summary>
    private static bool IsTransientError(Exception ex)
    {
        return ex is ProxmoxApiException or CertificateTrustException or HttpRequestException or IOException
            or OperationCanceledException or ObjectDisposedException;
    }

    /// <summary>표본 수·마지막 시각으로 새 데이터 여부 판단 — 같으면 시리즈를 다시 만들지 않는다.</summary>
    private static string GraphSignature(string ownerKey, string timeframe, IReadOnlyList<RrdSample> samples)
    {
        return $"{ownerKey}:{timeframe}:{samples.Count}:{(samples.Count > 0 ? samples[^1].TimeUnix : 0)}";
    }

    /// <summary>
    ///     rrddata 표본 → 통합 그래프(CPU·메모리 = 왼쪽 % 축, 네트워크·디스크 IO = 오른쪽 초당 바이트 축).
    ///     값이 없는 시리즈(예: 노드의 디스크 IO)는 그래프 컨트롤이 범례에서 제외한다.
    /// </summary>
    private static GraphData BuildGraph(string timeframe, IReadOnlyList<RrdSample> samples)
    {
        IReadOnlyList<GraphSeries> series =
        [
            new("CPU",
                samples.Select(s => s.Cpu is { } cpu ? 100.0 * cpu : (double?)null).ToList(),
                CpuSeriesColor, GraphAxis.Left, GraphValueUnit.Percent),
            new(Loc.T("MainWindow_19"),
                samples.Select(s =>
                        s.Mem is { } mem && s.MaxMem is { } maxMem && maxMem > 0 ? 100.0 * mem / maxMem : (double?)null)
                    .ToList(),
                MemorySeriesColor, GraphAxis.Left, GraphValueUnit.Percent),
            new(Loc.T("Graph_Network"),
                samples.Select(s => SumOrNull(s.NetIn, s.NetOut)).ToList(),
                NetworkSeriesColor, GraphAxis.Right, GraphValueUnit.BytesPerSecond),
            new(Loc.T("Graph_DiskIo"),
                samples.Select(s => SumOrNull(s.DiskRead, s.DiskWrite)).ToList(),
                DiskIoSeriesColor, GraphAxis.Right, GraphValueUnit.BytesPerSecond)
        ];

        IReadOnlyList<DateTime?> pointTimes = samples
            .Select(s =>
                s.TimeUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.TimeUnix).LocalDateTime : (DateTime?)null)
            .ToList();

        var times = samples.Select(s => s.TimeUnix).Where(t => t > 0).ToList();
        IReadOnlyList<string> xLabels = times.Count >= 2
            ?
            [
                FormatTimeAxis(times[0], timeframe),
                FormatTimeAxis(times[times.Count / 2], timeframe),
                FormatTimeAxis(times[^1], timeframe)
            ]
            : [];

        return new GraphData(series, xLabels, pointTimes);
    }

    /// <summary>한쪽만 값이 있어도 합계 표시(null + 값 = null 로 선이 끊기던 문제 방지). 둘 다 없으면 null.</summary>
    private static double? SumOrNull(double? first, double? second)
    {
        return first is null && second is null ? null : (first ?? 0) + (second ?? 0);
    }

    private static string FormatTimeAxis(long unixSeconds, string timeframe)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        return timeframe switch
        {
            "hour" or "day" => time.ToString("HH:mm"),
            "year" => time.ToString("yy/MM"),
            _ => time.ToString("MM/dd")
        };
    }

    private static ImageSource? LoadPng(byte[] png)
    {
        if (png.Length == 0) return null;

        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private bool CanVpnConnect()
    {
        return SelectedProfile is { UseVpn: true } or { VpnConfigPath: not null } &&
               _vpn.State is not (VpnState.Connected or VpnState.Connecting or VpnState.Reconnecting
                   or VpnState.Disconnecting) &&
               !IsBusy;
    }

    [RelayCommand(CanExecute = nameof(CanVpnConnect))]
    private async Task VpnConnectAsync()
    {
        if (SelectedProfile is not { } profile) return;

        IsBusy = true;
        try
        {
            await _vpn.ConnectAsync(profile.VpnConfigPath!, profile.VpnExePath).ConfigureAwait(true);
            StatusMessage = Loc.T("MainViewModel_M13");
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.T("MainViewModel_M14", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanVpnDisconnect()
    {
        return _vpn.State is VpnState.Connected or VpnState.Connecting or VpnState.Reconnecting or VpnState.Error &&
               !IsBusy;
    }

    [RelayCommand(CanExecute = nameof(CanVpnDisconnect))]
    private async Task VpnDisconnectAsync()
    {
        IsBusy = true;
        try
        {
            await _vpn.DisconnectAsync().ConfigureAwait(true);
            StatusMessage = Loc.T("MainViewModel_M15");
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.T("MainViewModel_M16", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void HandleVpnStateChanged(VpnState state)
    {
        RunOnUi(() =>
        {
            VpnState = state;
            VpnVirtualIp = _vpn.VirtualIp;
            VpnStatusText = DescribeVpnState(state, _vpn.LastError);
            VpnConnectCommand.NotifyCanExecuteChanged();
            VpnDisconnectCommand.NotifyCanExecuteChanged();
        });
    }

    private static string DescribeVpnState(VpnState state, string? lastError)
    {
        return state switch
        {
            VpnState.Disconnected => Loc.T("Vpn_NotConnected"),
            VpnState.Connecting => Loc.T("ConsoleWindow_06"),
            VpnState.Connected => Loc.T("ConsoleWindow_M08"),
            VpnState.Reconnecting => Loc.T("Vpn_Reconnecting"),
            VpnState.Disconnecting => Loc.T("Vpn_Disconnecting"),
            VpnState.Error => Loc.T("Vpn_Error", lastError ?? Loc.T("Vpn_UnknownError")),
            _ => state.ToString()
        };
    }

    private void OnVpnLogReceived(string line)
    {
        RunOnUi(() =>
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            VpnLogText = VpnLogText.Length + line.Length > VpnLogCapacityChars
                ? $"[{stamp}] {line}\n"
                : VpnLogText + $"[{stamp}] {line}\n";
        });
    }

    private void OnVpnStatsUpdated(long bytesIn, long bytesOut)
    {
        RunOnUi(() =>
        {
            VpnBytesIn = bytesIn;
            VpnBytesOut = bytesOut;
        });
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private sealed record GraphData(
        IReadOnlyList<GraphSeries> Series,
        IReadOnlyList<string> XLabels,
        IReadOnlyList<DateTime?> Times);
}