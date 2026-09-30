using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Node;

/// <summary>노드 업데이트 화면 — 웹 UI 처럼 '업데이트'와 'APT 저장소' 하위 탭.</summary>
internal static class UpdateTabs
{
    private static readonly IReadOnlyList<TableColumn> UpdateColumns =
    [
        new() { Key = "Package", HeaderKey = "Table_Package", Width = 180 },
        new() { Key = "OldVersion", HeaderKey = "Table_CurrentVersion", Width = 140 },
        new() { Key = "Version", HeaderKey = "Table_NewVersion", Width = 140 },
        new() { Key = "Title", HeaderKey = "Table_Description", Width = 0 }
    ];

    private static readonly IReadOnlyList<TableColumn> RepositoryColumns =
    [
        new() { Key = "Enabled", HeaderKey = "Table_Enabled", Width = 60, Format = TableFormats.Flag },
        new() { Key = "Types", HeaderKey = "Table_Type", Width = 70 },
        new() { Key = "URIs", HeaderKey = "NodeApt_Uri", Width = 0 },
        new() { Key = "Suites", HeaderKey = "NodeApt_Suites", Width = 110 },
        new() { Key = "Components", HeaderKey = "NodeApt_Components", Width = 160 },
        new() { Key = "Comment", HeaderKey = "Table_Comment", Width = 140 },
        new() { Key = "path", HeaderKey = "Table_File", Width = 200 }
    ];

    public static SubTabsView Create(ProxmoxApiClient api, string node, bool canConsole)
    {
        return new SubTabsView(
        [
            ("NodeTab_Updates", () => new TableTab(() => api.Nodes.ListUpdatesAsync(node), UpdateColumns,
                "NodeUpdates_Hint", UpdateActions(api, node, canConsole))),
            ("NodeApt_Repositories", () => new TableTab(async () =>
                {
                    // 켜고 끌 때 쓸 수 있게 목록을 읽은 시점의 digest 를 행마다 붙여 둔다
                    var repos = await api.GetAptRepositoriesAsync(node);
                    return repos.Repositories
                        .Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(r)
                        {
                            ["digest"] = repos.Digest
                        })
                        .ToList();
                }, RepositoryColumns,
                "NodeApt_Hint", RepositoryActions(api, node)))
        ]);
    }

    private static readonly IReadOnlyList<TableColumn> PackageVersionColumns =
    [
        new() { Key = "Package", HeaderKey = "NodeApt_Package", Width = 200 },
        new() { Key = "Version", HeaderKey = "NodeApt_Version", Width = 180 },
        new() { Key = "CurrentState", HeaderKey = "Table_State", Width = 110 },
        new() { Key = "Title", HeaderKey = "Table_Description", Width = 0 }
    ];

    private static IReadOnlyList<TableAction> UpdateActions(ProxmoxApiClient api, string node,
        bool canConsole)
    {
        var actions = new List<TableAction>
        {
            new()
            {
                LabelKey = "NodeUpdates_RefreshRepo", IconKey = "IconDownload",
                Run = async (_, _) => await RunTaskAsync(api, api.Nodes.RefreshUpdatesAsync(node),
                    "NodeUpdates_Refreshed")
            },
            new()
            {
                LabelKey = "NodeApt_Changelog", IconKey = "IconList", NeedsSelection = true,
                Run = async (row, owner) =>
                {
                    var package = row!["Package"];
                    var text = await api.Nodes.ChangelogAsync(node, package, Value(row, "Version"));
                    return TextViewWindow.ShowModal(owner, Loc.T("NodeApt_ChangelogTitle", package), text);
                }
            }
        };

        actions.Add(new TableAction
        {
            LabelKey = "NodeApt_Versions", IconKey = "IconBox",
            Run = (_, owner) => Task.FromResult(TableWindow.ShowModal(owner, Loc.T("NodeApt_VersionsTitle", node),
                new TableTab(() => api.Nodes.PackageVersionsAsync(node), PackageVersionColumns,
                    "NodeApt_VersionsHint")))
        });

        // 웹 UI 의 '업그레이드'와 같다 — 노드 셸에서 apt dist-upgrade 를 실행해 진행을 직접 본다
        if (canConsole)
            actions.Add(new TableAction
            {
                LabelKey = "NodeApt_Upgrade", IconKey = "IconTerminal",
                Run = (_, _) =>
                {
                    Services.ConsoleWindows.ShowNodeShell(api, node, "upgrade");
                    return Task.FromResult<string?>(Loc.T("NodeApt_UpgradeOpened"));
                }
            });

        return actions;
    }

    private static IReadOnlyList<TableAction> RepositoryActions(ProxmoxApiClient api, string node)
    {
        return
        [
            new TableAction
            {
                LabelKey = "NodeApt_Toggle", IconKey = "IconSwitch", NeedsSelection = true,
                Run = async (row, _) =>
                {
                    // 목록을 읽은 뒤 파일이 바뀌었으면 서버가 digest 로 거절한다(순번이 다른 저장소를 가리키지 않게)
                    var digest = Value(row!, "digest");
                    var enable = Value(row!, "Enabled") is "1" or "true" ? "0" : "1";
                    await api.Nodes.ChangeRepositoryAsync(node, new Dictionary<string, string>
                    {
                        ["path"] = row!["path"], ["index"] = row["index"], ["enabled"] = enable, ["digest"] = digest
                    });
                    return Loc.T(enable == "1" ? "NodeApt_Enabled" : "NodeApt_Disabled");
                }
            },
            new TableAction
            {
                LabelKey = "NodeApt_AddStandard", IconKey = "IconPlus",
                Run = async (_, owner) =>
                {
                    var repos = await api.GetAptRepositoriesAsync(node);
                    var choices = repos.Standard
                        .Where(r => Value(r, "status") is not ("1" or "true"))
                        .Select(r => (Value(r, "handle"), Value(r, "name")))
                        .ToList();

                    return await SubmitAsync(owner, "NodeApt_AddStandard",
                    [
                        new FormField
                        {
                            Key = "handle", LabelKey = "NodeApt_Repository", Kind = FormFieldKind.Choice,
                            Choices = choices, Required = true
                        }
                    ], values => api.Nodes.AddRepositoryAsync(node, new Dictionary<string, string>
                    {
                        ["handle"] = values["handle"], ["digest"] = repos.Digest
                    }), "NodeApt_Added");
                }
            }
        ];
    }
}
