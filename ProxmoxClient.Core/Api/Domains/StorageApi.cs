using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     저장소 — 데이터센터 설정(storage)과 노드별 상태·내용(nodes/{node}/storage). 설정 파라미터는 버전마다 늘었다:
///     fs-name·keyring·preallocation·data-pool·max-protected-backups(7.1), content-dirs(7.4),
///     create-base-path·create-subdirs(8.0), skip-cert-verification(8.1),
///     snapshot-as-volume-chain·zfs-base-path·saferemove-stepsize(9.0). 서버가 모르는 칸은 보내지 않는다.
/// </summary>
public sealed class StorageApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private static string S(string node, string storage) => $"nodes/{Seg(node)}/storage/{Seg(storage)}";

    [PveApi("GET", "/storage")]
    public Task<IReadOnlyList<Row>> ListAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync("storage", ct);
    }

    [PveApi("GET", "/storage/{storage}")]
    public Task<Row> GetAsync(string storage, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"storage/{Seg(storage)}", ct);
    }

    [PveApi("POST", "/storage")]
    [PveParam("fs-name", "7.1")]
    [PveParam("keyring", "7.1")]
    [PveParam("preallocation", "7.1")]
    [PveParam("data-pool", "7.1")]
    [PveParam("max-protected-backups", "7.1")]
    [PveParam("content-dirs", "7.4")]
    [PveParam("create-base-path", "8.0")]
    [PveParam("create-subdirs", "8.0")]
    [PveParam("skip-cert-verification", "8.1")]
    [PveParam("snapshot-as-volume-chain", "9.0")]
    [PveParam("zfs-base-path", "9.0")]
    [PveParam("saferemove-stepsize", "9.0")]
    public Task<string> CreateAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PostActionAsync("storage", Supported(form), ct);
    }

    [PveApi("PUT", "/storage/{storage}")]
    [PveParam("fs-name", "7.1")]
    [PveParam("keyring", "7.1")]
    [PveParam("preallocation", "7.1")]
    [PveParam("data-pool", "7.1")]
    [PveParam("max-protected-backups", "7.1")]
    [PveParam("content-dirs", "7.4")]
    [PveParam("create-base-path", "8.0")]
    [PveParam("create-subdirs", "8.0")]
    [PveParam("skip-cert-verification", "8.1")]
    [PveParam("snapshot-as-volume-chain", "9.0")]
    [PveParam("zfs-base-path", "9.0")]
    [PveParam("saferemove-stepsize", "9.0")]
    public Task<string> UpdateAsync(string storage, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"storage/{Seg(storage)}", Supported(form), ct);
    }

    [PveApi("DELETE", "/storage/{storage}")]
    public Task<string> DeleteAsync(string storage, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"storage/{Seg(storage)}", ct);
    }

    // ------------------------------------------------------------ 노드별

    /// <param name="content">images·rootdir·iso·vztmpl·backup … — 주면 그 내용을 담을 수 있는 켜진 저장소만.</param>
    [PveApi("GET", "/nodes/{node}/storage")]
    public Task<IReadOnlyList<Row>> NodeStoragesAsync(string node, string? content = null,
        CancellationToken ct = default)
    {
        var query = content is null ? string.Empty : $"?content={Uri.EscapeDataString(content)}&enabled=1";
        return Api.GetTableAsync($"nodes/{Seg(node)}/storage{query}", ct);
    }

    [PveApi("GET", "/nodes/{node}/storage/{storage}/status")]
    public Task<Row> StatusAsync(string node, string storage, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"{S(node, storage)}/status", ct);
    }

    /// <param name="content">내용 종류로 거른다(비우면 전부).</param>
    /// <param name="vmid">그 게스트의 볼륨만.</param>
    [PveApi("GET", "/nodes/{node}/storage/{storage}/content")]
    public Task<IReadOnlyList<Row>> ContentAsync(string node, string storage, string? content = null,
        int? vmid = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (content is not null) query.Add($"content={Uri.EscapeDataString(content)}");
        if (vmid is { } id) query.Add($"vmid={id}");
        var suffix = query.Count == 0 ? string.Empty : "?" + string.Join('&', query);
        return Api.GetTableAsync($"{S(node, storage)}/content{suffix}", ct);
    }

    /// <summary>볼륨 메모(notes)·보호(protected, 7.1+).</summary>
    [PveApi("PUT", "/nodes/{node}/storage/{storage}/content/{volume}")]
    [PveParam("protected", "7.1")]
    public Task<string> UpdateVolumeAsync(string node, string storage, string volume,
        IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{S(node, storage)}/content/{Seg(volume)}", Supported(form), ct);
    }

    [PveApi("DELETE", "/nodes/{node}/storage/{storage}/content/{volume}")]
    public Task<string> DeleteVolumeAsync(string node, string storage, string volume, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{S(node, storage)}/content/{Seg(volume)}", ct);
    }

    /// <summary>주소에서 ISO·템플릿 내려받기(작업 UPID) — url·filename·content, (8.1+) compression.</summary>
    [PveApi("POST", "/nodes/{node}/storage/{storage}/download-url")]
    [PveParam("compression", "8.1")]
    public Task<string> DownloadUrlAsync(string node, string storage, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{S(node, storage)}/download-url", Supported(form), ct);
    }

    /// <summary>내려받을 수 있는 CT 템플릿 목록(aplinfo).</summary>
    [PveApi("GET", "/nodes/{node}/aplinfo")]
    public Task<IReadOnlyList<Row>> TemplatesAsync(string node, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"nodes/{Seg(node)}/aplinfo", ct);
    }

    /// <summary>CT 템플릿 내려받기(작업 UPID).</summary>
    [PveApi("POST", "/nodes/{node}/aplinfo")]
    public Task<string> DownloadTemplateAsync(string node, string storage, string template,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"nodes/{Seg(node)}/aplinfo",
            new Dictionary<string, string> { ["storage"] = storage, ["template"] = template }, ct);
    }

    // ------------------------------------------------------------ 저장소 추가 때 찾아보기(scan)

    /// <summary>웹 UI 처럼 요청을 받은 노드(localhost)에서 찾는다 — 저장소 추가는 데이터센터 설정이라 노드가 없다.</summary>
    private const string ScanNode = "nodes/localhost/scan";

    private static string Query(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs.Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}").ToList();
        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    /// <summary>NFS 서버가 내보내는 경로 — path·options.</summary>
    [PveApi("GET", "/nodes/{node}/scan/nfs")]
    public Task<IReadOnlyList<Row>> ScanNfsAsync(string server, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{ScanNode}/nfs{Query(("server", server))}", ct);
    }

    /// <summary>CIFS 서버의 공유 — share·description. 계정이 필요하면 함께 준다.</summary>
    [PveApi("GET", "/nodes/{node}/scan/cifs")]
    public Task<IReadOnlyList<Row>> ScanCifsAsync(string server, string? username, string? password,
        string? domain, CancellationToken ct = default)
    {
        var query = Query(("server", server), ("username", username), ("password", password), ("domain", domain));
        return Api.GetTableAsync($"{ScanNode}/cifs{query}", ct);
    }

    /// <summary>PBS 데이터스토어 — store·comment.</summary>
    [PveApi("GET", "/nodes/{node}/scan/pbs")]
    public Task<IReadOnlyList<Row>> ScanPbsAsync(string server, string username, string password,
        string? fingerprint, string? port, CancellationToken ct = default)
    {
        var query = Query(("server", server), ("username", username), ("password", password),
            ("fingerprint", fingerprint), ("port", port));
        return Api.GetTableAsync($"{ScanNode}/pbs{query}", ct);
    }

    /// <summary>iSCSI 포털의 타깃 — target·portal.</summary>
    [PveApi("GET", "/nodes/{node}/scan/iscsi")]
    public Task<IReadOnlyList<Row>> ScanIscsiAsync(string portal, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{ScanNode}/iscsi{Query(("portal", portal))}", ct);
    }

    /// <summary>이 노드의 LVM 볼륨 그룹 — vg·size·free.</summary>
    [PveApi("GET", "/nodes/{node}/scan/lvm")]
    public Task<IReadOnlyList<Row>> ScanLvmAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{ScanNode}/lvm", ct);
    }

    /// <summary>볼륨 그룹 안의 씬 풀 — lv.</summary>
    [PveApi("GET", "/nodes/{node}/scan/lvmthin")]
    public Task<IReadOnlyList<Row>> ScanLvmThinAsync(string vg, CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{ScanNode}/lvmthin{Query(("vg", vg))}", ct);
    }

    /// <summary>이 노드의 ZFS 풀·데이터셋 — pool.</summary>
    [PveApi("GET", "/nodes/{node}/scan/zfs")]
    public Task<IReadOnlyList<Row>> ScanZfsAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{ScanNode}/zfs", ct);
    }

    // ------------------------------------------------------------ 백업 정리(prune)

    /// <summary>
    ///     정리 미리 보기 — 백업마다 volid·vmid·ctime·type·mark(keep·remove·protected·renamed).
    ///     pruneBackups 가 비면 저장소 설정의 보존 규칙, vmid 를 주면 그 게스트 백업만.
    /// </summary>
    [PveApi("GET", "/nodes/{node}/storage/{storage}/prunebackups")]
    public Task<IReadOnlyList<Row>> PrunePreviewAsync(string node, string storage, string? pruneBackups,
        int? vmid, CancellationToken ct = default)
    {
        var query = Query(("prune-backups", pruneBackups), ("vmid", vmid?.ToString()));
        return Api.GetTableAsync($"{S(node, storage)}/prunebackups{query}", ct);
    }

    /// <summary>미리 보기에서 remove 로 표시된 백업을 지운다(작업 UPID).</summary>
    [PveApi("DELETE", "/nodes/{node}/storage/{storage}/prunebackups")]
    public Task<string> PruneAsync(string node, string storage, string? pruneBackups, int? vmid,
        CancellationToken ct = default)
    {
        var query = Query(("prune-backups", pruneBackups), ("vmid", vmid?.ToString()));
        return Api.DeleteActionAsync($"{S(node, storage)}/prunebackups{query}", ct);
    }

    /// <summary>URL 의 파일 이름·크기·형식(7.1+) — filename·size·mimetype. URL 에서 받기 창이 이름을 채운다.</summary>
    [PveApi("GET", "/nodes/{node}/query-url-metadata", Since = "7.1")]
    public async Task<Row> QueryUrlMetadataAsync(string node, string url, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetObjectAsync($"nodes/{Seg(node)}/query-url-metadata{Query(("url", url))}", ct)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------ PBS 백업 속 파일 복원

    /// <summary>
    ///     PBS 백업 안의 디렉터리 목록 — filepath(base64)·text·type(d 디렉터리·f 파일·v 가상·h 하드링크 등)·leaf·size·mtime.
    ///     path 는 base64 로 적은 경로, 맨 위는 "/". 처음 열 때 서버가 복원용 가상 머신을 띄워 수십 초 걸릴 수 있다.
    /// </summary>
    [PveApi("GET", "/nodes/{node}/storage/{storage}/file-restore/list")]
    public Task<IReadOnlyList<Row>> FileRestoreListAsync(string node, string storage, string volume, string path,
        CancellationToken ct = default)
    {
        return Api.GetTableAsync($"{S(node, storage)}/file-restore/list"
                                 + Query(("volume", volume), ("filepath", path)), LongRequestTimeout, ct);
    }

    /// <summary>
    ///     PBS 백업 안의 파일(또는 디렉터리를 zip 으로)을 destination 에 내려받는다. tar=true 면 디렉터리를 tar.zst 로.
    /// </summary>
    [PveApi("GET", "/nodes/{node}/storage/{storage}/file-restore/download")]
    public Task FileRestoreDownloadAsync(string node, string storage, string volume, string path, bool tar,
        Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var query = Query(("volume", volume), ("filepath", path), ("tar", tar ? "1" : null));
        return Api.DownloadAsync($"{S(node, storage)}/file-restore/download{query}", destination,
            LongRequestTimeout, progress, ct);
    }

    // ------------------------------------------------------------ OCI 이미지(9.0 — 컨테이너 템플릿으로 받기)

    /// <summary>
    ///     OCI 이미지를 CT 템플릿으로 받는다(9.0+, 작업 UPID) — reference 예: docker.io/library/alpine:3.20.
    ///     filename 을 비우면 서버가 이름을 정한다.
    /// </summary>
    [PveApi("POST", "/nodes/{node}/storage/{storage}/oci-registry-pull", Since = "9.0")]
    public async Task<string> OciPullAsync(string node, string storage, string reference, string? filename,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        var form = new Dictionary<string, string> { ["reference"] = reference };
        if (!string.IsNullOrWhiteSpace(filename)) form["filename"] = filename.Trim();
        return await Api.PostActionAsync($"{S(node, storage)}/oci-registry-pull", form, ct).ConfigureAwait(false);
    }

    /// <summary>OCI 저장소의 태그 목록(9.0+) — repository 예: docker.io/library/alpine.</summary>
    [PveApi("GET", "/nodes/{node}/query-oci-repo-tags", Since = "9.0")]
    public async Task<IReadOnlyList<string>> OciTagsAsync(string node, string repository,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        var text = await Api.GetTextAsync($"nodes/{Seg(node)}/query-oci-repo-tags{Query(("reference", repository))}",
            ct).ConfigureAwait(false);
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>저장소 사용량 시계열 — time·used·total(바이트). timeframe: hour·day·week·month·year.</summary>
    [PveApi("GET", "/nodes/{node}/storage/{storage}/rrddata")]
    public Task<IReadOnlyList<Row>> RrdDataAsync(string node, string storage, string timeframe,
        CancellationToken ct = default)
    {
        if (timeframe is not ("hour" or "day" or "week" or "month" or "year"))
            throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null);
        return Api.GetTableAsync($"{S(node, storage)}/rrddata?timeframe={timeframe}&cf=AVERAGE", ct);
    }

    /// <summary>
    ///     빈 디스크 이미지를 만든다 — filename 예: vm-100-disk-1(파일 저장소는 .qcow2 등), size 예: 32G, format: raw·qcow2 등.
    ///     만든 디스크는 VM 설정에 '안 쓰는 디스크'로 붙지 않으니 하드웨어에서 직접 붙인다.
    /// </summary>
    [PveApi("POST", "/nodes/{node}/storage/{storage}/content")]
    public Task<string> AllocateDiskAsync(string node, string storage, int vmid, string filename, string size,
        string? format, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["vmid"] = vmid.ToString(System.Globalization.CultureInfo.InvariantCulture), ["filename"] = filename,
            ["size"] = size
        };
        if (!string.IsNullOrEmpty(format)) form["format"] = format;
        return Api.PostActionAsync($"{S(node, storage)}/content", form, ct);
    }
}
