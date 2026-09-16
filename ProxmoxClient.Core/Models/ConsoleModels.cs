using System.Text;

namespace ProxmoxClient.Core.Models;

/// <summary>POST /nodes/{node}/qemu/{vmid}/vncproxy 결과 — VNC 웹소켓 연결에 필요.</summary>
public sealed class VncProxyInfo
{
    /// <summary>노드 측 VNC 프록시 포트.</summary>
    public int Port { get; init; }

    /// <summary>VNC 티켓. vncticket 쿼리 파라미터이자 QEMU VNC 인증 비밀번호로 사용.</summary>
    public string Ticket { get; init; } = string.Empty;

    /// <summary>프록시 시작 작업 UPID.</summary>
    public string UpId { get; init; } = string.Empty;
}

/// <summary>POST /nodes/{node}/{lxc|qemu}/{vmid}/termproxy 결과 — 서버 PTY 에 붙는 터미널 웹소켓 접속 정보.</summary>
public sealed class TermProxyInfo
{
    /// <summary>노드 측 termproxy 포트.</summary>
    public int Port { get; init; }

    /// <summary>터미널 티켓 — 웹소켓 쿼리와 첫 인증 메시지("user:ticket\n")에 사용.</summary>
    public string Ticket { get; init; } = string.Empty;

    /// <summary>티켓이 발급된 사용자(user@realm).</summary>
    public string User { get; init; } = string.Empty;

    public string UpId { get; init; } = string.Empty;
}

/// <summary>POST /nodes/{node}/qemu/{vmid}/spiceproxy 결과 — .vv 파일 생성용.</summary>
public sealed class SpiceProxyInfo
{
    public string Host { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public int? TlsPort { get; init; }
    public int? SecurePort { get; init; }
    public string HostSubject { get; init; } = string.Empty;
    public string Proxy { get; init; } = string.Empty;
    public string ReleaseCursor { get; init; } = string.Empty;
    public string ToggleFullscreen { get; init; } = string.Empty;

    /// <summary>
    ///     서버가 돌려준 전체 설정(키=값). Proxmox 웹 UI 와 같이 모두 .vv 에 기록한다 — 특히 <c>ca</c>(자체 서명 CA 인증서)가
    ///     빠지면 remote-viewer 가 TLS 검증에 실패해 연결하지 못한다.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Settings { get; init; } = [];

    /// <summary>remote-viewer(virt-viewer)용 .vv 파일 내용 생성.</summary>
    public string ToVvFile(string title)
    {
        if (Settings.Count > 0) return BuildFromSettings(title);

        var sb = new StringBuilder();
        sb.AppendLine("[virt-viewer]");
        sb.AppendLine("type=spice");
        sb.AppendLine($"host={Host}");
        if (TlsPort is { } tls) sb.AppendLine($"tls-port={tls}");
        if (SecurePort is { } secure && secure != TlsPort) sb.AppendLine($"secure-port={secure}");
        sb.AppendLine($"password={Password}");
        if (!string.IsNullOrEmpty(HostSubject)) sb.AppendLine($"host-subject={HostSubject}");
        if (!string.IsNullOrEmpty(Proxy)) sb.AppendLine($"proxy={Proxy}");
        sb.AppendLine("delete-this-file=1");
        if (!string.IsNullOrEmpty(ReleaseCursor)) sb.AppendLine($"release-cursor={ReleaseCursor}");
        if (!string.IsNullOrEmpty(ToggleFullscreen)) sb.AppendLine($"toggle-fullscreen={ToggleFullscreen}");
        sb.AppendLine($"title={title}");
        sb.AppendLine("fullscreen=0");
        return sb.ToString();
    }

    private string BuildFromSettings(string title)
    {
        var sb = new StringBuilder();
        sb.Append("[virt-viewer]\n");
        foreach (var (key, value) in Settings)
        {
            if (key is "title" or "delete-this-file") continue; // 아래에서 창 제목·삭제 지시를 직접 지정

            // .vv(ini) 는 한 줄 값만 허용 — 여러 줄(CA 인증서 PEM 등)은 virt-viewer 규약대로 "\n" 문자열로 이스케이프
            sb.Append(key).Append('=').Append(value.Replace("\r", string.Empty).Replace("\n", "\\n")).Append('\n');
        }

        sb.Append("title=").Append(title.Replace("\r", string.Empty).Replace('\n', ' ')).Append('\n');
        sb.Append("delete-this-file=1\n"); // 비밀번호가 들어 있으므로 remote-viewer 가 읽은 뒤 지우게 한다
        return sb.ToString();
    }
}