using ProxmoxClient.Core.Api.Versioning;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, string>;

namespace ProxmoxClient.Core.Api.Domains;

/// <summary>
///     외부 메트릭 서버 — graphite·influxdb, OpenTelemetry(9.0+). influxdb 의 verify-certificate 는 7.1+.
/// </summary>
public sealed class MetricsApi(ProxmoxApiClient api) : PveDomainApi(api)
{
    private const string Path = "cluster/metrics/server";
    private const string OpenTelemetry = "9.0";

    /// <summary>이 서버에서 만들 수 있는 종류(opentelemetry 는 9.0+).</summary>
    public IReadOnlyList<string> Types =>
        Api.Supports(PveApiVersion.Parse(OpenTelemetry))
            ? ["influxdb", "graphite", "opentelemetry"]
            : ["influxdb", "graphite"];

    [PveApi("GET", "/cluster/metrics/server")]
    public Task<IReadOnlyList<Row>> ListAsync(CancellationToken ct = default)
    {
        return Api.GetTableAsync(Path, ct);
    }

    [PveApi("GET", "/cluster/metrics/server/{id}")]
    public Task<Row> GetAsync(string id, CancellationToken ct = default)
    {
        return Api.GetObjectAsync($"{Path}/{Seg(id)}", ct);
    }

    [PveApi("POST", "/cluster/metrics/server/{id}")]
    [PveParam("verify-certificate", "7.1")]
    [PveParam("otel-protocol", OpenTelemetry)]
    [PveParam("otel-path", OpenTelemetry)]
    [PveParam("otel-verify-ssl", OpenTelemetry)]
    [PveParam("otel-headers", OpenTelemetry)]
    [PveParam("otel-resource-attributes", OpenTelemetry)]
    [PveParam("otel-compression", OpenTelemetry)]
    [PveParam("otel-timeout", OpenTelemetry)]
    [PveParam("otel-max-body-size", OpenTelemetry)]
    public Task<string> CreateAsync(string id, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PostActionAsync($"{Path}/{Seg(id)}", Supported(form), ct);
    }

    [PveApi("PUT", "/cluster/metrics/server/{id}")]
    [PveParam("verify-certificate", "7.1")]
    [PveParam("otel-protocol", OpenTelemetry)]
    [PveParam("otel-path", OpenTelemetry)]
    [PveParam("otel-verify-ssl", OpenTelemetry)]
    [PveParam("otel-headers", OpenTelemetry)]
    [PveParam("otel-resource-attributes", OpenTelemetry)]
    [PveParam("otel-compression", OpenTelemetry)]
    [PveParam("otel-timeout", OpenTelemetry)]
    [PveParam("otel-max-body-size", OpenTelemetry)]
    public Task<string> UpdateAsync(string id, IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        return Api.PutActionAsync($"{Path}/{Seg(id)}", Supported(form), ct);
    }

    [PveApi("DELETE", "/cluster/metrics/server/{id}")]
    public Task<string> DeleteAsync(string id, CancellationToken ct = default)
    {
        return Api.DeleteActionAsync($"{Path}/{Seg(id)}", ct);
    }
}
