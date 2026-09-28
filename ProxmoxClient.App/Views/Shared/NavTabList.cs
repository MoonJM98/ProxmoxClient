using System.Windows;
using ProxmoxClient.Core.Api.Versioning;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     탐색 창 탭 목록 — 권한(visible)이 없으면 넣지 않고, 쓰는 API 기능(requires)은 탭에 적어 두어 서버가 못 쓰면
///     탐색 창이 뺀다. 노드·데이터센터 창이 탭을 묶음별로 나눠 넣을 때 쓴다.
/// </summary>
internal sealed class NavTabList : List<NavTab>
{
    public void Add(string id, string labelKey, string iconKey, bool visible, Func<UIElement> create,
        ApiFeature? requires = null)
    {
        if (visible)
            Add(new NavTab { Id = id, LabelKey = labelKey, IconKey = iconKey, Create = create, Requires = requires });
    }
}
