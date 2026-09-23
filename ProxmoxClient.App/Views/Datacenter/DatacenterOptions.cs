using ProxmoxClient.App.Views.Guest.Tabs;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     데이터센터 옵션(cluster/options) — 웹 UI 의 Datacenter › Options 에 해당한다.
///     "type=secure,network=…" 같은 복합 값은 서버 형식 그대로 고친다.
/// </summary>
internal static class DatacenterOptions
{
    private static readonly GuestOption[] All =
    [
        new()
        {
            Key = "keyboard", LabelKey = "DcOptions_Keyboard", Kind = OptionKind.Choice,
            EmptyLabelKey = "DcOptions_Default",
            Choices =
            [
                ("", "DcOptions_Default"), ("en-us", "English (US)"), ("ko", "한국어"), ("ja", "日本語"),
                ("de", "Deutsch"), ("fr", "Français"), ("en-gb", "English (UK)")
            ]
        },
        new()
        {
            Key = "console", LabelKey = "DcOptions_Console", Kind = OptionKind.Choice,
            EmptyLabelKey = "DcOptions_Default",
            Choices = [("", "DcOptions_Default"), ("html5", "noVNC"), ("vv", "SPICE"), ("xtermjs", "xterm.js")]
        },
        new()
        {
            Key = "http_proxy", LabelKey = "DcOptions_HttpProxy", Kind = OptionKind.Text,
            EmptyLabelKey = "GuestOptions_NotSet"
        },
        new()
        {
            Key = "email_from", LabelKey = "DcOptions_EmailFrom", Kind = OptionKind.Text,
            EmptyLabelKey = "DcOptions_Default"
        },
        new()
        {
            Key = "mac_prefix", LabelKey = "DcOptions_MacPrefix", Kind = OptionKind.Text,
            EmptyLabelKey = "DcOptions_Default"
        },
        new()
        {
            Key = "migration", LabelKey = "DcOptions_Migration", Kind = OptionKind.Text,
            EmptyLabelKey = "DcOptions_Default"
        },
        new()
        {
            Key = "bwlimit", LabelKey = "DcOptions_Bwlimit", Kind = OptionKind.Text,
            EmptyLabelKey = "DcOptions_Default"
        },
        new()
        {
            Key = "max_workers", LabelKey = "DcOptions_MaxWorkers", Kind = OptionKind.Text,
            EmptyLabelKey = "DcOptions_Default"
        },
        new()
        {
            Key = "next-id", LabelKey = "DcOptions_NextId", Kind = OptionKind.Text,
            EmptyLabelKey = "DcOptions_Default"
        },
        new()
        {
            Key = "ha", LabelKey = "DcOptions_Ha", Kind = OptionKind.Text,
            EmptyLabelKey = "DcOptions_Default"
        },
        new()
        {
            Key = "tag-style", LabelKey = "DcOptions_TagStyle", Kind = OptionKind.Text,
            EmptyLabelKey = "DcOptions_Default"
        },
        new()
        {
            Key = "description", LabelKey = "Table_Description", Kind = OptionKind.Text,
            EmptyLabelKey = "GuestOptions_NotSet"
        }
    ];

    public static OptionsTab Create(ProxmoxApiClient api)
    {
        // 비운 값은 서버에서 지워 기본값으로 돌린다(빈 문자열을 그대로 보내면 형식 오류가 나는 항목이 있다)
        return new OptionsTab(All, () => api.GetObjectAsync("cluster/options"),
            async changes => await api.PutActionAsync("cluster/options", UpdateForm(changes)));
    }
}
