namespace ProxmoxClient.Core.Models;

/// <summary>GET /nodes/{node}/storage/{storage}/content 항목 — ISO/템플릿/백업 파일 등.</summary>
public sealed class PveContentFile
{
    /// <summary>볼륨 ID, 예: "local:iso/ubuntu-2404.iso".</summary>
    public string Volid { get; init; } = string.Empty;

    /// <summary>콘텐츠 종류(iso, vztmpl, backup, images ...).</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>파일 형식(iso, tar.gz, qcow2 ...).</summary>
    public string Format { get; init; } = string.Empty;

    public long SizeBytes { get; init; }
}

/// <summary>게스트 방화벽 규칙 (GET/POST /nodes/{n}/{kind}/{vmid}/firewall/rules).</summary>
public sealed class PveFirewallRule
{
    public int Pos { get; set; }

    /// <summary>ACCEPT | DROP | REJECT.</summary>
    public string Action { get; set; } = "ACCEPT";

    /// <summary>in | out.</summary>
    public string Direction { get; set; } = "in";

    /// <summary>tcp | udp | icmp | (빈 값=모두).</summary>
    public string Proto { get; set; } = string.Empty;

    /// <summary>대상 포트/범위, 예: "22" 또는 "8006", "2000-3000".</summary>
    public string DPort { get; set; } = string.Empty;

    /// <summary>소스 CIDR/별칭 (빈 값=모두).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>서비스 매크로(ssh, http, ... 비우면 미사용).</summary>
    public string Macro { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>규칙 유형: "in"/"out" 일반 규칙 or "group".</summary>
    public string Type { get; set; } = string.Empty;

    private Dictionary<string, string> Form => new()
    {
        ["action"] = Action,
        ["type"] = Direction,
        ["proto"] = Proto,
        ["dport"] = DPort,
        ["source"] = Source,
        ["macro"] = Macro,
        ["comment"] = Comment,
        ["enable"] = Enabled ? "1" : "0"
    };

    /// <summary>POST/PUT 전송용 폼 필드 (pos 제외).</summary>
    public IReadOnlyDictionary<string, string> ToForm()
    {
        return Form.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}