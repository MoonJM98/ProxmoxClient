using System.Windows.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App;

/// <summary>
///     값(Tag)과 표시 라벨을 분리한 ComboBox 선택지 도우미. Proxmox 고정 선택지 목록은 공용 <see cref="ChoiceLists" />.
///     라벨에는 리소스 키를 넣고 <see cref="Fill" /> 이 채울 때 번역한다(정적 목록이라 미리 번역하면 언어 전환이 반영되지 않는다).
///     제품명처럼 번역이 필요 없는 라벨은 키가 없으므로 원문 그대로 표시된다.
/// </summary>
internal static class ComboChoices
{
    public static void Fill(ComboBox box, IEnumerable<(string Value, string Label)> items)
    {
        box.Items.Clear();
        foreach (var (value, label) in items)
            // 키가 없는 라벨(제품명 등)은 Loc.T 가 원문을 그대로 돌려준다
            box.Items.Add(new ComboBoxItem { Content = Loc.T(label), Tag = value });
    }

    /// <summary>값으로 선택. 목록에 없는 값(서버에만 존재)은 원문 그대로 항목을 추가해 보존한다.</summary>
    public static void Select(ComboBox box, string value)
    {
        var match = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, value, StringComparison.OrdinalIgnoreCase));
        if (match is null && value.Length > 0)
        {
            match = new ComboBoxItem { Content = value, Tag = value };
            box.Items.Add(match);
        }

        box.SelectedItem = match ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    public static string Selected(ComboBox box)
    {
        return (box.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
    }
}