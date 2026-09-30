using System.Text.Json;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Api;

/// <summary>가져오기(ESXi·OVA) 정보 — 응답이 객체 안 객체라 표 대신 <see cref="ImportMetadata" /> 로 푼다.</summary>
public sealed partial class ProxmoxApiClient
{
    private static readonly Versioning.PveApiVersion ImportSince = new(8, 1);

    /// <summary>
    ///     가져올 게스트(볼륨 예: esxi:ha-datacenter/datastore1/vm1/vm1.vmx)의 새 VM 설정·디스크·네트워크·알려진 문제(8.1+).
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/storage/{storage}/import-metadata", Since = "8.1")]
    public async Task<ImportMetadata> GetImportMetadataAsync(string node, string storage, string volume,
        CancellationToken ct = default)
    {
        if (!await SupportsAsync(ImportSince, ct).ConfigureAwait(false))
            throw new ProxmoxApiException(Localization.Res.T("Api_VersionRequired", ImportSince, ServerVersion));

        var data = await GetJsonAsync($"nodes/{Escape(node)}/storage/{Escape(storage)}/import-metadata"
                                      + $"?volume={Uri.EscapeDataString(volume)}", ct).ConfigureAwait(false);
        return new ImportMetadata(Map(data, "create-args"), Map(data, "disks"), Nics(data), ImportWarnings(data));
    }

    private static Dictionary<string, string> Map(in JsonElement data, string name)
    {
        return data.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object
            ? ToStringMap(obj)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static Dictionary<string, ImportNic> Nics(in JsonElement data)
    {
        var result = new Dictionary<string, ImportNic>(StringComparer.Ordinal);
        if (!data.TryGetProperty("net", out var nets) || nets.ValueKind != JsonValueKind.Object) return result;

        foreach (var nic in nets.EnumerateObject())
        {
            string? V(string key) => nic.Value.ValueKind == JsonValueKind.Object
                                     && nic.Value.TryGetProperty(key, out var v)
                ? ToText(v)
                : null;
            result[nic.Name] = new ImportNic(V("macaddr"), V("model"));
        }

        return result;
    }

    /// <summary>알려진 문제 한 줄씩 — "type (key=value)".</summary>
    private static List<string> ImportWarnings(in JsonElement data)
    {
        if (!data.TryGetProperty("warnings", out var list) || list.ValueKind != JsonValueKind.Array) return [];

        return list.EnumerateArray().Where(w => w.ValueKind == JsonValueKind.Object).Select(w =>
        {
            string V(string key) => w.TryGetProperty(key, out var v) ? ToText(v) : string.Empty;
            var subject = V("key").Length > 0 ? $" ({V("key")}{(V("value").Length > 0 ? "=" + V("value") : "")})" : "";
            return V("type") + subject;
        }).ToList();
    }
}
