using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Node;

/// <summary>
///     노드 관리 영역의 등록. 실제 진입점은 노드 상세의 "노드 관리" 항목(<see cref="FeatureRegistry.Node" />)이며
///     <see cref="NodeNavigator.Create" /> 로 탐색 화면을 연다. 탭마다 검증용 미리보기도 등록한다
///     (tools/UiSnapshot, target "preview:node") — 미리보기는 모든 권한을 허용으로 본다.
/// </summary>
internal static class NodeModule
{
    // 모듈 초기화기로 등록한다(porting-guide 5항). CA2255 는 라이브러리 사용 경고지만, 이 등록은 의도한 것이다.
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "진입점·검증 등록은 모듈 초기화에서 하도록 정해진 방식(porting-guide 5항)")]
    internal static void Register()
    {
        FeatureRegistry.Node = (session, node) => NodeNavigator.Create(session.Api, node.Node, session.Permissions);

        const string area = "node";
        Add(area, "summary", "GuestTab_Summary", "IconList");
        Add(area, "notes", "NodeTab_Notes", "IconPencil");
        Add(area, "shell", "NodeTab_Shell", "IconTerminal");
        Add(area, "network", "NodeTab_Network", "IconSwitch");
        Add(area, "certificates", "NodeTab_Certificates", "IconShield");
        Add(area, "dns", "NodeTab_Dns", "IconServer");
        Add(area, "hosts", "NodeTab_Hosts", "IconList");
        Add(area, "options", "GuestTab_Options", "IconSettings");
        Add(area, "time", "NodeTab_Time", "IconRotate");
        Add(area, "services", "NodeTab_Services", "IconSettings");
        Add(area, "syslog", "NodeTab_Syslog", "IconList");
        Add(area, "updates", "NodeTab_Updates", "IconDownload");
        Add(area, "firewall", "GuestTab_Firewall", "IconShield");
        Add(area, "disks", "NodeTab_Disks", "IconDatabase");
        Add(area, "ceph", "DcTab_Ceph", "IconDatabase");
        Add(area, "sdn", "DcTab_Sdn", "IconSwitch");
        Add(area, "replication", "DcTab_Replication", "IconCopy");
        Add(area, "tasks", "GuestTab_Tasks", "IconArchive");
        Add(area, "subscription", "NodeTab_Subscription", "IconCheck");
    }

    private static void Add(string area, string id, string labelKey, string iconKey)
    {
        PreviewRegistry.Add(area, new PreviewTab(id, labelKey, iconKey, ctx => Tab(ctx, id)));
    }

    /// <summary>미리보기 대상 노드(pve1)의 탭 하나 — 탐색 창과 같은 목록에서 고른다.</summary>
    private static Control Tab(PreviewContext ctx, string id)
    {
        return NodeNavigator.Tabs(ctx.Api, ctx.Node.Node, PermissionsInfo.Admin).First(t => t.Id == id).Create();
    }
}
