using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProxmoxClient.Core.Localization;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     Async client for the Proxmox VE REST API (api2/json).
///     - Password auth: <see cref="LoginAsync" /> exchanges credentials for a ticket
///     (PVEAuthCookie) + CSRF token; every write (POST/PUT/DELETE) then carries the
///     CSRFPreventionToken header.
///     - API-token auth: no login round-trip; requests carry
///     "Authorization: PVEAPIToken={tokenId}={secret}".
///     - Optional HTTP/HTTPS/SOCKS5 proxy and self-signed certificate acceptance.
/// </summary>
public sealed partial class ProxmoxApiClient : IDisposable
{
    private const int ResponseExcerptLength = 300;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    /// <summary>요청 하나의 기본 제한 시간(응답 머리글까지). 업로드처럼 오래 걸리는 요청은 따로 넘긴다.</summary>
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    /// <summary>티켓 갱신 주기 — PVE 티켓 유효 시간(2시간)보다 충분히 앞서 갱신한다.</summary>
    private static readonly TimeSpan TicketRenewAge = TimeSpan.FromMinutes(60);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _renewGate = new(1, 1);
    private volatile AuthSession? _auth;

    /// <summary>
    ///     티켓 갱신이 인증 거부(401)로 끝났다 — 티켓과 보관한 비밀번호(OpenID 면 처음 티켓)가 모두 통하지 않는다.
    ///     이후 요청은 서버에 로그인을 다시 보내지 않고 바로 실패한다(요청마다 실패한 로그인이 쌓여 차단되지 않게).
    ///     다시 로그인하면 풀린다.
    /// </summary>
    private volatile bool _authRejected;
    private bool _disposed;
    /// <summary>Builds a client for the given profile (proxy + TLS validation are applied here).</summary>
    public ProxmoxApiClient(ConnectionProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.Host)) throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_01"));

        var handler = new HttpClientHandler
        {
            UseProxy = profile.ProxyMode != ProxyMode.None,
            // pveproxy 는 Accept-Encoding: gzip 이면 JSON 을 압축해 보낸다 — 매초 받는 자원·작업 목록 대역폭이 크게 준다
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        // 모든 인증서 허용 대신 공인 CA 검증 또는 신뢰한 지문(TOFU)만 통과
        CertificateValidator = new ServerCertificateValidator(profile);
        handler.ServerCertificateCustomValidationCallback =
            (_, certificate, _, errors) => CertificateValidator.Validate(certificate, errors);

        ConfigureProxy(handler, profile);

        _http = new HttpClient(handler)
        {
            BaseAddress = BuildBaseAddress(profile),
            // 제한 시간은 요청마다 건다(SendOnceAsync) — 대용량 업로드만 더 길게 줄 수 있게
            Timeout = Timeout.InfiniteTimeSpan
        };

        if (profile.AuthMode == AuthMode.ApiToken)
        {
            var header = $"PVEAPIToken={profile.ApiTokenId}={SecureStringHelper.ToPlainString(profile.ApiTokenSecret)}";
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);
        }
    }
    /// <summary>True once ticket auth succeeded (or immediately in token mode).</summary>
    public bool IsAuthenticated =>
        Profile.AuthMode == AuthMode.ApiToken || _auth is not null;
    /// <summary>로그인한 사용자 ID(user@realm, 서버 응답 기준) — 로그인 전이거나 API 토큰이면 null.</summary>
    public string? AuthenticatedUser { get; private set; }

    /// <summary>세션 인증 티켓(비밀번호 모드). 콘솔 웹소켓 연결에 필요.</summary>
    public string? AuthTicket => _auth?.Ticket;
    /// <summary>이 클라이언트를 만든 연결 프로필.</summary>
    public ConnectionProfile Profile { get; }
    /// <summary>서버 인증서 검증기(API·콘솔 웹소켓 공용) — 거부 시 신뢰 확인 정보를 보관한다.</summary>
    internal ServerCertificateValidator CertificateValidator { get; }
    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _http.Dispose();
        _renewGate.Dispose();
    }
    /// <summary>
    ///     Authenticates with the profile credentials. Password mode performs
    ///     POST /access/ticket and stores the ticket/CSRF token; token mode is a no-op.
    /// </summary>
    [Versioning.PveApi("POST", "/access/ticket")]
    public async Task LoginAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (Profile.AuthMode == AuthMode.ApiToken) return;

        if (string.IsNullOrWhiteSpace(Profile.UserName) || Profile.Password is null)
            throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_02"));

        await AcquireTicketAsync(SecureStringHelper.ToPlainString(Profile.Password) ?? string.Empty, ct)
            .ConfigureAwait(false);
    }
    /// <summary>
    ///     비밀번호 모드 티켓을 만료 전에 갱신한다(<paramref name="force" /> 면 나이와 무관 — 401 응답 직후).
    ///     현재 티켓으로 재발급(PVE 표준: password 자리에 티켓)하고, 실패하면 보관 중인 비밀번호로 다시 로그인한다.
    ///     동시 요청이 몰려도 갱신은 한 번만 수행된다.
    /// </summary>
    private async Task EnsureFreshTicketAsync(bool force, CancellationToken ct)
    {
        if (Profile.AuthMode != AuthMode.Password || _auth is not { } current) return;
        if (_authRejected) throw SessionExpired();

        if (!force && Environment.TickCount64 - current.IssuedAtTick < (long)TicketRenewAge.TotalMilliseconds) return;

        await _renewGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_auth, current)) return; // 기다리는 사이 다른 요청이 이미 갱신함
            if (_authRejected) throw SessionExpired();

            try
            {
                try
                {
                    await AcquireTicketAsync(current.Ticket, ct).ConfigureAwait(false);
                }
                catch (ProxmoxApiException) when (Profile.Password is not null)
                {
                    await AcquireTicketAsync(SecureStringHelper.ToPlainString(Profile.Password) ?? string.Empty, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (ProxmoxApiException ex) when (ex.StatusCode == (int)HttpStatusCode.Unauthorized)
            {
                // 연결 실패(상태 0)는 다음 요청 때 다시 해 본다 — 거부만 세션 만료로 본다
                _authRejected = true;
                throw SessionExpired();
            }
        }
        finally
        {
            _renewGate.Release();
        }
    }
    private static ProxmoxApiException SessionExpired()
    {
        return new ProxmoxApiException((int)HttpStatusCode.Unauthorized, null, Res.T("ProxmoxApiClient_SessionExpired"));
    }
    private async Task AcquireTicketAsync(string password, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["username"] = Profile.UserName,
            ["password"] = password
        };

        var data = await PostFormJsonAsync("access/ticket", form, false, ct).ConfigureAwait(false);
        var ticket = data.TryGetProperty("ticket", out var ticketEl) ? ticketEl.GetString() : null;
        var csrfToken = data.TryGetProperty("CSRFPreventionToken", out var csrfEl) ? csrfEl.GetString() : null;

        if (string.IsNullOrEmpty(ticket)) throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_03"));
        // 2단계 인증 계정 — 서버는 TFA 를 기다리는 반쪽 티켓을 준다. 받아 두면 로그인은 "성공"인데 모든 요청이 401 이다
        if (data.TryGetProperty("NeedTFA", out var needTfa) && needTfa.ValueKind switch
            {
                JsonValueKind.Number => needTfa.TryGetInt32(out var flag) && flag != 0,
                JsonValueKind.String => needTfa.GetString() is { Length: > 0 } text && text != "0",
                JsonValueKind.True => true,
                _ => false
            })
            throw new ProxmoxApiException(0, null, Res.T("ProxmoxApiClient_NeedTfa"));

        _auth = new AuthSession(ticket, csrfToken, Environment.TickCount64);
        _authRejected = false;
        // 서버가 알려 준 실제 사용자 ID — 프로필에 영역 없이 "root" 로 적어 두어도 "root@pam" 으로 온다
        AuthenticatedUser = GetString(data, "username") is { Length: > 0 } user ? user : Profile.UserName;
    }
    /// <summary>Gets the cluster version (GET /version) — handy as a connection test.</summary>
    [Versioning.PveApi("GET", "/version")]
    public async Task<PveVersion> GetVersionAsync(CancellationToken ct = default)
    {
        var data = await GetJsonAsync("version", ct).ConfigureAwait(false);
        RememberVersion(GetString(data, "version"));

        return new PveVersion
        {
            Version = GetString(data, "version"),
            Release = GetString(data, "release"),
            RepoId = GetString(data, "repoid")
        };
    }
    /// <summary>Lists all VMs and CTs in the cluster (GET /cluster/resources?type=vm).</summary>
    [Versioning.PveApi("GET", "/cluster/resources")]
    public Task<IReadOnlyList<PveResource>> GetClusterResourcesAsync(CancellationToken ct = default)
    {
        return GetListAsync<PveResource, ResourceDto>(
            "cluster/resources?type=vm",
            dto => MapResource(dto, dto.Node ?? string.Empty),
            ct);
    }
    /// <summary>Lists all storages in the cluster (GET /cluster/resources?type=storage).</summary>
    [Versioning.PveApi("GET", "/cluster/resources")]
    public Task<IReadOnlyList<PveStorage>> GetClusterStoragesAsync(CancellationToken ct = default)
    {
        return GetListAsync<PveStorage, ResourceDto>("cluster/resources?type=storage", MapStorage, ct);
    }
    /// <summary>
    ///     클러스터 전체 현황을 한 번에 조회(GET /cluster/resources) — 게스트·노드·스토리지가 한 응답에 담긴다.
    ///     값은 pvestatd 주기로 모인 것이라 실시간보다 몇 초 늦을 수 있다(전원 작업 직후엔 status/current 로 보정).
    /// </summary>
    [Versioning.PveApi("GET", "/cluster/resources")]
    [Versioning.PveApi("GET", "/storage")]
    public async Task<ClusterOverview> GetClusterOverviewAsync(CancellationToken ct = default)
    {
        using var doc = await GetDocumentAsync("cluster/resources", ct).ConfigureAwait(false);
        var data = DataElement(doc);

        var guests = new List<PveResource>();
        var nodes = new List<PveNode>();
        var storages = new List<PveStorage>();
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var item in data.EnumerateArray())
            {
                if (item.Deserialize<ResourceDto>(JsonOptions) is not { } dto) continue;

                switch (dto.Type)
                {
                    case "qemu":
                    case "lxc":
                        guests.Add(MapResource(dto, dto.Node ?? string.Empty));
                        break;
                    case "node":
                        nodes.Add(MapNode(dto));
                        break;
                    case "storage":
                        storages.Add(MapStorage(dto));
                        break;
                }
            }

        return new ClusterOverview(guests, nodes, storages);
    }
    private static PveStorage MapStorage(ResourceDto dto)
    {
        return new PveStorage
        {
            Id = dto.Id ?? $"storage/{dto.Node}/{dto.Storage}",
            Storage = dto.Storage ?? dto.Id ?? string.Empty,
            Node = dto.Node ?? string.Empty,
            PluginType = dto.PluginType ?? string.Empty,
            Content = NormalizeCsv(dto.Content),
            DiskBytes = dto.Disk ?? 0,
            MaxDiskBytes = dto.MaxDisk ?? 0,
            Status = dto.Status ?? string.Empty
        };
    }
    private static PveNode MapNode(ResourceDto dto)
    {
        return new PveNode
        {
            Node = dto.Node ?? string.Empty,
            Status = dto.Status ?? "unknown",
            CpuUsagePercent = (dto.Cpu ?? 0) * 100.0,
            CpuCount = dto.MaxCpu ?? 0,
            MemBytes = dto.Mem ?? 0,
            MaxMemBytes = dto.MaxMem ?? 0,
            DiskBytes = dto.Disk ?? 0,
            MaxDiskBytes = dto.MaxDisk ?? 0,
            UptimeSeconds = dto.Uptime ?? 0,
            Level = dto.Level
        };
    }
    /// <summary>nodes/{node}/status 의 loadavg 배열 항목(문자열 또는 숫자). 없거나 해석 불가면 0.</summary>
    private static double ParseLoadAverage(in JsonElement data, int index)
    {
        if (!TryArrayItem(data, "loadavg", index, out var item)) return 0.0;

        return item.ValueKind switch
        {
            JsonValueKind.Number => item.GetDouble(),
            JsonValueKind.String when double.TryParse(
                item.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) => value,
            _ => 0.0
        };
    }
    /// <summary>
    ///     "vztmpl,iso,backup" 처럼 서버가 매 응답마다 순서를 바꿔 주는 콤마 목록을 정렬해 표시가 흔들리지 않게 한다.
    /// </summary>
    private static string NormalizeCsv(string? csv)
    {
        return string.IsNullOrWhiteSpace(csv)
            ? string.Empty
            : string.Join(",", csv
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Order(StringComparer.OrdinalIgnoreCase));
    }
    /// <summary>
    ///     Gets the effective permission tree visible to the current user
    ///     (GET /access/permissions) and flattens it into a capability summary.
    /// </summary>
    [Versioning.PveApi("GET", "/access/permissions")]
    public async Task<PermissionsInfo> GetPermissionsSummaryAsync(CancellationToken ct = default)
    {
        var data = await GetJsonAsync("access/permissions", ct).ConfigureAwait(false);
        var privileges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var isAdmin = false;
        if (data.ValueKind != JsonValueKind.Object) return new PermissionsInfo { Privileges = privileges };

        foreach (var path in data.EnumerateObject())
        {
            if (path.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var role in path.Value.EnumerateObject())
            {
                // PVE 응답 형식: { "/vms": { "VM.PowerMgmt": 1, ... } } — 값은 propagate 플래그(권한 보유 자체는 동일)
                if (role.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    privileges.Add(role.Name);
                    continue;
                }

                if (role.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) isAdmin = true;

                if (role.Value.ValueKind != JsonValueKind.Array) continue;

                foreach (var privilege in role.Value.EnumerateArray())
                    if (privilege.ValueKind is JsonValueKind.String)
                        privileges.Add(privilege.GetString() ?? string.Empty);
            }
        }

        return new PermissionsInfo { IsAdmin = isAdmin, Privileges = privileges };
    }
    /// <summary>
    ///     Gets the authentication realms (login types) for the login screen
    ///     (GET /access/domains — explicitly allowed without authentication by
    ///     pve-proxy). Returns an empty list on failure so callers can fall back
    ///     to built-in defaults.
    /// </summary>
    [Versioning.PveApi("GET", "/access/domains")]
    public async Task<IReadOnlyList<PveAuthDomain>> GetAuthDomainsAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await GetJsonAsync("access/domains", ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Array) return [];

            var list = new List<PveAuthDomain>();
            foreach (var item in data.EnumerateArray())
            {
                var realm = GetString(item, "realm");
                if (string.IsNullOrEmpty(realm)) continue;

                list.Add(new PveAuthDomain
                {
                    Realm = realm,
                    Type = GetString(item, "type"),
                    Comment = GetString(item, "comment"),
                    RequiresTfa = item.TryGetProperty("tfa", out _),
                    IsDefault = item.TryGetProperty("default", out var def)
                                && def.ValueKind is JsonValueKind.Number or JsonValueKind.String
                                && def.ToString() == "1"
                });
            }

            return list;
        }
        catch (ProxmoxApiException)
        {
            return [];
        }
    }
    private static Uri BuildBaseAddress(ConnectionProfile profile)
    {
        var builder = new UriBuilder(Uri.UriSchemeHttps, profile.Host, profile.Port)
        {
            Path = "/api2/json/"
        };
        return builder.Uri;
    }
    private static void ConfigureProxy(HttpClientHandler handler, ConnectionProfile profile)
    {
        if (profile.ProxyMode == ProxyMode.None)
        {
            handler.UseProxy = false; // 직접 연결 — 시스템 프록시도 사용하지 않음
            return;
        }

        if (profile.ProxyMode == ProxyMode.System)
        {
            handler.UseProxy = true; // 시스템(운영체제) 프록시 설정 사용
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.ProxyHost) || profile.ProxyPort is not > 0) return;

        var scheme = profile.ProxyMode switch
        {
            ProxyMode.Http => "http",
            ProxyMode.Https => "https",
            ProxyMode.Socks5 => "socks5",
            _ => "http"
        };

        var proxy = new WebProxy($"{scheme}://{profile.ProxyHost}:{profile.ProxyPort!.Value}");
        if (!string.IsNullOrEmpty(profile.ProxyUserName))
            proxy.Credentials = new NetworkCredential(profile.ProxyUserName, profile.ProxyPassword ?? string.Empty);

        handler.Proxy = proxy;
    }
    private static string Escape(string segment)
    {
        return Uri.EscapeDataString(segment);
    }
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    private static string Excerpt(string body)
    {
        return body.Length <= ResponseExcerptLength
            ? body
            : body[..ResponseExcerptLength];
    }
    /// <summary>GET → "data" 요소(복제본). 1초 주기 목록 조회는 복제 없이 문서에서 바로 매핑하는 <see cref="GetListAsync{T,TDto}" /> 사용.</summary>
    private async Task<JsonElement> GetJsonAsync(string relative, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var doc = await GetDocumentAsync(relative, ct, timeout).ConfigureAwait(false);
        return DataElement(doc).Clone(); // Clone() survives doc disposal.
    }
    private async Task<JsonDocument> GetDocumentAsync(string relative, CancellationToken ct,
        TimeSpan? timeout = null)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, relative), true, ct,
                timeout)
            .ConfigureAwait(false);
        return await ReadJsonDocumentAsync(response, ct).ConfigureAwait(false);
    }
    /// <summary>
    ///     POSTs a form and returns the "data" element (login path: requireAuth=false, no cookie/renewal).
    /// </summary>
    private async Task<JsonElement> PostFormJsonAsync(
        string relative,
        IReadOnlyDictionary<string, string> form,
        bool requireAuth,
        CancellationToken ct)
    {
        using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, relative) { Content = new FormUrlEncodedContent(form) },
                requireAuth,
                ct)
            .ConfigureAwait(false);
        using var doc = await ReadJsonDocumentAsync(response, ct).ConfigureAwait(false);
        return DataElement(doc).Clone();
    }
    private Task<string> PostWriteAsync(string relative, IReadOnlyDictionary<string, string>? form,
        CancellationToken ct)
    {
        return SendWriteAsync(HttpMethod.Post, relative, form, ct);
    }
    private Task<string> DeleteWriteAsync(string relative, CancellationToken ct)
    {
        return SendWriteAsync(HttpMethod.Delete, relative, null, ct);
    }
    /// <summary>Performs a write request (POST/DELETE) with the CSRF header; returns "data" as string (UPID).</summary>
    private async Task<string> SendWriteAsync(
        HttpMethod method,
        string relative,
        IReadOnlyDictionary<string, string>? form,
        CancellationToken ct)
    {
        ThrowIfDisposed();

        if (Profile.AuthMode == AuthMode.Password && _auth is null)
            throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_08"));

        using var response = await SendAsync(
                () => new HttpRequestMessage(method, relative)
                {
                    Content = form is null ? null : new FormUrlEncodedContent(form)
                },
                true,
                ct)
            .ConfigureAwait(false);
        using var doc = await ReadJsonDocumentAsync(response, ct).ConfigureAwait(false);
        var data = DataElement(doc);
        return data.ValueKind == JsonValueKind.String ? data.GetString() ?? string.Empty : string.Empty;
    }
    /// <summary>
    ///     인증 헤더를 요청마다 붙여 전송한다(공유 DefaultRequestHeaders 를 바꾸지 않아 동시 요청과 경합 없음).
    ///     비밀번호 모드는 만료 전에 티켓을 갱신하고, 401 이면 한 번 갱신 후 재시도한다(요청은 재전송 불가라 팩토리로 생성).
    ///     연결 실패·타임아웃은 <see cref="ProxmoxApiException" /> 으로 감싸 호출자가 한 종류만 처리하면 되게 한다.
    ///     응답은 헤더까지만 받고 본문은 스트림으로 읽는다(문자열 전체 버퍼링 없음) — 호출자가 Dispose 해야 한다.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, bool requireAuth,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        ThrowIfDisposed();
        if (requireAuth) await EnsureFreshTicketAsync(false, ct).ConfigureAwait(false);

        var response = await SendOnceAsync(createRequest, requireAuth, ct, timeout).ConfigureAwait(false);
        if (!requireAuth || response.StatusCode != HttpStatusCode.Unauthorized || _auth is null) return response;

        response.Dispose();
        await EnsureFreshTicketAsync(true, ct).ConfigureAwait(false);
        return await SendOnceAsync(createRequest, requireAuth, ct, timeout).ConfigureAwait(false);
    }
    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> createRequest, bool requireAuth,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        using var request = createRequest();
        ApplyAuthHeaders(request, requireAuth);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? DefaultRequestTimeout);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // TLS 인증 실패가 인증서 미신뢰·지문 불일치 때문이면 호출자가 사용자에게 확인을 요청할 수 있게 구분한다
            if (CertificateValidator.TryCreateTrustException(ex) is { } trust) throw trust;

            throw new ProxmoxApiException(0, null, Res.T("ProxmoxApiClient_09", ex.Message));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProxmoxApiException(0, null, Res.T("ProxmoxApiClient_10"));
        }
    }
    /// <summary>티켓 쿠키(모든 인증 요청)와 CSRF 헤더(쓰기 요청)를 요청 단위로 붙인다.</summary>
    private void ApplyAuthHeaders(HttpRequestMessage request, bool requireAuth)
    {
        if (!requireAuth || Profile.AuthMode != AuthMode.Password || _auth is not { } auth) return;

        request.Headers.TryAddWithoutValidation("Cookie", $"PVEAuthCookie={auth.Ticket}");
        if (request.Method != HttpMethod.Get && !string.IsNullOrEmpty(auth.CsrfToken))
            request.Headers.TryAddWithoutValidation("CSRFPreventionToken", auth.CsrfToken);
    }
    /// <summary>성공 응답 본문을 스트림에서 바로 파싱; 실패(non-2xx)는 본문 발췌와 함께 <see cref="ProxmoxApiException" />.</summary>
    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new ProxmoxApiException((int)response.StatusCode, Excerpt(body));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ProxmoxApiException((int)response.StatusCode, null, Res.T("ProxmoxApiClient_11", ex.Message));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ProxmoxApiException((int)response.StatusCode, null, Res.T("ProxmoxApiClient_12", ex.Message));
        }
    }
    /// <summary>{"data": ...} 봉투의 data, 봉투가 없으면 루트.</summary>
    private static JsonElement DataElement(JsonDocument doc)
    {
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;
    }
    /// <summary>목록 응답을 문서에서 바로 DTO → 모델로 매핑(응답 문자열·JsonElement 복제·중간 리스트 없음).</summary>
    private async Task<IReadOnlyList<T>> GetListAsync<T, TDto>(
        string relative,
        Func<TDto, T> map,
        CancellationToken ct)
    {
        using var doc = await GetDocumentAsync(relative, ct).ConfigureAwait(false);
        var data = DataElement(doc);
        switch (data.ValueKind)
        {
            case JsonValueKind.Array:
                var list = new List<T>(data.GetArrayLength());
                foreach (var item in data.EnumerateArray())
                    if (item.Deserialize<TDto>(JsonOptions) is { } dto)
                        list.Add(map(dto));

                return list;

            case JsonValueKind.Object:
                return data.Deserialize<TDto>(JsonOptions) is { } single ? [map(single)] : [];

            default:
                return [];
        }
    }
    private static PveResource MapResource(ResourceDto dto, string node)
    {
        var kind = string.Equals(dto.Type, "lxc", StringComparison.OrdinalIgnoreCase)
            ? ResourceKind.Lxc
            : ResourceKind.Qemu;
        var vmid = dto.VmId ?? 0;

        return new PveResource
        {
            Id = dto.Id ?? $"{kind.ApiSegment()}/{vmid}",
            Kind = kind,
            VmId = vmid,
            Name = !string.IsNullOrWhiteSpace(dto.Name) ? dto.Name : $"{kind.Label()}-{vmid}",
            Node = node,
            Status = dto.Status ?? string.Empty,
            CpuUsagePercent = (dto.Cpu ?? 0) * 100.0,
            CpuCount = dto.MaxCpu ?? dto.Cpus ?? 0,
            MemBytes = dto.Mem ?? 0,
            MaxMemBytes = dto.MaxMem ?? 0,
            DiskBytes = dto.Disk ?? 0,
            MaxDiskBytes = dto.MaxDisk ?? 0,
            NetInBytes = dto.NetIn ?? 0,
            NetOutBytes = dto.NetOut ?? 0,
            UptimeSeconds = dto.Uptime ?? 0,
            IsTemplate = (dto.Template ?? 0) != 0,
            Tags = (dto.Tags ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.TrimStart(';'))
                .ToArray(),
            Pool = string.IsNullOrEmpty(dto.Pool) ? null : dto.Pool
        };
    }
    /// <summary>
    ///     Parses "UPID:{node}:{pid}:{pstart}:{starttime}:{type}:{id}:{user}:" — pid·pstart·starttime 은 16진수.
    ///     Unknown shapes yield an empty task instead of throwing.
    /// </summary>
    internal static PveTask ParseUpid(string upid)
    {
        var parts = upid.Split(':');
        if (parts.Length < 8 || parts[0] != "UPID") return new PveTask { Upid = upid, Status = string.Empty };

        var startTime = long.TryParse(parts[4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                            out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            : default;

        return new PveTask
        {
            Upid = upid,
            Node = parts[1],
            Type = parts[5],
            Id = parts[6],
            User = parts[7],
            StartTimeUtc = startTime
        };
    }
    // Used for payloads with nested objects (node status) where DTO mapping is clumsy.
    // Proxmox may send numbers as strings, so both kinds are accepted.
    private static string GetString(in JsonElement obj, string name)
    {
        return obj.ValueKind == JsonValueKind.Object
               && obj.TryGetProperty(name, out var v)
               && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
    }
    private static long GetLong(in JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return 0;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (long)v.GetDouble(),
            JsonValueKind.String when long.TryParse(v.GetString(), out var l) => l,
            _ => 0
        };
    }
    private static int GetInt(in JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return 0;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : (int)v.GetDouble(),
            JsonValueKind.String when int.TryParse(v.GetString(), out var i) => i,
            _ => 0
        };
    }
    private static double GetDouble(in JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return 0;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(
                v.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var d) => d,
            _ => 0
        };
    }
    private static bool TryObject(in JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Object)
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }
    private static bool TryArrayItem(in JsonElement obj, string name, int index, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Array
            && v.GetArrayLength() > index)
        {
            value = v[index];
            return true;
        }

        value = default;
        return false;
    }
    /// <summary>비밀번호 모드 인증 상태 — 티켓·CSRF 토큰을 한 객체로 원자적으로 교체해 동시 요청이 짝이 어긋난 값을 읽지 않게 한다.</summary>
    private sealed record AuthSession(string Ticket, string? CsrfToken, long IssuedAtTick);
    private sealed class ResourceDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("vmid")] public int? VmId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("node")] public string? Node { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("cpu")] public double? Cpu { get; set; }
        [JsonPropertyName("maxcpu")] public int? MaxCpu { get; set; }
        [JsonPropertyName("cpus")] public int? Cpus { get; set; }
        [JsonPropertyName("mem")] public long? Mem { get; set; }
        [JsonPropertyName("maxmem")] public long? MaxMem { get; set; }
        [JsonPropertyName("disk")] public long? Disk { get; set; }
        [JsonPropertyName("maxdisk")] public long? MaxDisk { get; set; }
        [JsonPropertyName("netin")] public long? NetIn { get; set; }
        [JsonPropertyName("netout")] public long? NetOut { get; set; }
        [JsonPropertyName("uptime")] public long? Uptime { get; set; }
        [JsonPropertyName("template")] public int? Template { get; set; }
        [JsonPropertyName("tags")] public string? Tags { get; set; }
        [JsonPropertyName("pool")] public string? Pool { get; set; }
        [JsonPropertyName("storage")] public string? Storage { get; set; }
        [JsonPropertyName("plugintype")] public string? PluginType { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("level")] public string? Level { get; set; } // 노드 항목의 구독 수준
    }
    private sealed class NodeDto
    {
        [JsonPropertyName("node")] public string? Node { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("cpu")] public double? Cpu { get; set; }
        [JsonPropertyName("maxcpu")] public int? MaxCpu { get; set; }
        [JsonPropertyName("mem")] public long? Mem { get; set; }
        [JsonPropertyName("maxmem")] public long? MaxMem { get; set; }
        [JsonPropertyName("disk")] public long? Disk { get; set; }
        [JsonPropertyName("maxdisk")] public long? MaxDisk { get; set; }
        [JsonPropertyName("uptime")] public long? Uptime { get; set; }
        [JsonPropertyName("level")] public string? Level { get; set; }
    }
}
