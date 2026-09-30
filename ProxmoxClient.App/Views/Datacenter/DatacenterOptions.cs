using ProxmoxClient.App.Views.Guest.Tabs;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     데이터센터 옵션(cluster/options) — 웹 UI 의 Datacenter › Options 에 해당한다.
///     복합 값(이전 네트워크·HA·다음 ID 등)은 칸으로 나눈 편집기(<see cref="DatacenterOptionEditors" />)로 고친다.
/// </summary>
internal static class DatacenterOptions
{
    /// <summary>서버가 받는 키보드 배치(PVE::Tools::kvmkeymaplist) — 목록에 없는 값은 저장이 거절된다.</summary>
    private static readonly (string, string)[] Keymaps =
    [
        ("", "DcOptions_Default"),
        ("da", "da"), ("de", "de"), ("de-ch", "de-ch"), ("en-gb", "en-gb"), ("en-us", "en-us"), ("es", "es"),
        ("fi", "fi"), ("fr", "fr"), ("fr-be", "fr-be"), ("fr-ca", "fr-ca"), ("fr-ch", "fr-ch"), ("hu", "hu"),
        ("is", "is"), ("it", "it"), ("ja", "ja"), ("lt", "lt"), ("mk", "mk"), ("nl", "nl"), ("no", "no"),
        ("pl", "pl"), ("pt", "pt"), ("pt-br", "pt-br"), ("sl", "sl"), ("sv", "sv"), ("tr", "tr")
    ];

    /// <summary>웹 UI Options 목록 순서.</summary>
    private static readonly GuestOption[] All =
    [
        new()
        {
            Key = "keyboard", LabelKey = "DcOptions_Keyboard", Kind = OptionKind.Choice,
            EmptyLabelKey = "DcOptions_Default", Choices = Keymaps
        },
        Row("http_proxy", "DcOptions_HttpProxy", DatacenterOptionEditors.HttpProxy(), "GuestOptions_NotSet"),
        new()
        {
            Key = "console", LabelKey = "DcOptions_Console", Kind = OptionKind.Choice,
            EmptyLabelKey = "DcOpt_ConsoleDefault",
            Choices = [("", "DcOpt_ConsoleDefault"), ("html5", "noVNC"), ("vv", "SPICE"), ("xtermjs", "xterm.js")]
        },
        Row("email_from", "DcOptions_EmailFrom", DatacenterOptionEditors.EmailFrom()),
        Row("mac_prefix", "DcOptions_MacPrefix", DatacenterOptionEditors.MacPrefix()),
        Row("migration", "DcOptions_Migration",
            DatacenterOptionEditors.MigrationLike("migration", "DcOptions_Migration")),
        Row("replication", "DcOpt_Replication",
            DatacenterOptionEditors.MigrationLike("replication", "DcOpt_Replication")),
        Row("ha", "DcOptions_Ha", DatacenterOptionEditors.Ha()),
        Row("crs", "DcOpt_Crs", DatacenterOptionEditors.Crs()),
        Row("u2f", "DcOpt_U2f", DatacenterOptionEditors.U2f(), "GuestOptions_NotSet"),
        Row("webauthn", "DcOpt_WebAuthn", DatacenterOptionEditors.WebAuthn(), "GuestOptions_NotSet"),
        Row("bwlimit", "DcOptions_Bwlimit", DatacenterOptionEditors.Bwlimit(), "DcOpt_NoLimit"),
        Row("max_workers", "DcOptions_MaxWorkers", DatacenterOptionEditors.MaxWorkers()),
        Row("next-id", "DcOptions_NextId", DatacenterOptionEditors.NextId()),
        Row("tag-style", "DcOptions_TagStyle", DatacenterOptionEditors.TagStyle()),
        Row("user-tag-access", "DcOpt_UserTagAccess", DatacenterOptionEditors.UserTagAccess()),
        Row("registered-tags", "DcOpt_RegisteredTags", DatacenterOptionEditors.RegisteredTags(), "DcOpt_NoTags"),
        Row("consent-text", "DcOpt_ConsentText", DatacenterOptionEditors.ConsentText(), "GuestOptions_NotSet"),
        Row("description", "Table_Description", null, "GuestOptions_NotSet")
    ];

    private static GuestOption Row(string key, string labelKey, OptionEditor? editor,
        string emptyKey = "DcOptions_Default")
    {
        return new GuestOption { Key = key, LabelKey = labelKey, Editor = editor, EmptyLabelKey = emptyKey };
    }

    /// <summary>GET cluster/options 가 객체로 돌려주는 키(parse_datacenter_config).</summary>
    private static readonly HashSet<string> ObjectKeys =
    [
        "migration", "replication", "ha", "crs", "next-id", "u2f", "webauthn", "tag-style", "user-tag-access",
        "notify"
    ];

    public static OptionsTab Create(ProxmoxApiClient api)
    {
        // 비운 값은 서버에서 지워 기본값으로 돌린다(빈 문자열을 그대로 보내면 형식 오류가 나는 항목이 있다).
        // 서버가 모르는 옵션(예: 9.0 전의 replication)은 옵션 목록이 저장 요청(target)을 보고 뺀다
        return new OptionsTab(All, async () => FromServer(await api.Cluster.GetOptionsAsync()),
            async changes => await api.Cluster.UpdateOptionsAsync(UpdateForm(changes)),
            api.Cluster.Feature(nameof(ClusterApi.UpdateOptionsAsync)));
    }

    /// <summary>GET 이 객체로 돌려준 값(migration·ha·next-id 등)을 PUT 형식(key=value,...)으로 되돌린다.</summary>
    private static IReadOnlyDictionary<string, string> FromServer(IReadOnlyDictionary<string, string> config)
    {
        return config.ToDictionary(kv => kv.Key,
            kv => ObjectKeys.Contains(kv.Key) ? PropertyString.FromJsonObject(kv.Value) : kv.Value,
            StringComparer.Ordinal);
    }
}
