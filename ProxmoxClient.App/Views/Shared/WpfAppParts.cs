using System.Globalization;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.App.Views.Datacenter;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>공용 화면 안에서 이 앱이 따로 만드는 조각(<see cref="IAppParts" />).</summary>
internal sealed class WpfAppParts : IAppParts
{
    public bool SendsDigest => false;

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
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gib)
               && gib > 0 && gib <= maxGib
            ? $"+{text}G"
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

    /// <summary>Ceph 설치 — 웹 UI 처럼 노드 셸에서 설치 마법사(pveceph install)를 띄운다.</summary>
    public TableAction CephInstall(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "CephTab_Install", IconKey = "IconTerminal",
            Run = (_, _) =>
            {
                Services.ConsoleWindows.ShowNodeShell(api, node, "ceph_install");
                return Task.FromResult<string?>(Loc.T("CephTab_InstallOpened"));
            }
        };
    }

    /// <summary>웹 UI 의 '업그레이드'와 같다 — 노드 셸에서 apt dist-upgrade 를 실행해 진행을 직접 본다.</summary>
    public TableAction AptUpgrade(ProxmoxApiClient api, string node)
    {
        return new TableAction
        {
            LabelKey = "NodeApt_Upgrade", IconKey = "IconTerminal",
            Run = (_, _) =>
            {
                Services.ConsoleWindows.ShowNodeShell(api, node, "upgrade");
                return Task.FromResult<string?>(Loc.T("NodeApt_UpgradeOpened"));
            }
        };
    }

    /// <summary>Windows 자격 증명 관리자.</summary>
    private sealed class SecretStore : IGuestSecretStore
    {
        public string? Read(string target)
        {
            return WindowsCredentialStore.Read(target);
        }

        public void Write(string target, string userName, string secret)
        {
            WindowsCredentialStore.Write(target, userName, secret);
        }

        public void Delete(string target)
        {
            WindowsCredentialStore.Delete(target);
        }
    }
}
