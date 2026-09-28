using ProxmoxClient.Core.Api.Domains;

namespace ProxmoxClient.Core.Api;

/// <summary>영역별 API 묶음 — 화면 코드는 경로 대신 여기의 메서드를 부른다.</summary>
public sealed partial class ProxmoxApiClient
{
    private AccessApi? _access;
    private AcmeApi? _acme;
    private CephApi? _ceph;
    private ClusterApi? _cluster;
    private DisksApi? _disks;
    private FirewallApi? _firewall;
    private GuestsApi? _guests;
    private HaApi? _ha;
    private JobsApi? _jobs;
    private MappingsApi? _mappings;
    private MetricsApi? _metrics;
    private NodesApi? _nodes;
    private NotificationsApi? _notifications;
    private PoolsApi? _pools;
    private RealmsApi? _realms;
    private SdnApi? _sdn;
    private SdnRoutingApi? _sdnRouting;
    private GuestAgentApi? _agent;
    private StorageApi? _storage;
    private TasksApi? _tasks;
    private TfaApi? _tfa;
    private UsersApi? _users;

    public AccessApi Access => _access ??= new AccessApi(this);

    public AcmeApi Acme => _acme ??= new AcmeApi(this);

    public CephApi Ceph => _ceph ??= new CephApi(this);

    public ClusterApi Cluster => _cluster ??= new ClusterApi(this);

    public DisksApi Disks => _disks ??= new DisksApi(this);

    public FirewallApi Firewall => _firewall ??= new FirewallApi(this);

    public GuestsApi Guests => _guests ??= new GuestsApi(this);

    public HaApi Ha => _ha ??= new HaApi(this);

    public JobsApi Jobs => _jobs ??= new JobsApi(this);

    public MappingsApi Mappings => _mappings ??= new MappingsApi(this);

    public MetricsApi Metrics => _metrics ??= new MetricsApi(this);

    public NodesApi Nodes => _nodes ??= new NodesApi(this);

    public NotificationsApi Notifications => _notifications ??= new NotificationsApi(this);

    public PoolsApi Pools => _pools ??= new PoolsApi(this);

    public RealmsApi Realms => _realms ??= new RealmsApi(this);

    public SdnApi Sdn => _sdn ??= new SdnApi(this);

    /// <summary>SDN 라우팅 정책(9.1+) — 접두사 목록·라우트 맵.</summary>
    public SdnRoutingApi SdnRouting => _sdnRouting ??= new SdnRoutingApi(this);

    /// <summary>게스트 에이전트로 게스트 안 다루기 — 명령·파일·동결.</summary>
    public GuestAgentApi Agent => _agent ??= new GuestAgentApi(this);

    public StorageApi Storage => _storage ??= new StorageApi(this);

    public TasksApi Tasks => _tasks ??= new TasksApi(this);

    public TfaApi Tfa => _tfa ??= new TfaApi(this);

    public UsersApi Users => _users ??= new UsersApi(this);
}
