using Avalonia.Controls;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;
using static ProxmoxClient.Presentation.Shared.ActionHelpers;
using static ProxmoxClient.Presentation.Shared.FormHelpers;

namespace ProxmoxClient.UI.Views.Datacenter;

/// <summary>옵션 항목을 어떤 입력으로 고치는지(목록 값 고르기 또는 글자).</summary>
internal enum DcOptionKind
{
    Text,
    Choice
}

/// <summary>
///     데이터센터 옵션 목록 한 줄 — 서버 키, 이름 리소스, 고르기 목록(또는 글자), 여러 칸 편집기.
///     WPF 의 GuestOption 과 같은 뜻이며, 게스트 옵션 화면이 옮겨지면 그쪽 모델과 합칠 수 있다.
/// </summary>
internal sealed class DcOption
{
    public required string Key { get; init; }

    public required string LabelKey { get; init; }

    public DcOptionKind Kind { get; init; } = DcOptionKind.Text;

    /// <summary>고르기 목록(값, 표시 리소스 키 또는 원문).</summary>
    public (string Value, string Label)[]? Choices { get; init; }

    /// <summary>비었을 때 보여 줄 설명 리소스 키.</summary>
    public string? EmptyLabelKey { get; init; }

    /// <summary>여러 칸으로 나눠 고치는 항목 — 있으면 Kind 대신 이 편집기(게스트 옵션과 같은 공용 형식)를 쓴다.</summary>
    public OptionEditor? Editor { get; init; }

    public string Label => Loc.T(LabelKey);
}

/// <summary>
///     데이터센터 옵션(cluster/options) — 웹 UI 의 Datacenter › Options 에 해당한다.
///     목록(이름·값)에서 고르면 대화상자로 고친다. 복합 값(이전 네트워크·HA·다음 ID 등)은
///     <see cref="DatacenterOptionEditors" /> 의 칸 편집기로 고친다.
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
    private static readonly DcOption[] All =
    [
        new()
        {
            Key = "keyboard", LabelKey = "DcOptions_Keyboard", Kind = DcOptionKind.Choice,
            EmptyLabelKey = "DcOptions_Default", Choices = Keymaps
        },
        Row("http_proxy", "DcOptions_HttpProxy", DatacenterOptionEditors.HttpProxy(), "GuestOptions_NotSet"),
        new()
        {
            Key = "console", LabelKey = "DcOptions_Console", Kind = DcOptionKind.Choice,
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

    /// <summary>GET cluster/options 가 객체로 돌려주는 키(parse_datacenter_config).</summary>
    private static readonly HashSet<string> ObjectKeys =
    [
        "migration", "replication", "ha", "crs", "next-id", "u2f", "webauthn", "tag-style", "user-tag-access",
        "notify"
    ];

    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "label", HeaderKey = "Table_Name", Width = 220 },
        new() { Key = "value", HeaderKey = "Table_Value", Width = 0 }
    ];

    /// <summary>
    ///     옵션 목록 화면. 비운 값은 서버에서 지워 기본값으로 돌린다(빈 문자열을 그대로 보내면 형식 오류가 나는 항목이 있다).
    ///     서버가 모르는 옵션(예: 9.0 전의 replication)은 저장 요청(target)을 보고 목록에서 뺀다.
    /// </summary>
    public static Control Create(ProxmoxApiClient api)
    {
        var target = api.Cluster.Feature(nameof(ClusterApi.UpdateOptionsAsync));
        var options = All.Where(o => target.Accepts(o.Key)).ToList();
        return new TableTab(() => RowsAsync(api, options), Columns, "DcOptions_Hint",
            [EditAction(api, options)], filterable: true);
    }

    private static DcOption Row(string key, string labelKey, OptionEditor? editor,
        string emptyKey = "DcOptions_Default")
    {
        return new DcOption { Key = key, LabelKey = labelKey, Editor = editor, EmptyLabelKey = emptyKey };
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> RowsAsync(ProxmoxApiClient api,
        IReadOnlyList<DcOption> options)
    {
        var config = FromServer(await api.Cluster.GetOptionsAsync());
        return options.Select(o => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key"] = o.Key,
            ["label"] = o.Label,
            ["value"] = Render(o, config.GetValueOrDefault(o.Key, string.Empty))
        }).ToList();
    }

    private static TableAction EditAction(ProxmoxApiClient api, IReadOnlyList<DcOption> options)
    {
        return new TableAction
        {
            LabelKey = "Action_Edit", IconKey = "IconPencil", NeedsSelection = true,
            Run = (row, owner) => EditAsync(api, options.First(o => o.Key == row!["key"]), owner)
        };
    }

    /// <summary>현재 값으로 편집 창을 띄워 바뀐 키만 저장한다(취소면 null, 바뀐 것이 없으면 안내 문구).</summary>
    private static async Task<string?> EditAsync(ProxmoxApiClient api, DcOption option, object? owner)
    {
        var config = FromServer(await api.Cluster.GetOptionsAsync());
        var raw = config.GetValueOrDefault(option.Key, string.Empty);
        var title = Loc.T("GuestOptions_EditTitle", option.Label);

        IReadOnlyDictionary<string, string> changes;
        if (option.Editor is { } editor)
        {
            var values = await FormDialog.ShowAsync(title, editor.Fields(raw, config), editor.Validate);
            if (values is null) return null;

            changes = editor.Build(values, raw);
        }
        else
        {
            var values = await FormDialog.ShowAsync(title,
            [
                new FormField
                {
                    Key = "value", LabelKey = option.LabelKey,
                    Kind = option.Kind == DcOptionKind.Choice ? FormFieldKind.Choice : FormFieldKind.Text,
                    Choices = option.Choices, Initial = raw, Trim = true
                }
            ]);
            if (values is null) return null;

            changes = new Dictionary<string, string>(StringComparer.Ordinal) { [option.Key] = values["value"] };
        }

        var changed = changes.Where(kv => config.GetValueOrDefault(kv.Key, string.Empty) != kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (changed.Count == 0) return Loc.T("GuestSettingsWindow_M04");

        // 실제 서버 변경 직전에 한 번 더 확인한다(WriteConfirmation) — 확인 창에 이 옵션 이름이 보인다
        using var flow = WriteConfirmation.Begin(title);
        await api.Cluster.UpdateOptionsAsync(UpdateForm(changed));
        return Loc.T("DcOptions_Updated");
    }

    /// <summary>목록에 보일 값 — 편집기가 보기 문구를 주면 그것을, 아니면 고르기 이름 또는 글자.</summary>
    private static string Render(DcOption option, string raw)
    {
        if (option.Editor?.Display is { } display) return raw.Length > 0 ? display(raw) : EmptyText(option);

        if (option.Kind == DcOptionKind.Choice)
        {
            var match = option.Choices?.FirstOrDefault(c =>
                string.Equals(c.Value, raw, StringComparison.OrdinalIgnoreCase));
            if (match is { Label: { Length: > 0 } label }) return Loc.T(label);
            return raw.Length > 0 ? raw : EmptyText(option);
        }

        return raw.Length > 0 ? raw : EmptyText(option);
    }

    private static string EmptyText(DcOption option)
    {
        return Loc.T(option.EmptyLabelKey ?? "GuestOptions_NotSet");
    }

    /// <summary>GET 이 객체로 돌려준 값(migration·ha·next-id 등)을 PUT 형식(key=value,...)으로 되돌린다.</summary>
    private static IReadOnlyDictionary<string, string> FromServer(IReadOnlyDictionary<string, string> config)
    {
        return config.ToDictionary(kv => kv.Key,
            kv => ObjectKeys.Contains(kv.Key) ? PropertyString.FromJsonObject(kv.Value) : kv.Value,
            StringComparer.Ordinal);
    }
}
