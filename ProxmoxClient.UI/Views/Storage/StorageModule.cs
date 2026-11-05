using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Storage;

/// <summary>
///     저장소 영역의 진입점 등록 — 스토리지 목록의 행을 누르면 저장소 탐색 창(<see cref="StorageNavigator" />)이 열린다.
///     검증용 미리보기(tools/UiSnapshot, target "preview:storage")는 로컬(pve1) 저장소의 탭을 연다.
/// </summary>
internal static class StorageModule
{
    /// <summary>미리보기에서 모든 콘텐츠 탭을 보이게 하는 목록(실제 창은 저장소가 가진 콘텐츠만 보인다).</summary>
    private const string PreviewContent = "backup,iso,vztmpl,import,images,rootdir";

    // 가이드가 정한 등록 방식(진입점·검증 등록부)이라 라이브러리 경고(CA2255)는 의도적으로 끈다
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "진입점·검증 등록부 등록 — porting-guide 의 등록 방식")]
    internal static void Register()
    {
        FeatureRegistry.Storage = (session, storage) => StorageNavigator.Create(session.Api, storage.Node,
            storage.Storage, storage.Content, session.Permissions,
            storage.PluginType is { Length: > 0 } type ? type : null);

        const string area = "storage";
        PreviewRegistry.Add(area, new PreviewTab("summary", "GuestTab_Summary", "IconList", ctx => Preview(ctx, "summary")));
        PreviewRegistry.Add(area, new PreviewTab("graph", "StorageGraph_Tab", "IconSwitch", ctx => Preview(ctx, "graph")));
        PreviewRegistry.Add(area, new PreviewTab("backup", "GuestTab_Backup", "IconArchive", ctx => Preview(ctx, "backup")));
        PreviewRegistry.Add(area, new PreviewTab("iso", "StorageContent_Iso", "IconDownload", ctx => Preview(ctx, "iso")));
        PreviewRegistry.Add(area, new PreviewTab("vztmpl", "StorageContent_Templates", "IconBox", ctx => Preview(ctx, "vztmpl")));
        PreviewRegistry.Add(area, new PreviewTab("import", "StorageContent_ImportTab", "IconDownload", ctx => Preview(ctx, "import")));
        PreviewRegistry.Add(area, new PreviewTab("images", "StorageContent_Images", "IconDatabase", ctx => Preview(ctx, "images")));
    }

    /// <summary>미리보기 탭 하나 — 저장소 창과 같은 탭 정의를 쓰므로 내용·버튼이 같다.</summary>
    private static Control Preview(PreviewContext ctx, string id)
    {
        var tabs = StorageNavigator.Tabs(ctx.Api, ctx.Storage.Node, ctx.Storage.Storage, PreviewContent,
            ctx.Permissions, ctx.Storage.PluginType);
        return tabs.First(t => t.Id == id).Create();
    }
}
