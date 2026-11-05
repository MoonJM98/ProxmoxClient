using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     화면 낭독기(TalkBack·VoiceOver·내레이터)가 읽을 이름 — 아이콘만 있는 단추는 글자가 없어 아무것도 읽지 못한다.
///     툴팁(길게 누르면 뜨는 이름)을 단 컨트롤은 따로 이름을 정하지 않았으면 그 툴팁을 이름으로 쓴다.
/// </summary>
public static class AccessibleNames
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;

        _installed = true;
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not string tip || tip.Length == 0) return;
            var current = AutomationProperties.GetName(control);
            // 이전 툴팁에서 따라 정한 이름이면 새 툴팁으로 바꾼다(직접 정한 이름은 그대로)
            if (string.IsNullOrEmpty(current) || current == e.OldValue as string) AutomationProperties.SetName(control, tip);
        });
        // 글자 없는 스위치·체크 칸(옆의 글자가 이름 노릇) — 같은 줄(부모·조부모)의 첫 글자를 이름으로
        Control.LoadedEvent.AddClassHandler<ToggleButton>((toggle, _) => NameFromLabel(toggle), RoutingStrategies.Direct);
    }

    private static void NameFromLabel(ToggleButton toggle)
    {
        if (!string.IsNullOrEmpty(AutomationProperties.GetName(toggle)) || toggle.Content is string { Length: > 0 }) return;

        // 표 칸의 체크(예: 템플릿 열) — 열 머리글이 이름
        if (DataGridColumn.GetColumnContainingElement(toggle)?.Header is string { Length: > 0 } header)
        {
            AutomationProperties.SetName(toggle, header);
            return;
        }

        foreach (var container in toggle.GetVisualAncestors().Take(2).OfType<Panel>())
        {
            var label = container.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text) && !t.GetVisualAncestors().Contains(toggle));
            if (label is null) continue;

            AutomationProperties.SetName(toggle, label.Text);
            return;
        }
    }
}
