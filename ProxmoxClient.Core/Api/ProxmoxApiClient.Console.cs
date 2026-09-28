using System.Text.Json;
using ProxmoxClient.Core.Localization;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.Core.Api;

/// <summary>콘솔 연결 준비 — VNC·터미널·SPICE 프록시.</summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>
    ///     Starts a VNC console proxy (POST nodes/{node}/qemu/{vmid}/vncproxy).
    ///     Returns port + ticket used to open the vncwebsocket console channel.
    ///     Requires password (ticket) authentication.
    /// </summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/vncproxy")]
    [Versioning.PveApi("POST", "/nodes/{node}/lxc/{vmid}/vncproxy")]
    public async Task<VncProxyInfo> CreateVncProxyAsync(string node, ResourceKind kind, int vmid,
        CancellationToken ct = default)
    {
        if (Profile.AuthMode == AuthMode.ApiToken || _auth is null)
            throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_05"));

        var data = await PostFormJsonAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/vncproxy",
            new Dictionary<string, string>(),
            true,
            ct).ConfigureAwait(false);

        return new VncProxyInfo
        {
            Port = GetInt(data, "port"),
            Ticket = GetString(data, "ticket"),
            UpId = GetString(data, "upid")
        };
    }
    /// <summary>
    ///     터미널 프록시 시작 (POST nodes/{node}/{lxc|qemu}/{vmid}/termproxy).
    ///     서버가 게스트 콘솔에 PTY 를 붙이고, 반환된 포트·티켓으로 vncwebsocket 에 연결한다.
    /// </summary>
    [Versioning.PveApi("POST", "/nodes/{node}/termproxy")]
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/termproxy")]
    [Versioning.PveApi("POST", "/nodes/{node}/lxc/{vmid}/termproxy")]
    public Task<TermProxyInfo> CreateTermProxyAsync(string node, ResourceKind kind, int vmid,
        CancellationToken ct = default)
    {
        return CreateTermProxyAsync(ConsoleTarget.ForGuest(node, kind, vmid), ct);
    }
    /// <summary>터미널 프록시를 연다(POST {target}/termproxy). 노드 대상이면 노드 셸이 열린다.</summary>
    [Versioning.PveApi("POST", "/nodes/{node}/termproxy")]
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/termproxy")]
    [Versioning.PveApi("POST", "/nodes/{node}/lxc/{vmid}/termproxy")]
    public async Task<TermProxyInfo> CreateTermProxyAsync(ConsoleTarget target, CancellationToken ct = default)
    {
        if (Profile.AuthMode == AuthMode.ApiToken || _auth is null)
            throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_06"));

        var form = new Dictionary<string, string>();
        if (target.Command is { } command) form["cmd"] = command;

        var data = await PostFormJsonAsync($"{target.BasePath}/termproxy", form, true, ct).ConfigureAwait(false);

        return new TermProxyInfo
        {
            Port = GetInt(data, "port"),
            Ticket = GetString(data, "ticket"),
            User = GetString(data, "user"),
            UpId = GetString(data, "upid")
        };
    }
    /// <summary>
    ///     Gets the SPICE proxy connection settings (POST nodes/{node}/qemu/{vmid}/spiceproxy)
    ///     used to build a .vv file for remote-viewer. QEMU VMs only.
    /// </summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/spiceproxy")]
    public async Task<SpiceProxyInfo> GetSpiceProxyAsync(string node, int vmid, CancellationToken ct = default)
    {
        // proxy: 클라이언트가 접속할 spiceproxy 주소. 생략하면 서버가 노드 이름을 돌려주는데,
        // 이 PC 에서 그 이름이 해석되지 않으면 연결에 실패하므로 웹 UI 처럼 접속 중인 서버 주소를 보낸다.
        var data = await PostFormJsonAsync(
            $"nodes/{Escape(node)}/qemu/{vmid}/spiceproxy",
            new Dictionary<string, string> { ["proxy"] = Profile.Host },
            true,
            ct).ConfigureAwait(false);

        if (data.ValueKind != JsonValueKind.Object)
            throw new ProxmoxApiException(0, null, Res.T("ProxmoxApiClient_07"));

        var settings = new List<KeyValuePair<string, string>>();
        foreach (var property in data.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => null
            };
            if (value is not null) settings.Add(new KeyValuePair<string, string>(property.Name, value));
        }

        return new SpiceProxyInfo
        {
            Settings = settings,
            Host = GetString(data, "host"),
            Password = GetString(data, "password"),
            TlsPort = GetIntOrNull(data, "tls-port"),
            SecurePort = GetIntOrNull(data, "secure-port"),
            HostSubject = GetString(data, "host-subject"),
            Proxy = GetString(data, "proxy"),
            ReleaseCursor = GetString(data, "release-cursor"),
            ToggleFullscreen = GetString(data, "toggle-fullscreen")
        };
    }
    private static int? GetIntOrNull(in JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var el)
               && el.ValueKind is JsonValueKind.Number or JsonValueKind.String
               && int.TryParse(el.ToString(), out var value)
            ? value
            : null;
    }
}
