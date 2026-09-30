using System.Text.Json;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     관리 화면용 범용 조회 — 네트워크·디스크·사용자 목록처럼 필드만 다른 표를 모델 없이 문자열 맵으로 읽는다.
/// </summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>경로를 이루는 이름(노드·저장소 등)을 URL 안전하게 만든다.</summary>
    public static string PathSegment(string value)
    {
        return Uri.EscapeDataString(value);
    }

    /// <summary>배열 응답(GET {relativePath})의 각 항목을 문자열 맵으로 돌려준다.</summary>
    /// <param name="relativePath">api2/json 아래 경로. 이름이 들어가면 <see cref="PathSegment" />로 감쌀 것.</param>
    public Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> GetTableAsync(
        string relativePath, CancellationToken ct = default)
    {
        return GetTableAsync(relativePath, null, ct);
    }

    /// <summary>오래 걸리는 목록(백업 속 파일 등) — 기본 제한 시간 대신 timeout 까지 기다린다.</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> GetTableAsync(
        string relativePath, TimeSpan? timeout, CancellationToken ct = default)
    {
        var data = await GetJsonAsync(relativePath, ct, timeout).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Array) return [];

        var rows = new List<IReadOnlyDictionary<string, string>>(data.GetArrayLength());
        foreach (var item in data.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object)
                rows.Add(ToStringMap(item));

        return rows;
    }

    /// <summary>
    ///     객체 응답 안의 배열 속성(예: LVM 의 children, SMART 의 attributes)을 표로 돌려준다.
    ///     속성이 없거나 배열이 아니면 빈 목록.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> GetArrayPropertyAsync(
        string relativePath, string property, CancellationToken ct = default)
    {
        var data = await GetJsonAsync(relativePath, ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(property, out var items)
                                                   || items.ValueKind != JsonValueKind.Array)
            return [];

        var rows = new List<IReadOnlyDictionary<string, string>>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object)
                rows.Add(ToStringMap(item));

        return rows;
    }

    /// <summary>
    ///     배열 응답의 각 항목 안에 든 하위 배열을 한 행씩 펼친다(예: 2단계 인증 — 사용자마다 entries).
    ///     하위 행에는 부모 항목의 단순 값 필드도 함께 담는다(같은 이름이면 하위 값이 우선).
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> GetFlattenedTableAsync(
        string relativePath, string childProperty, CancellationToken ct = default)
    {
        var data = await GetJsonAsync(relativePath, ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Array) return [];

        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var parent in data.EnumerateArray())
        {
            if (parent.ValueKind != JsonValueKind.Object
                || !parent.TryGetProperty(childProperty, out var children)
                || children.ValueKind != JsonValueKind.Array)
                continue;

            var parentFields = parent.EnumerateObject()
                .Where(p => p.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                .ToDictionary(p => p.Name, p => ToText(p.Value), StringComparer.Ordinal);

            foreach (var child in children.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Object) continue;

                var row = new Dictionary<string, string>(parentFields, StringComparer.Ordinal);
                foreach (var property in child.EnumerateObject()) row[property.Name] = ToText(property.Value);
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>
    ///     설정 하나를 읽는다 — 배열(알림 수신자·매핑 map 등)은 줄바꿈으로 이어 여러 줄 칸에 그대로 넣는다
    ///     (항목이 객체·배열이면 한 줄 JSON). 표 조회는 배열을 ", " 로 이어 값 안의 쉼표와 구분할 수 없다.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetConfigLinesAsync(string relativePath,
        CancellationToken ct = default)
    {
        var data = await GetJsonAsync(relativePath, ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in data.EnumerateObject())
            map[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.Array => string.Join('\n', p.Value.EnumerateArray().Select(e =>
                    e.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        ? JsonSerializer.Serialize(e)
                        : e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.ToString())),
                JsonValueKind.String => p.Value.GetString() ?? string.Empty,
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => p.Value.ToString()
            };
        return map;
    }

    /// <summary>객체 응답(GET {relativePath})을 문자열 맵으로 돌려준다.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetObjectAsync(
        string relativePath, CancellationToken ct = default)
    {
        var data = await GetJsonAsync(relativePath, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object
            ? ToStringMap(data)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>문자열 응답(GET {relativePath}) — 패키지 변경 기록처럼 긴 글.</summary>
    public async Task<string> GetTextAsync(string relativePath, CancellationToken ct = default)
    {
        return ToText(await GetJsonAsync(relativePath, ct).ConfigureAwait(false));
    }

    /// <summary>오래 걸리는 글 응답(시스템 보고서 등) — 기본 제한 시간 대신 timeout 까지 기다린다.</summary>
    public async Task<string> GetTextAsync(string relativePath, TimeSpan timeout, CancellationToken ct = default)
    {
        return ToText(await GetJsonAsync(relativePath, ct, timeout).ConfigureAwait(false));
    }

    /// <summary>작업을 요청한다(POST {relativePath}). 작업이 생기면 UPID 를 돌려준다.</summary>
    public Task<string> PostActionAsync(
        string relativePath, IReadOnlyDictionary<string, string>? form = null, CancellationToken ct = default)
    {
        return PostWriteAsync(relativePath, form, ct);
    }

    /// <summary>
    ///     쓰기 요청의 응답이 객체일 때(예: API 토큰 생성 — 비밀 값은 이 응답에서 한 번만 받을 수 있다).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> SendForObjectAsync(
        HttpMethod method, string relativePath, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ThrowIfDisposed();

        using var response = await SendAsync(
                () => new HttpRequestMessage(method, relativePath) { Content = new FormUrlEncodedContent(form) },
                true,
                ct,
                timeout)
            .ConfigureAwait(false);
        using var doc = await ReadJsonDocumentAsync(response, ct).ConfigureAwait(false);
        var data = DataElement(doc);
        return data.ValueKind == JsonValueKind.Object
            ? ToStringMap(data)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    ///     같은 키를 여러 번 보내야 하는 쓰기(배열 파라미터 — 예: 알림 규칙의 target 여러 개).
    ///     arrayKeys 에 든 키의 값은 쉼표로 나눠 키를 반복한다.
    /// </summary>
    public Task<string> SendWithArraysAsync(HttpMethod method, string relativePath,
        IReadOnlyDictionary<string, string> form, IReadOnlySet<string> arrayKeys, CancellationToken ct = default)
    {
        return SendPairsAsync(method, relativePath, ExpandArrays(form, arrayKeys), ct);
    }

    /// <summary>arrayKeys 에 든 키의 쉼표 목록을 키 반복으로 펼친다(나머지 키는 그대로 한 번).</summary>
    internal static List<KeyValuePair<string, string>> ExpandArrays(IReadOnlyDictionary<string, string> form,
        IReadOnlySet<string> arrayKeys)
    {
        return form.SelectMany(kv => arrayKeys.Contains(kv.Key)
            ? kv.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => new KeyValuePair<string, string>(kv.Key, v))
            : [kv]).ToList();
    }

    /// <summary>키·값 쌍을 보내고 응답 객체를 문자열 맵으로 받는다(배열 파라미터 + 객체 응답 — 에이전트 exec 등).</summary>
    public async Task<IReadOnlyDictionary<string, string>> SendPairsForObjectAsync(HttpMethod method,
        string relativePath, IReadOnlyList<KeyValuePair<string, string>> pairs, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var response = await SendAsync(
                () => new HttpRequestMessage(method, relativePath) { Content = new FormUrlEncodedContent(pairs) },
                true,
                ct)
            .ConfigureAwait(false);
        using var doc = await ReadJsonDocumentAsync(response, ct).ConfigureAwait(false);
        var data = DataElement(doc);
        return data.ValueKind == JsonValueKind.Object
            ? ToStringMap(data)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>키·값 쌍을 그대로 보낸다 — 값 자체에 쉼표가 든 배열 파라미터(리소스 매핑의 map 등)용.</summary>
    public async Task<string> SendPairsAsync(HttpMethod method, string relativePath,
        IReadOnlyList<KeyValuePair<string, string>> pairs, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var response = await SendAsync(
                () => new HttpRequestMessage(method, relativePath) { Content = new FormUrlEncodedContent(pairs) },
                true,
                ct)
            .ConfigureAwait(false);
        using var doc = await ReadJsonDocumentAsync(response, ct).ConfigureAwait(false);
        var data = DataElement(doc);
        return data.ValueKind == JsonValueKind.String ? data.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>설정을 바꾼다(PUT {relativePath}).</summary>
    public Task<string> PutActionAsync(
        string relativePath, IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        return SendWriteAsync(HttpMethod.Put, relativePath, form, ct);
    }

    /// <summary>항목을 지운다(DELETE {relativePath}).</summary>
    public Task<string> DeleteActionAsync(string relativePath, CancellationToken ct = default)
    {
        return DeleteWriteAsync(relativePath, ct);
    }

    private static Dictionary<string, string> ToStringMap(in JsonElement obj)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
            map[property.Name] = ToText(property.Value);

        return map;
    }

    private static string ToText(in JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(v => ToText(v))),
            _ => value.GetRawText()
        };
    }
}
