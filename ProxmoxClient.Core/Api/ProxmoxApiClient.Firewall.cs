using System.Text.Json;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Api;

/// <summary>방화벽 — 데이터센터·노드·게스트의 규칙과 옵션.</summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>방화벽 옵션(GET {scope}/options)을 문자열 맵으로 읽는다.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetFirewallOptionsAsync(
        FirewallScope scope, CancellationToken ct = default)
    {
        var data = await GetJsonAsync($"{scope.BasePath}/options", ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data.ValueKind == JsonValueKind.Object)
            foreach (var property in data.EnumerateObject())
                map[property.Name] = property.Value.ToString();

        return map;
    }
    /// <summary>방화벽을 켜거나 끈다(PUT {scope}/options).</summary>
    public Task<string> SetFirewallEnabledAsync(FirewallScope scope, bool enabled, CancellationToken ct = default)
    {
        return SendWriteAsync(
            HttpMethod.Put,
            $"{scope.BasePath}/options",
            new Dictionary<string, string> { ["enable"] = enabled ? "1" : "0" },
            ct);
    }
    /// <summary>방화벽 규칙 목록(GET {scope}/rules).</summary>
    public async Task<IReadOnlyList<PveFirewallRule>> GetFirewallRulesAsync(
        FirewallScope scope, CancellationToken ct = default)
    {
        var data = await GetJsonAsync($"{scope.RulesPath}", ct).ConfigureAwait(false);
        var list = new List<PveFirewallRule>();
        if (data.ValueKind != JsonValueKind.Array) return list;

        foreach (var item in data.EnumerateArray())
        {
            var type = GetString(item, "type");
            list.Add(new PveFirewallRule
            {
                Pos = GetInt(item, "pos"),
                Action = GetString(item, "action"),
                Direction = type is "in" or "out" ? type : string.Empty,
                Proto = GetString(item, "proto"),
                DPort = GetString(item, "dport"),
                Source = GetString(item, "source"),
                Macro = GetString(item, "macro"),
                Comment = GetString(item, "comment"),
                Enabled = GetInt(item, "enable") == 1,
                Type = type
            });
        }

        return list;
    }
    /// <summary>규칙 추가(POST {scope}/rules). pos는 서버가 정한다.</summary>
    public Task<string> AddFirewallRuleAsync(FirewallScope scope, PveFirewallRule rule, CancellationToken ct = default)
    {
        return PostWriteAsync(
            scope.RulesPath,
            new Dictionary<string, string>(rule.ToForm(), StringComparer.Ordinal),
            ct);
    }
    /// <summary>pos 위치의 규칙을 바꾼다(PUT {scope}/rules/{pos}).</summary>
    public Task<string> UpdateFirewallRuleAsync(
        FirewallScope scope, int pos, PveFirewallRule rule, CancellationToken ct = default)
    {
        return SendWriteAsync(
            HttpMethod.Put,
            $"{scope.RulesPath}/{pos}",
            new Dictionary<string, string>(rule.ToForm(), StringComparer.Ordinal),
            ct);
    }
    /// <summary>pos 위치의 규칙을 지운다(DELETE {scope}/rules/{pos}).</summary>
    public Task<string> DeleteFirewallRuleAsync(FirewallScope scope, int pos, CancellationToken ct = default)
    {
        return SendWriteAsync(HttpMethod.Delete, $"{scope.RulesPath}/{pos}", null, ct);
    }
}
