using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     알림(PVE 8.1+) — 대상(sendmail·smtp·gotify, webhook 은 8.2+)과 규칙(matcher).
///     보내는 형식: 줄마다 하나인 목록(mailto·match-field …)과 쉼표 목록(target·delete)은 키를 반복한다(배열).
/// </summary>
public sealed class NotificationsApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private const string Base = "cluster/notifications";
    private const string Webhook = "8.2";

    /// <summary>줄마다 하나씩 보내는 목록 키.</summary>
    private static readonly HashSet<string> LineArrays =
        ["mailto", "mailto-user", "match-field", "match-calendar", "header"];

    /// <summary>쉼표로 고르는 목록 키 — 대상, 그리고 delete(알림 API 의 delete 는 항목마다 반복하는 배열이다).</summary>
    private static readonly HashSet<string> CommaArrays = ["target", "delete"];

    /// <summary>이 서버에서 만들 수 있는 대상 종류(webhook 은 8.2+).</summary>
    public IReadOnlyList<string> EndpointTypes =>
        Api.Supports(PveApiVersion.Parse(Webhook))
            ? ["sendmail", "smtp", "gotify", "webhook"]
            : ["sendmail", "smtp", "gotify"];

    [PveApi("GET", "/cluster/notifications/targets", Since = "8.1")]
    public async Task<IReadOnlyList<Row>> ListTargetsAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync($"{Base}/targets", ct).ConfigureAwait(false);
    }

    [PveApi("POST", "/cluster/notifications/targets/{name}/test", Since = "8.1")]
    public async Task<string> TestTargetAsync(string name, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.PostActionAsync($"{Base}/targets/{Seg(name)}/test", null, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 대상(endpoint)

    [PveApi("GET", "/cluster/notifications/endpoints/sendmail/{name}", Since = "8.1")]
    [PveApi("GET", "/cluster/notifications/endpoints/smtp/{name}", Since = "8.1")]
    [PveApi("GET", "/cluster/notifications/endpoints/gotify/{name}", Since = "8.1")]
    [PveApi("GET", "/cluster/notifications/endpoints/webhook/{name}", Since = Webhook)]
    public async Task<Row> GetEndpointAsync(string type, string name, CancellationToken ct = default)
    {
        await RequireTypeAsync(type, ct).ConfigureAwait(false);
        return await Api.GetConfigLinesAsync(EndpointPath(type, name), ct).ConfigureAwait(false);
    }

    [PveApi("POST", "/cluster/notifications/endpoints/sendmail", Since = "8.1")]
    [PveApi("POST", "/cluster/notifications/endpoints/smtp", Since = "8.1")]
    [PveApi("POST", "/cluster/notifications/endpoints/gotify", Since = "8.1")]
    [PveApi("POST", "/cluster/notifications/endpoints/webhook", Since = Webhook)]
    public async Task<string> CreateEndpointAsync(string type, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireTypeAsync(type, ct).ConfigureAwait(false);
        return await Api.SendPairsAsync(HttpMethod.Post, $"{Base}/endpoints/{Seg(type)}", ToPairs(form), ct)
            .ConfigureAwait(false);
    }

    /// <summary>수정 — form 의 delete(쉼표 목록)는 항목마다 반복해 보낸다.</summary>
    [PveApi("PUT", "/cluster/notifications/endpoints/sendmail/{name}", Since = "8.1")]
    [PveApi("PUT", "/cluster/notifications/endpoints/smtp/{name}", Since = "8.1")]
    [PveApi("PUT", "/cluster/notifications/endpoints/gotify/{name}", Since = "8.1")]
    [PveApi("PUT", "/cluster/notifications/endpoints/webhook/{name}", Since = Webhook)]
    public async Task<string> UpdateEndpointAsync(string type, string name, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireTypeAsync(type, ct).ConfigureAwait(false);
        return await Api.SendPairsAsync(HttpMethod.Put, EndpointPath(type, name), ToPairs(form), ct)
            .ConfigureAwait(false);
    }

    [PveApi("DELETE", "/cluster/notifications/endpoints/sendmail/{name}", Since = "8.1")]
    [PveApi("DELETE", "/cluster/notifications/endpoints/smtp/{name}", Since = "8.1")]
    [PveApi("DELETE", "/cluster/notifications/endpoints/gotify/{name}", Since = "8.1")]
    [PveApi("DELETE", "/cluster/notifications/endpoints/webhook/{name}", Since = Webhook)]
    public async Task<string> DeleteEndpointAsync(string type, string name, CancellationToken ct = default)
    {
        await RequireTypeAsync(type, ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync(EndpointPath(type, name), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 규칙(matcher)

    [PveApi("GET", "/cluster/notifications/matchers", Since = "8.1")]
    public async Task<IReadOnlyList<Row>> ListMatchersAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync($"{Base}/matchers", ct).ConfigureAwait(false);
    }

    [PveApi("GET", "/cluster/notifications/matchers/{name}", Since = "8.1")]
    public async Task<Row> GetMatcherAsync(string name, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetConfigLinesAsync($"{Base}/matchers/{Seg(name)}", ct).ConfigureAwait(false);
    }

    [PveApi("POST", "/cluster/notifications/matchers", Since = "8.1")]
    public async Task<string> CreateMatcherAsync(IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.SendPairsAsync(HttpMethod.Post, $"{Base}/matchers", ToPairs(form), ct).ConfigureAwait(false);
    }

    [PveApi("PUT", "/cluster/notifications/matchers/{name}", Since = "8.1")]
    public async Task<string> UpdateMatcherAsync(string name, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.SendPairsAsync(HttpMethod.Put, $"{Base}/matchers/{Seg(name)}", ToPairs(form), ct)
            .ConfigureAwait(false);
    }

    [PveApi("DELETE", "/cluster/notifications/matchers/{name}", Since = "8.1")]
    public async Task<string> DeleteMatcherAsync(string name, CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.DeleteActionAsync($"{Base}/matchers/{Seg(name)}", ct).ConfigureAwait(false);
    }

    /// <summary>폼 → 요청 쌍: 줄 목록·쉼표 목록 키는 항목마다 키를 반복한다.</summary>
    public static List<KeyValuePair<string, string>> ToPairs(IReadOnlyDictionary<string, string> form)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in form)
        {
            var parts = LineArrays.Contains(key)
                ? value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : CommaArrays.Contains(key)
                    ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [value];
            pairs.AddRange(parts.Select(v => new KeyValuePair<string, string>(key, v)));
        }

        return pairs;
    }

    private static string EndpointPath(string type, string name)
    {
        return $"{Base}/endpoints/{Seg(type)}/{Seg(name)}";
    }

    /// <summary>알림 전체(8.1)와 webhook(8.2) 도입 버전을 확인한다.</summary>
    private async Task RequireTypeAsync(string type, CancellationToken ct)
    {
        await Api.RequireAsync(GetType(), ct, nameof(ListTargetsAsync)).ConfigureAwait(false);
        if (type == "webhook" && !await SupportsAsync(Webhook, ct).ConfigureAwait(false))
            throw new ProxmoxApiException(Localization.Res.T("Api_VersionRequired", Webhook, Api.ServerVersion));
    }

    /// <summary>알림 규칙에 쓸 수 있는 필드 값(8.2+) — field·value·comment(예: type=vzdump, hostname=pve).</summary>
    [PveApi("GET", "/cluster/notifications/matcher-field-values", Since = "8.2")]
    public async Task<IReadOnlyList<Row>> MatcherFieldValuesAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct).ConfigureAwait(false);
        return await Api.GetTableAsync("cluster/notifications/matcher-field-values", ct).ConfigureAwait(false);
    }
}
