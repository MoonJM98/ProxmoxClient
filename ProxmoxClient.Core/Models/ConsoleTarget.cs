namespace ProxmoxClient.Core.Models;

/// <summary>
///     콘솔을 여는 대상 — 게스트(CT/VM)와 노드 셸은 같은 termproxy/vncwebsocket API 를 경로만 달리해 쓴다.
/// </summary>
public sealed class ConsoleTarget
{
    private ConsoleTarget(string basePath, string displayName, string? command = null)
    {
        BasePath = basePath;
        DisplayName = displayName;
        Command = command;
    }

    /// <summary>termproxy·vncwebsocket 앞까지의 API 상대 경로(이미 이스케이프됨).</summary>
    public string BasePath { get; }

    /// <summary>로그에 남길 짧은 이름.</summary>
    public string DisplayName { get; }

    /// <summary>노드 셸에서 로그인 대신 실행할 명령(termproxy 의 cmd — 예: "upgrade"). 없으면 로그인 셸.</summary>
    public string? Command { get; }

    public static ConsoleTarget ForGuest(string node, ResourceKind kind, int vmid)
    {
        return new ConsoleTarget($"nodes/{Uri.EscapeDataString(node)}/{kind.ApiSegment()}/{vmid}", vmid.ToString());
    }

    public static ConsoleTarget ForNode(string node, string? command = null)
    {
        return new ConsoleTarget($"nodes/{Uri.EscapeDataString(node)}", node, command);
    }
}
