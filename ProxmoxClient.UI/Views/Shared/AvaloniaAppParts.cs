using System.Globalization;
using System.Runtime.CompilerServices;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Views.Datacenter;

namespace ProxmoxClient.UI.Views.Shared;

/// <summary>공용 화면 안에서 이 앱이 따로 만드는 조각(<see cref="IAppParts" />).</summary>
internal sealed class AvaloniaAppParts : IAppParts
{
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255",
        Justification = "공용 코드는 이 어셈블리의 코드에서만 불리므로, 어셈블리가 처음 쓰일 때 한 번 연결하면 된다")]
    internal static void Register()
    {
        AppParts.Current = new AvaloniaAppParts();
    }

    public bool SendsDigest => true;

    public IGuestSecretStore GuestSecrets { get; } = new SecretStore();

    public ViewSpec VnetFirewall(ProxmoxApiClient api, string vnet, bool canEdit)
    {
        return new NativeView(() => FirewallTabs.ForVnet(api, vnet, canEdit));
    }

    public IReadOnlyList<TableAction> NetworkActions(ProxmoxApiClient api, string node)
    {
        return Node.NetworkActions.Actions(api, node);
    }

    public IReadOnlyList<TableAction> TfaRegistration(ProxmoxApiClient api)
    {
        return Datacenter.TfaRegistration.Actions(api);
    }

    public string? ResizeSize(string text, int maxGib)
    {
        // 소수점만 받는다 — 1e3·1,5 같은 값은 서버 형식(+숫자[.숫자]G)이 아니다
        return GuestInputChecks.TryParseGiB(text, out var gib) && gib <= maxGib
            ? $"+{gib.ToString(CultureInfo.InvariantCulture)}G"
            : null;
    }

    public ViewSpec StorageGraph(ProxmoxApiClient api, string node, string storage)
    {
        return new NativeView(() => new Storage.StorageGraphTab(api, node, storage));
    }

    public TableAction FileRestore(ProxmoxApiClient api, string node, string storage)
    {
        return Storage.FileRestoreBrowser.Create(api, node, storage);
    }

    public Task<string?> UploadAsync(ProxmoxApiClient api, string node, string storage, string content,
        object? owner)
    {
        return Storage.StorageNavigator.UploadAsync(api, node, storage, content, owner);
    }

    /// <summary>Ceph 설치 — 웹 UI 처럼 터미널에서 설치 마법사(pveceph install)를 띄운다(먼저 확인).</summary>
    public TableAction CephInstall(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "CephTab_Install", IconKey = "IconTerminal",
            Run = async (_, _) =>
            {
                await OpenCephInstallerAsync(api, node);
                return null;
            }
        };
    }

    /// <summary>확인을 받고 노드 셸에서 pveceph install 을 연다.</summary>
    public static async Task OpenCephInstallerAsync(ProxmoxApiClient api, string node)
    {
        if (await Ask.ConfirmAsync(Loc.T("Terminal_ConfirmCephInstall", node), Loc.T("CephTab_Install")))
            await Services.ConsoleLauncher.OpenNodeShellAsync(api, node, "ceph_install");
    }

    /// <summary>
    ///     웹 UI 의 '업그레이드'와 같다 — 노드 셸에서 apt dist-upgrade 를 실행해 진행을 직접 본다.
    ///     시스템이 바뀌는 작업이라 셸을 열기 전에 확인을 받는다.
    /// </summary>
    public TableAction AptUpgrade(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "NodeApt_Upgrade", IconKey = "IconTerminal",
            Run = async (_, _) =>
            {
                if (!await Ask.ConfirmAsync(Loc.T("Terminal_ConfirmUpgrade", node), Loc.T("NodeApt_Upgrade")))
                    return null;

                await Services.ConsoleLauncher.OpenNodeShellAsync(api, node, "upgrade");
                return Loc.T("NodeApt_UpgradeOpened");
            }
        };
    }

    /// <summary>공용 비밀값 저장소(<see cref="Files.GuestSecrets" />) — 사용자 이름은 설정 파일에 따로 있어 보관하지 않는다.</summary>
    private sealed class SecretStore : IGuestSecretStore
    {
        public string? Read(string target)
        {
            return Files.GuestSecrets.Read(target);
        }

        public void Write(string target, string userName, string secret)
        {
            Files.GuestSecrets.Write(target, secret);
        }

        public void Delete(string target)
        {
            Files.GuestSecrets.Delete(target);
        }
    }
}
