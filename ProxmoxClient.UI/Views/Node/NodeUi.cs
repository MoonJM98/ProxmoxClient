using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Views.Node;

/// <summary>노드 화면들이 함께 쓰는 도우미 — 아이콘 찾기와 아직 옮기지 않은 터미널 안내.</summary>
internal static class NodeUi
{
    /// <summary>
    ///     터미널(노드 셸·설치 마법사·업그레이드)은 아직 옮기지 않았다. 셸을 여는 버튼은 이 안내만 보여 준다.
    /// </summary>
    internal static string TerminalPending => Loc.T("Node_TerminalPending");

    internal static Geometry? Icon(string key)
    {
        return Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            ? value as Geometry
            : null;
    }

    /// <summary>
    ///     공용 글 편집 화면(<see cref="TextEditTab" />)의 편집 칸은 세로 가운데에 글을 둬서 WPF(위쪽 정렬)와 다르다.
    ///     공용 파일은 고칠 수 없으므로, 붙은 뒤에 이 화면 안의 편집 칸만 위쪽으로 맞춘다.
    /// </summary>
    internal static T TopAligned<T>(T view) where T : Control
    {
        // 만들 때 바로 바꾼다 — 글 칸은 만든 뒤 논리 트리에 이미 있으므로 화면에 붙기 전에도 찾을 수 있다
        foreach (var box in view.GetLogicalDescendants().OfType<TextBox>())
            box.VerticalContentAlignment = VerticalAlignment.Top;
        return view;
    }
}
