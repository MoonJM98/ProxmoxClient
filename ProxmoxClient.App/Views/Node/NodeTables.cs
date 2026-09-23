using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Node;

/// <summary>노드 창의 표 화면들 — 서버 경로와 보여 줄 열만 정의한다.</summary>
internal static class NodeTables
{
    private static readonly IReadOnlyList<TableColumn> NetworkColumns =
    [
        new() { Key = "iface", HeaderKey = "Table_Name", Width = 110 },
        new() { Key = "type", HeaderKey = "Table_Type", Width = 90 },
        new() { Key = "active", HeaderKey = "Table_Active", Width = 60, Format = TableFormats.Flag },
        new() { Key = "autostart", HeaderKey = "Table_Autostart", Width = 70, Format = TableFormats.Flag },
        new() { Key = "bridge_ports", HeaderKey = "Table_Ports", Width = 110 },
        new() { Key = "cidr", HeaderKey = "Table_Cidr", Width = 140 },
        new() { Key = "gateway", HeaderKey = "Table_Gateway", Width = 120 },
        new() { Key = "comments", HeaderKey = "Table_Comment", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> CertificateColumns =
    [
        new() { Key = "filename", HeaderKey = "Table_File", Width = 140 },
        new() { Key = "issuer", HeaderKey = "Table_Issuer", Width = 200 },
        new() { Key = "notafter", HeaderKey = "Table_Expires", Width = 140, Format = TableFormats.EpochDate },
        new() { Key = "san", HeaderKey = "Table_San", Width = 0 },
        new() { Key = "fingerprint", HeaderKey = "Table_Fingerprint", Width = 200 }
    ];

    public static TableTab Network(ProxmoxApiClient api, string node, bool canEdit)
    {
        return new TableTab(() => api.GetTableAsync(NodePath(node, "network")), NetworkColumns, "NodeNetwork_Hint",
            canEdit ? NetworkActions.Actions(api, node) : null);
    }

    public static TableTab Certificates(ProxmoxApiClient api, string node, bool canEdit)
    {
        return new TableTab(() => api.GetTableAsync(NodePath(node, "certificates/info")), CertificateColumns,
            "NodeCertificates_Hint", canEdit ? CertificateActions.Actions(api, node) : null);
    }

    private static string NodePath(string node, string section)
    {
        return $"nodes/{ProxmoxApiClient.PathSegment(node)}/{section}";
    }
}
