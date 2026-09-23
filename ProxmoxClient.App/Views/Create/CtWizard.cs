using System.Globalization;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Create;

/// <summary>
///     CT 만들기 마법사(웹 UI lxc/CreateWizard.js) — 일반·템플릿·디스크·CPU·메모리·네트워크·DNS·확인.
///     값 조립은 <see cref="CtDraft" />, 이 클래스는 단계 화면과 검사만 맡는다.
/// </summary>
internal sealed class CtWizard
{
    private const int MaxMounts = 256;

    private static readonly (string, string)[] V4Modes = [("static", "Wz_Static"), ("dhcp", "DHCP")];

    private static readonly (string, string)[] V6Modes =
        [("static", "Wz_Static"), ("dhcp", "DHCP"), ("auto", "SLAAC")];

    private readonly ProxmoxApiClient _api;
    private readonly CtDraft _d = new();
    private readonly IReadOnlyList<(string Value, string Label)> _nodes;
    private WizardData _data = new();

    private CtWizard(ProxmoxApiClient api, IReadOnlyList<string> nodes)
    {
        _api = api;
        _nodes = nodes.Select(n => (n, n)).ToList();
    }

    /// <summary>마법사를 연다 — 만들었으면 결과 문구, 취소면 null.</summary>
    public static async Task<string?> ShowAsync(Window owner, ProxmoxApiClient api, IReadOnlyList<string> nodes)
    {
        if (nodes.Count == 0) return null;
        var wizard = new CtWizard(api, nodes);
        await wizard.LoadNodeAsync(nodes[0]);
        var window = new WizardWindow(Loc.T("Wz_CreateCt"), wizard.Steps(), wizard.CreateAsync) { Owner = owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    private async Task LoadNodeAsync(string node)
    {
        _data = await WizardData.LoadAsync(_api, node);
        _d.Node = node;
        if (_d.VmId.Length == 0) _d.VmId = _data.NextId;
        var root = _data.RootStorages.FirstOrDefault()?.Id ?? string.Empty;
        _d.RootStorage = root;
        foreach (var m in _d.Mounts) m.Storage = root;
        _d.Bridge = _data.Bridges.FirstOrDefault().Value ?? string.Empty;
        _d.Template = string.Empty;
    }

    private async Task<string> CreateAsync()
    {
        return await WizardData.CreateAsync(_api, $"nodes/{ActionHelpers.Seg(_d.Node)}/lxc", _d.BuildParams(),
            _d.VmId.Trim());
    }

    private IReadOnlyList<WizardStep> Steps()
    {
        return
        [
            new WizardStep { TitleKey = "Wz_General", Build = General, Validate = ValidateGeneral },
            new WizardStep { TitleKey = "Wz_Template", Build = Template, Validate = ValidateTemplate },
            new WizardStep { TitleKey = "Wz_Disks", Build = Disks, Validate = ValidateDisks },
            new WizardStep { TitleKey = "Wz_Cpu", Build = Cpu, Validate = ValidateCpu },
            new WizardStep { TitleKey = "Wz_Memory", Build = Memory, Validate = ValidateMemory },
            new WizardStep { TitleKey = "Wz_Network", Build = Network, Validate = ValidateNetwork },
            new WizardStep { TitleKey = "Wz_Dns", Build = Dns },
            new WizardStep { TitleKey = "Wz_Confirm", Build = Confirm }
        ];
    }

    // ------------------------------------------------------------ 일반 · 템플릿

    private void General(WizardPage page)
    {
        page.Choice("Wz_Node", _nodes, () => _d.Node, node => _ = ChangeNodeAsync(page.Window, node));
        page.Text("Wz_CtId", () => _d.VmId, v => _d.VmId = v);
        page.Text("Wz_Hostname", () => _d.Hostname, v => _d.Hostname = v);
        page.Check("Wz_Unprivileged", () => _d.Unprivileged, v => _d.Unprivileged = v, rebuild: true);
        page.Check("Wz_Nesting", () => _d.Unprivileged && _d.Nesting, v => _d.Nesting = v,
            enabled: _d.Unprivileged);
        page.Choice("Table_Pool", [("", "Common_None"), .. _data.Pools], () => _d.Pool, v => _d.Pool = v);
        page.Password("Wz_Password", () => _d.Password, v => _d.Password = v);
        page.Password("Wz_ConfirmPassword", () => _d.ConfirmPassword, v => _d.ConfirmPassword = v);
        page.Multiline("Wz_SshKeys", () => _d.SshKeys, v => _d.SshKeys = v, Loc.T("Wz_SshKeysHint"));
        page.Text("Wz_Tags", () => _d.Tags, v => _d.Tags = v, Loc.T("Wz_TagsHint"), true);
    }

    /// <summary>노드를 바꾸면 그 노드의 목록을 다시 읽는다 — 읽는 동안 창을 잠가 다른 입력과 섞이지 않게.</summary>
    private async Task ChangeNodeAsync(WizardWindow window, string node)
    {
        window.SetBusy(true);
        window.SetStatus(Loc.T("Hw_Loading"));
        try
        {
            await LoadNodeAsync(node);
            window.SetStatus(string.Empty);
        }
        catch (Exception ex)
        {
            App.Log($"[만들기] 노드 목록 조회 실패: {ex.Message}");
            window.SetStatus(Loc.T("Wz_LoadFailed", ex.Message));
        }
        finally
        {
            window.SetBusy(false);
            window.Rebuild();
        }
    }

    private string? ValidateGeneral()
    {
        if (!int.TryParse(_d.VmId, out var id) || id < 100) return Loc.T("CreateGuest_VmidMin", 100);
        if (_d.Hostname.Trim() is { Length: > 0 } host && !IsDnsName(host)) return Loc.T("Wz_BadHostname");
        return _d.PasswordProblem() is { } key ? Loc.T(key, CtDraft.MinPasswordLength) : null;
    }

    /// <summary>호스트 이름 — 영숫자·하이픈 라벨을 점으로 이은 것(웹 UI DnsName).</summary>
    internal static bool IsDnsName(string value)
    {
        return value.Split('.').All(label => label.Length is > 0 and <= 63 && char.IsAsciiLetterOrDigit(label[0])
                                             && char.IsAsciiLetterOrDigit(label[^1])
                                             && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    private void Template(WizardPage page)
    {
        page.Choice("Wz_Template", _data.Templates, () => _d.Template, v => _d.Template = v,
            _data.Templates.Count == 0 ? Loc.T("Wz_NoTemplate") : null);
    }

    private string? ValidateTemplate()
    {
        return _d.Template.Length == 0 ? Loc.T("Wz_PickTemplate") : null;
    }

    // ------------------------------------------------------------ 디스크

    private void Disks(WizardPage page)
    {
        var storages = WizardData.StorageChoices(_data.RootStorages);
        page.Section("Wz_RootDisk");
        page.Choice("Table_Storage", storages, () => _d.RootStorage, v => _d.RootStorage = v);
        page.Text("HwEd_DiskSizeGib", () => _d.RootSizeGib, v => _d.RootSizeGib = v);
        foreach (var m in _d.Mounts.ToList())
        {
            page.Section(Loc.T("Wz_MountTitle", m.Id));
            page.Choice("Table_Storage", storages, () => m.Storage, v => m.Storage = v);
            page.Text("HwEd_DiskSizeGib", () => m.SizeGib, v => m.SizeGib = v);
            page.Text("Wz_MountPath", () => m.Path, v => m.Path = v, Loc.T("Wz_MountPathHint"));
            page.Check("HwEd_Backup", () => m.Backup, v => m.Backup = v);
            page.Button("Wz_RemoveDisk", () =>
            {
                _d.Mounts.Remove(m);
                page.Window.Rebuild();
            });
        }

        if (_d.Mounts.Count >= MaxMounts) return;
        page.Button("Wz_AddMount", () =>
        {
            var used = _d.Mounts.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            var id = Enumerable.Range(0, MaxMounts).Select(i => $"mp{i}").First(s => !used.Contains(s));
            _d.Mounts.Add(new MountDraft { Id = id, Storage = _d.RootStorage });
            page.Window.Rebuild();
        });
    }

    private string? ValidateDisks()
    {
        if (_d.RootStorage.Length == 0) return Loc.T("Wz_PickStorage", "rootfs");
        if (!IsSize(_d.RootSizeGib)) return Loc.T("HwEd_DiskSizeRange");
        foreach (var m in _d.Mounts)
        {
            if (m.Storage.Length == 0) return Loc.T("Wz_PickStorage", m.Id);
            if (!IsSize(m.SizeGib)) return Loc.T("HwEd_DiskSizeRange");
            if (!m.Path.Trim().StartsWith('/') || m.Path.Trim() == "/") return Loc.T("Wz_MountPathHint");
        }

        return _d.Mounts.Select(m => m.Path.Trim()).Distinct().Count() == _d.Mounts.Count
            ? null
            : Loc.T("Wz_DuplicatePath");
    }

    private static bool IsSize(string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gib)
               && gib is >= 0.001 and <= 131072;
    }

    // ------------------------------------------------------------ CPU · 메모리

    private void Cpu(WizardPage page)
    {
        page.Text("HwEd_Cores", () => _d.Cores, v => _d.Cores = v,
            _data.HostCpus > 0 ? Loc.T("HostLimit_Cores", _data.HostCpus, 1) : null);
        page.Text("Wz_CpuLimit", () => _d.CpuLimit, v => _d.CpuLimit = v, Loc.T("Wz_CpuLimitHint"), true);
        page.Text("Wz_CpuUnits", () => _d.CpuUnits, v => _d.CpuUnits = v, Loc.T("Wz_CpuUnitsHint"), true);
    }

    private string? ValidateCpu()
    {
        if (!int.TryParse(_d.Cores, out var c) || c is < 1 or > 8192) return Loc.T("GuestSettings_CoresMin");
        if (_d.CpuLimit.Trim().Length > 0
            && !(double.TryParse(_d.CpuLimit, NumberStyles.Float, CultureInfo.InvariantCulture, out var l)
                 && l is >= 0 and <= 8192))
            return Loc.T("Wz_CpuLimitHint");
        return _d.CpuUnits.Trim().Length == 0 || int.TryParse(_d.CpuUnits, out var u) && u is >= 1 and <= 10000
            ? null
            : Loc.T("Wz_CpuUnitsHint");
    }

    private void Memory(WizardPage page)
    {
        page.Text("HwEd_MemoryMiB", () => _d.Memory, v => _d.Memory = v,
            _data.HostMemoryMiB > 0 ? Loc.T("Wz_HostMemory", _data.HostMemoryMiB) : null);
        page.Text("Wz_SwapMiB", () => _d.Swap, v => _d.Swap = v);
    }

    private string? ValidateMemory()
    {
        if (!int.TryParse(_d.Memory, out var m) || m < 16) return Loc.T("HwEd_MemoryMin", 16);
        return int.TryParse(_d.Swap, out var s) && s >= 0 ? null : Loc.T("Wz_SwapMin");
    }

    // ------------------------------------------------------------ 네트워크 · DNS · 확인

    private void Network(WizardPage page)
    {
        page.Text("Table_Name", () => _d.NetName, v => _d.NetName = v);
        page.Text("GuestSettingsWindow_19", () => _d.Mac, v => _d.Mac = v, Loc.T("HwEd_MacAuto"));
        page.Choice("GuestSettingsWindow_17", _data.Bridges, () => _d.Bridge, v => _d.Bridge = v);
        page.Text("GuestSettingsWindow_21", () => _d.Vlan, v => _d.Vlan = v, Loc.T("HwEd_NoVlan"));
        page.Check("FirewallWindow_01", () => _d.Firewall, v => _d.Firewall = v);
        page.Section("Wz_IPv4");
        page.Choice("Wz_IpMode", V4Modes, () => _d.V4Mode, v => _d.V4Mode = v, rebuild: true);
        if (_d.V4Mode == "static")
        {
            page.Text("Wz_IpCidr", () => _d.Ip, v => _d.Ip = v, "192.168.1.10/24");
            page.Text("Wz_Gateway", () => _d.Gateway, v => _d.Gateway = v);
        }

        page.Section("Wz_IPv6");
        page.Choice("Wz_IpMode", V6Modes, () => _d.V6Mode, v => _d.V6Mode = v, rebuild: true);
        if (_d.V6Mode != "static") return;
        page.Text("Wz_IpCidr", () => _d.Ip6, v => _d.Ip6 = v, "2001:db8::10/64");
        page.Text("Wz_Gateway", () => _d.Gateway6, v => _d.Gateway6 = v);
    }

    private string? ValidateNetwork()
    {
        if (_d.NetName.Trim().Length == 0) return Loc.T("Wz_NetNameRequired");
        if (WizardChecks.Network(_d.Bridge, _d.Vlan, _d.Mac) is { } problem) return problem;
        if (_d.V4Mode == "static" && _d.Ip.Trim().Length > 0 && !_d.Ip.Contains('/')) return Loc.T("Wz_NeedCidr");
        return _d.V6Mode == "static" && _d.Ip6.Trim().Length > 0 && !_d.Ip6.Contains('/')
            ? Loc.T("Wz_NeedCidr")
            : null;
    }

    private void Dns(WizardPage page)
    {
        page.Note(Loc.T("Wz_DnsHostNote"));
        page.Text("Wz_SearchDomain", () => _d.SearchDomain, v => _d.SearchDomain = v, Loc.T("Wz_UseHost"));
        page.Text("Wz_DnsServers", () => _d.Nameserver, v => _d.Nameserver = v, Loc.T("Wz_UseHost"));
    }

    private void Confirm(WizardPage page)
    {
        var values = _d.BuildParams().ToDictionary(kv => kv.Key, kv => kv.Key == "password" ? "********" : kv.Value);
        page.Summary(values);
        page.Check("Wz_StartAfter", () => _d.Start, v => _d.Start = v, rebuild: true);
    }
}
