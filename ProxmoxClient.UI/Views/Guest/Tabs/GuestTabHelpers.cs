using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Guest.Tabs;

/// <summary>입력 칸 한 줄 — 위에 라벨, 아래에 입력, 필요하면 작은 안내(WPF FormRow 대신 세로 배치: 412px 에서도 잘리지 않게).</summary>
internal static class GuestFormRow
{
    public static StackPanel Create(string labelKey, Control input, string? hintKey = null)
    {
        var row = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = Loc.T(labelKey), Classes = { "label" } },
                input
            }
        };
        if (hintKey is not null)
            row.Children.Add(new TextBlock
            {
                Text = Loc.T(hintKey), Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap, FontSize = 12
            });
        return row;
    }
}

/// <summary>
///     알약 모양 하위 탭 — 누른 칸의 화면만 보인다(처음 누를 때 만든다). 공용 SubTabsView 가 아직 없어 게스트 탭 안에서만 쓴다.
/// </summary>
internal sealed class GuestChipTabs : UserControl
{
    public GuestChipTabs(params (string LabelKey, Func<Control> Create)[] tabs)
    {
        var group = "guest-chip-tabs-" + Guid.NewGuid().ToString("N");
        var created = new Control?[tabs.Length];
        var host = new ContentControl();

        void Show(int index)
        {
            created[index] ??= tabs[index].Create();
            host.Content = created[index];
        }

        var chips = new WrapPanel { Margin = new Thickness(12, 12, 12, 8) };
        for (var i = 0; i < tabs.Length; i++)
        {
            var index = i;
            var chip = new RadioButton
            {
                Content = Loc.T(tabs[i].LabelKey), GroupName = group, Classes = { "chip" }, IsChecked = i == 0
            };
            chip.IsCheckedChanged += (_, _) =>
            {
                if (chip.IsChecked == true) Show(index);
            };
            chips.Children.Add(chip);
        }

        if (tabs.Length > 0) Show(0);

        var layout = new DockPanel();
        DockPanel.SetDock(chips, Dock.Top);
        layout.Children.Add(chips);
        layout.Children.Add(host);
        Content = layout;
    }
}

/// <summary>
///     digest 로 지키는 쓰기 — 읽은 뒤 다른 곳에서 설정이 바뀌어 서버가 거절하면(<see cref="ProxmoxApiException.IsDigestMismatch" />)
///     실패 대신 "다시 불러왔으니 확인 후 다시 하라" 는 문구를 돌려준다. 표(TableTab)는 문구가 오면 목록을 다시 읽는다.
/// </summary>
internal static class DigestGuard
{
    /// <summary>행(또는 설정)에 든 digest. 없으면 null — 그때는 보내지 않는다.</summary>
    public static string? Of(IReadOnlyDictionary<string, string>? row)
    {
        return row is not null && row.TryGetValue("digest", out var digest) && digest.Length > 0 ? digest : null;
    }

    public static async Task<string?> RunAsync(Func<Task<string?>> work)
    {
        try
        {
            return await work();
        }
        catch (ProxmoxApiException ex) when (ex.IsDigestMismatch)
        {
            return Loc.T("Digest_Changed");
        }
    }

    /// <summary>표 단추의 실행을 감싼다(확인 문구 등 나머지는 그대로).</summary>
    public static TableAction Wrap(TableAction action)
    {
        var run = action.Run;
        return action with { Run = (row, owner) => RunAsync(() => run(row, owner)) };
    }
}
