using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.Core.Tests;

/// <summary>
///     PVE 응답 해석 — 같은 프로세스의 작은 HTTPS 서버가 tools/FakePve 와 같은 모양의 JSON 을 돌려주고,
///     ProxmoxApiClient 가 그것을 모델로 옮기는지 본다(자체 서명 인증서는 지문 고정으로 신뢰).
/// </summary>
public sealed class ApiParsingTests : IAsyncLifetime
{
    private const long Now = 1_760_000_000;
    private const string Upid = "UPID:pve1:0000A1B2:0012C3D4:68E5A6C0:qmstart:100:root@pam:";

    private static readonly Dictionary<string, string> Routes = new()
    {
        ["/api2/json/version"] = """{"data":{"version":"9.0.10","release":"9.0","repoid":"abcdef"}}""",
        ["/api2/json/cluster/resources"] = $$"""
            {"data":[
             {"id":"qemu/100","type":"qemu","vmid":100,"name":"web-01","node":"pve1","status":"running","cpu":0.123,"maxcpu":4,"mem":2400000000,"maxmem":4294967296,"disk":0,"maxdisk":34359738368,"netin":12345678900,"netout":2345678900,"uptime":277200,"template":0,"tags":"web"},
             {"id":"lxc/200","type":"lxc","vmid":200,"name":"dns","node":"pve2","status":"running","cpu":0.01,"maxcpu":1,"mem":60000000,"maxmem":536870912,"disk":1200000000,"maxdisk":8589934592,"netin":100000,"netout":200000,"uptime":108000,"template":0,"pool":"infra"},
             {"id":"qemu/9000","type":"qemu","vmid":9000,"name":"","node":"pve1","status":"stopped","cpu":0,"maxcpu":2,"mem":0,"maxmem":2147483648,"template":1},
             {"id":"node/pve1","type":"node","node":"pve1","status":"online","cpu":0.18,"maxcpu":16,"mem":40000000000,"maxmem":68719476736,"disk":20000000000,"maxdisk":100000000000,"uptime":1036800,"level":""},
             {"id":"storage/pve1/local","type":"storage","storage":"local","node":"pve1","plugintype":"dir","content":"iso,vztmpl,backup","disk":40000000000,"maxdisk":100000000000,"status":"available"},
             {"id":"sdn/pve1/localnetwork","type":"sdn","sdn":"localnetwork","node":"pve1","status":"ok"}
            ]}
            """,
        ["/api2/json/cluster/resources?type=vm"] = """
            {"data":[{"id":"qemu/101","type":"qemu","vmid":"101","name":"db-01","node":"pve1","status":"running","cpu":"0.47","maxcpu":8,"mem":12000000000,"maxmem":17179869184,"uptime":7200}]}
            """,
        ["/api2/json/cluster/tasks"] = $$"""
            {"data":[
             {"upid":"{{Upid}}","node":"pve1","type":"qmstart","id":"100","user":"root@pam","starttime":{{Now}},"endtime":{{Now + 20}},"status":"OK"},
             {"upid":"UPID:pve1:00000001:00000002:68E5A6C0:qmshutdown:101:root@pam:","node":"pve1","type":"qmshutdown","id":"101","user":"root@pam","starttime":{{Now}},"endtime":{{Now + 60}},"status":"ERROR: VM quit/powerdown failed - got timeout"},
             {"upid":"UPID:pve2:00000003:00000004:68E5A6C0:vzdump:200:backup@pve:","starttime":{{Now}}}
            ]}
            """,
        [$"/api2/json/nodes/pve1/tasks/{Upid}/status"] = $$"""
            {"data":{"status":"stopped","exitstatus":"OK","upid":"{{Upid}}","starttime":{{Now}},"endtime":{{Now + 20}} } }
            """,
        ["/api2/json/nodes/pve1/tasks/UPID:pve1:1:2:68E5A6C0:qmstart:100:root@pam:/status"] = $$"""
            {"data":{"status":"running","upid":"x","starttime":{{Now}} } }
            """,
        ["/api2/json/nodes/pve1/qemu/100/status/current"] = """{"data":{"status":"running","qmpstatus":"paused"}}""",
        ["/api2/json/nodes/pve2/lxc/200/status/current"] = """{"data":{"status":"stopped"}}"""
    };

    private TinyHttpsServer _server = null!;
    private ProxmoxApiClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = TinyHttpsServer.Start(Routes);
        await Task.CompletedTask;
        _client = new ProxmoxApiClient(new ConnectionProfile
        {
            Name = "test", Host = "127.0.0.1", Port = _server.Port,
            AuthMode = AuthMode.ApiToken, ApiTokenId = "root@pam!test",
            ApiTokenSecret = SecureStringHelper.FromString("secret"),
            AcceptSelfSignedCertificate = true,
            CertificateThumbprint = _server.Thumbprint
        });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _server.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Version()
    {
        var version = await _client.GetVersionAsync();
        Assert.Equal("9.0.10", version.Version);
        Assert.Equal("9.0", version.Release);
        Assert.Equal("abcdef", version.RepoId);
    }

    [Fact]
    public async Task ClusterOverviewSplitsByType()
    {
        var overview = await _client.GetClusterOverviewAsync();

        Assert.Equal(3, overview.Guests.Count); // sdn 등 모르는 종류는 버린다
        var vm = overview.Guests.Single(g => g.VmId == 100);
        Assert.Equal(ResourceKind.Qemu, vm.Kind);
        Assert.Equal("qemu/100", vm.Id);
        Assert.Equal("web-01", vm.Name);
        Assert.Equal("pve1", vm.Node);
        Assert.True(vm.IsRunning);
        Assert.Equal(12.3, vm.CpuUsagePercent, 3); // 비율(0.123) → 퍼센트
        Assert.Equal(4, vm.CpuCount);
        Assert.Equal(2_400_000_000, vm.MemBytes);
        Assert.Equal(4_294_967_296, vm.MaxMemBytes);
        Assert.Equal(12_345_678_900, vm.NetInBytes);
        Assert.Equal(277_200, vm.UptimeSeconds);
        Assert.False(vm.IsTemplate);
        Assert.Equal(["web"], vm.Tags);
        Assert.Null(vm.Pool);

        var ct = overview.Guests.Single(g => g.VmId == 200);
        Assert.Equal(ResourceKind.Lxc, ct.Kind);
        Assert.Equal("infra", ct.Pool);
        Assert.Empty(ct.Tags);

        var template = overview.Guests.Single(g => g.VmId == 9000);
        Assert.True(template.IsTemplate);
        Assert.False(string.IsNullOrWhiteSpace(template.Name)); // 이름이 없으면 종류-번호로 채운다
        Assert.Contains("9000", template.Name);

        var node = Assert.Single(overview.Nodes);
        Assert.Equal("pve1", node.Node);
        Assert.True(node.IsOnline);
        Assert.Equal(16, node.CpuCount);
        Assert.Equal(18.0, node.CpuUsagePercent, 3);
        Assert.Equal(68_719_476_736, node.MaxMemBytes);

        var storage = Assert.Single(overview.Storages);
        Assert.Equal("local", storage.Storage);
        Assert.Equal("dir", storage.PluginType);
        Assert.Equal(40.0, storage.UsagePercent, 3);
        Assert.Contains("iso", storage.Content);
        Assert.Contains("backup", storage.Content);
    }

    [Fact]
    public async Task ResourcesAcceptNumbersAsStrings()
    {
        // PVE 는 숫자를 문자열로 보내기도 한다
        var guest = Assert.Single(await _client.GetClusterResourcesAsync());
        Assert.Equal(101, guest.VmId);
        Assert.Equal(47.0, guest.CpuUsagePercent, 3);
    }

    [Fact]
    public async Task ClusterTasks()
    {
        var tasks = await _client.GetClusterTasksAsync();
        Assert.Equal(3, tasks.Count);

        var ok = tasks.Single(t => t.Upid == Upid);
        Assert.True(ok.IsOk);
        Assert.Equal("qmstart", ok.Type);
        Assert.Equal("100", ok.Id);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Now).UtcDateTime, ok.StartTimeUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Now + 20).UtcDateTime, ok.EndTimeUtc);

        var failed = tasks.Single(t => t.Type == "qmshutdown");
        Assert.True(failed.IsFailed);
        Assert.StartsWith("ERROR", failed.Status);

        // 상태·노드가 빠진 항목은 실행 중으로, 나머지는 UPID 에서 채운다
        var running = tasks.Single(t => t.Upid.Contains("vzdump"));
        Assert.True(running.IsRunning);
        Assert.Equal("pve2", running.Node);
        Assert.Equal("vzdump", running.Type);
        Assert.Equal("200", running.Id);
        Assert.Equal("backup@pve", running.User);
        Assert.Null(running.EndTimeUtc);
    }

    [Fact]
    public async Task TaskStatus()
    {
        var done = await _client.GetTaskStatusAsync(Upid);
        Assert.True(done.IsOk);
        Assert.Equal("pve1", done.Node);
        Assert.Equal("qmstart", done.Type);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Now + 20).UtcDateTime, done.EndTimeUtc);

        var running = await _client.GetTaskStatusAsync("UPID:pve1:1:2:68E5A6C0:qmstart:100:root@pam:");
        Assert.True(running.IsRunning);
        Assert.Null(running.EndTimeUtc);
    }

    [Fact]
    public async Task GuestCurrentStatus()
    {
        // 일시정지 VM 은 status=running·qmpstatus=paused → "paused"
        Assert.Equal("paused", await _client.GetGuestCurrentStatusAsync("pve1", ResourceKind.Qemu, 100));
        Assert.Equal("stopped", await _client.GetGuestCurrentStatusAsync("pve2", ResourceKind.Lxc, 200));
    }

    [Fact]
    public async Task UnknownPathFails()
    {
        await Assert.ThrowsAsync<ProxmoxApiException>(() => _client.GetGuestCurrentStatusAsync("nope", ResourceKind.Qemu, 1));
    }

    /// <summary>
    ///     요청 하나에 응답 하나(Connection: close)만 하는 최소 HTTPS 서버. 경로(+쿼리, %-해제)로 응답을 고르고,
    ///     없으면 500 을 돌려준다. 포트는 임시 포트(0)라 다른 검사와 겹치지 않는다.
    /// </summary>
    private sealed class TinyHttpsServer : IDisposable
    {
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _cts = new();
        private readonly TcpListener _listener;
        private readonly IReadOnlyDictionary<string, string> _routes;

        private TinyHttpsServer(IReadOnlyDictionary<string, string> routes)
        {
            _routes = routes;
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=pve1.local", rsa, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            // Windows 의 SslStream 은 임시 키를 못 쓰므로 PFX 로 한 번 내보냈다 읽는다
            _certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
            _listener = new TcpListener(IPAddress.Loopback, 0);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Thumbprint => ServerCertificateValidator.ComputeThumbprint(_certificate);

        public static TinyHttpsServer Start(IReadOnlyDictionary<string, string> routes)
        {
            var server = new TinyHttpsServer(routes);
            server._listener.Start();
            _ = server.AcceptLoopAsync();
            return server;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _certificate.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                                               or SocketException)
                {
                    return;
                }

                _ = ServeAsync(tcp);
            }
        }

        private async Task ServeAsync(TcpClient tcp)
        {
            using (tcp)
            {
                try
                {
                    await using var ssl = new SslStream(tcp.GetStream());
                    await ssl.AuthenticateAsServerAsync(_certificate);
                    var head = await ReadHeadAsync(ssl);
                    var target = Uri.UnescapeDataString(head.Split(' ')[1]);
                    var found = _routes.TryGetValue(target, out var body);
                    body ??= """{"data":null,"message":"no route"}""";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var status = found ? "200 OK" : "500 Internal Server Error";
                    var header = $"HTTP/1.1 {status}\r\nContent-Type: application/json;charset=UTF-8\r\n" +
                                 $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes(header));
                    await ssl.WriteAsync(bytes);
                    await ssl.FlushAsync();
                }
                catch (Exception ex) when (ex is IOException or AuthenticationException or ObjectDisposedException)
                {
                    // 클라이언트가 먼저 끊음 — 무시
                }
            }
        }

        private static async Task<string> ReadHeadAsync(Stream stream)
        {
            var buffer = new List<byte>();
            var one = new byte[1];
            while (await stream.ReadAsync(one) == 1)
            {
                buffer.Add(one[0]);
                if (buffer.Count >= 4 && buffer[^1] == '\n' && buffer[^2] == '\r' && buffer[^3] == '\n'
                    && buffer[^4] == '\r')
                    break;
            }

            return Encoding.ASCII.GetString(buffer.ToArray());
        }
    }
}
