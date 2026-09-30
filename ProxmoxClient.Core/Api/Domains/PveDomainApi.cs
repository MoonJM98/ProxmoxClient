using System.Runtime.CompilerServices;
using ProxmoxClient.Core.Api.Versioning;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     영역별 API 묶음(풀·알림·매핑 …)의 바탕. 경로 문자열과 보내는 형식(배열·delete·버전별 경로)을 여기 모아
///     화면 코드는 값만 넘긴다. 메서드마다 <see cref="PveApiAttribute" /> 로 엔드포인트와 도입 버전을 적는다.
/// </summary>
public abstract class PveDomainApi(ProxmoxApiClient api)
{
    /// <summary>
    ///     응답을 받기까지 오래 걸리는 요청(시스템 보고서, 큰 디스크 TRIM)의 제한 시간 — 기본 30초로는 모자란다.
    /// </summary>
    protected static readonly TimeSpan LongRequestTimeout = TimeSpan.FromMinutes(5);

    protected ProxmoxApiClient Api { get; } = api;

    /// <summary>부른 메서드의 도입 버전을 요구한다 — 서버가 낮으면 요청 전에 알아볼 수 있는 오류.</summary>
    protected Task RequireAsync(CancellationToken ct, [CallerMemberName] string member = "")
    {
        return Api.RequireAsync(GetType(), ct, member);
    }

    /// <summary>서버가 since 이상인지(버전을 모르면 먼저 읽고, 그래도 모르면 true).</summary>
    protected Task<bool> SupportsAsync(string since, CancellationToken ct)
    {
        return Api.SupportsAsync(PveApiVersion.Parse(since), ct);
    }

    /// <summary>
    ///     이 영역의 메서드를 지금 서버에서 쓸 수 있는지(도입 버전 ≤ 서버 버전). 버전을 모르면 true.
    ///     화면이 탭·버튼·선택지를 숨길 때 쓴다 — 예: api.Notifications.IsSupported(nameof(...)).
    /// </summary>
    public bool IsSupported(string method)
    {
        return Api.Supports(ProxmoxApiClient.SinceOf(GetType(), method));
    }

    /// <summary>
    ///     화면 요소가 이 메서드를 쓴다고 알리는 기능 — 메서드를 못 쓰는 서버면 요소가 숨고, 입력 창·옵션 목록은
    ///     서버가 모르는 파라미터 칸을 뺀다. <paramref name="param" /> 를 주면 그 파라미터를 알 때만 쓸 수 있다.
    /// </summary>
    public ApiFeature Feature(string method, string? param = null)
    {
        return new ApiFeature(
            () => param is null ? IsSupported(method) : SupportsParam(method, param),
            key => SupportsParam(method, key));
    }

    /// <summary>
    ///     그 메서드가 보내는 파라미터를 지금 서버가 아는지 — <see cref="PveParamAttribute" /> 가 없으면 true.
    ///     화면이 옵션·입력 칸을 숨길 때 쓴다(예: 데이터센터 옵션 목록).
    /// </summary>
    public bool SupportsParam(string method, string param)
    {
        var since = GetType().GetMethods()
            .Where(m => m.Name == method)
            .SelectMany(m => m.GetCustomAttributes(typeof(PveParamAttribute), false).Cast<PveParamAttribute>())
            .Where(p => p.Name == param || MatchesIndexed(p.Name, param))
            .Select(p => (PveApiVersion?)p.SinceVersion)
            .FirstOrDefault();
        return since is not { } v || Api.Supports(v);
    }

    /// <summary>
    ///     부른 메서드의 <see cref="PveParamAttribute" /> 를 보고, 서버가 아직 모르는 파라미터를 뺀다
    ///     (delete 목록 안의 이름도). 모르는 파라미터를 보내면 서버가 요청 전체를 거절하기 때문이다.
    ///     서버 버전을 모르면 그대로 둔다.
    /// </summary>
    protected Dictionary<string, string> Supported(IReadOnlyDictionary<string, string> form,
        [CallerMemberName] string member = "")
    {
        var unknown = UnknownParams(member);
        return form.Select(kv => (kv.Key, Value: Keep(kv.Key, kv.Value, unknown)))
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal);
    }

    /// <summary>쌍 목록판(같은 키를 반복하는 배열 요청) <see cref="Supported" />.</summary>
    protected List<KeyValuePair<string, string>> SupportedPairs(IReadOnlyList<KeyValuePair<string, string>> pairs,
        [CallerMemberName] string member = "")
    {
        var unknown = UnknownParams(member);
        return pairs.Select(p => (p.Key, Value: Keep(p.Key, p.Value, unknown)))
            .Where(p => p.Value is not null)
            .Select(p => new KeyValuePair<string, string>(p.Key, p.Value!))
            .ToList();
    }

    /// <summary>서버가 모르는 파라미터 이름(버전을 모르면 없음).</summary>
    private HashSet<string> UnknownParams(string member)
    {
        return GetType().GetMethods()
            .Where(m => m.Name == member)
            .SelectMany(m => m.GetCustomAttributes(typeof(PveParamAttribute), false).Cast<PveParamAttribute>())
            .Where(p => !Api.Supports(p.SinceVersion))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>보낼 값 — 모르는 키면 null, delete 면 모르는 이름을 뺀 목록(남은 게 없으면 null).</summary>
    private static string? Keep(string key, string value, HashSet<string> unknown)
    {
        if (IsUnknown(key, unknown)) return null;
        if (key != "delete" || unknown.Count == 0) return value;
        var kept = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !IsUnknown(name, unknown)).ToList();
        return kept.Count == 0 ? null : string.Join(',', kept);
    }

    /// <summary>이름이 모르는 파라미터인지 — "virtiofs[n]" 은 virtiofs0·virtiofs1 … 과 맞는다.</summary>
    private static bool IsUnknown(string key, HashSet<string> unknown)
    {
        return unknown.Contains(key) || unknown.Any(p => MatchesIndexed(p, key));
    }

    /// <summary>"name[n]" 형식 파라미터가 key(name + 숫자)와 맞는지.</summary>
    internal static bool MatchesIndexed(string param, string key)
    {
        if (!param.EndsWith("[n]", StringComparison.Ordinal)) return false;
        var prefix = param[..^3];
        return key.Length > prefix.Length && key.StartsWith(prefix, StringComparison.Ordinal)
                                          && key[prefix.Length..].All(char.IsAsciiDigit);
    }

    protected static string Seg(string value)
    {
        return ProxmoxApiClient.PathSegment(value);
    }
}
