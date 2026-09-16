using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views;

public partial class GuestSettingsWindow : Window
{
    private const string DefaultBridge = "vmbr0";
    private const string DefaultNicModel = "virtio";

    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private bool _busy;
    private IReadOnlyDictionary<string, string> _config = new Dictionary<string, string>();

    public GuestSettingsWindow(ProxmoxApiClient api, PveResource guest)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _api = api;
        _guest = guest;
        HeaderText.Text = $"{guest.Kind.Label()} {guest.VmId} — {guest.Name}";
        Title = Loc.T("GuestSettingsWindow_M01", guest.Kind.Label(), guest.VmId);

        ComboChoices.Fill(OsTypeBox, ComboChoices.QemuOsTypes);
        ComboChoices.Fill(NicModelBox, ComboChoices.NicModels);
        ApplyKindVisibility();

        Loaded += async (_, _) => await LoadAsync();
    }

    private bool IsVm => _guest.Kind == ResourceKind.Qemu;

    /// <summary>게스트 종류에 해당하지 않는 항목은 숨긴다(VM 전용 / CT 전용).</summary>
    private void ApplyKindVisibility()
    {
        var vmOnly = IsVm ? Visibility.Visible : Visibility.Collapsed;
        var ctOnly = IsVm ? Visibility.Collapsed : Visibility.Visible;

        RowOsType.Visibility = vmOnly;
        RowBoot.Visibility = vmOnly;
        RowSockets.Visibility = vmOnly;
        RowBalloon.Visibility = vmOnly;
        RowNicModel.Visibility = vmOnly;
        RowMac.Visibility = vmOnly;

        RowCpuLimit.Visibility = ctOnly;
        RowIp.Visibility = ctOnly;
        DnsSection.Visibility = ctOnly;
    }

    private async Task LoadAsync()
    {
        try
        {
            _config = await _api.GetGuestConfigAsync(_guest.Node, _guest.Kind, _guest.VmId);
            await LoadBridgesAsync();

            NameBox.Text = Get(IsVm ? "name" : "hostname");
            OnBootCheck.IsChecked = Get("onboot") == "1";
            ProtectionCheck.IsChecked = Get("protection") == "1";
            StartupBox.Text = Get("startup");
            ComboChoices.Select(OsTypeBox, Get("ostype"));
            BootBox.Text = Get("boot");
            CoresBox.Text = Get("cores");
            SocketsBox.Text = Get("sockets");
            MemoryBox.Text = Get("memory");
            BalloonBox.Text = Get("balloon");
            CpuLimitBox.Text = Get("cpulimit");
            NameserverBox.Text = Get("nameserver");
            SearchDomainBox.Text = Get("searchdomain");

            ParseNet0(Get("net0"));
            SetStatus(Loc.T("GuestSettingsWindow_M02"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("GuestSettingsWindow_M03", ex.Message));
        }
    }

    private async Task LoadBridgesAsync()
    {
        try
        {
            BridgeBox.ItemsSource = await _api.GetNodeBridgesAsync(_guest.Node);
        }
        catch (Exception ex)
        {
            App.Log($"[설정] 브리지 목록 조회 실패: {ex.Message}");
        }
    }

    private string Get(string key)
    {
        return _config.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private void ParseNet0(string net0)
    {
        BridgeBox.Text = DefaultBridge;
        ComboChoices.Select(NicModelBox, DefaultNicModel);
        IpBox.Text = "dhcp";
        MacBox.Text = string.Empty;
        VlanBox.Text = string.Empty;
        NicFirewallCheck.IsChecked = false;
        if (net0.Length == 0) return;

        var tokens = net0.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var eq = tokens[i].IndexOf('=');
            var key = eq > 0 ? tokens[i][..eq] : tokens[i];
            var value = eq > 0 ? tokens[i][(eq + 1)..] : string.Empty;

            // VM 형식: "virtio=BC:24:11:..,bridge=vmbr0" — 첫 토큰의 키가 NIC 모델, 값이 MAC
            if (IsVm && i == 0 && key is not ("bridge" or "tag" or "firewall"))
            {
                ComboChoices.Select(NicModelBox, key);
                MacBox.Text = value;
                continue;
            }

            switch (key)
            {
                case "bridge":
                    BridgeBox.Text = value;
                    break;
                case "tag":
                    VlanBox.Text = value;
                    break;
                case "ip":
                    IpBox.Text = value;
                    break;
                case "firewall":
                    NicFirewallCheck.IsChecked = value == "1";
                    break;
            }
        }
    }

    private string BuildNet0()
    {
        var bridge = string.IsNullOrWhiteSpace(BridgeBox.Text) ? DefaultBridge : BridgeBox.Text.Trim();
        var vlan = VlanBox.Text.Trim();
        var firewall = NicFirewallCheck.IsChecked == true;

        List<string> parts;
        if (IsVm)
        {
            var model = ComboChoices.Selected(NicModelBox) is { Length: > 0 } m ? m : DefaultNicModel;
            var mac = MacBox.Text.Trim();
            parts = [mac.Length > 0 ? $"{model}={mac}" : model, $"bridge={bridge}"];
        }
        else
        {
            var ip = string.IsNullOrWhiteSpace(IpBox.Text) ? "dhcp" : IpBox.Text.Trim();
            parts = ["name=eth0", $"bridge={bridge}", $"ip={ip}"];
        }

        if (vlan.Length > 0) parts.Add($"tag={vlan}");

        if (firewall) parts.Add("firewall=1");

        return string.Join(",", parts);
    }

    /// <summary>입력 필터로 걸러지지 않는 범위(최소값 등) 검증. 오류 메시지 또는 null.</summary>
    private string? Validate()
    {
        if (!IsPositiveOrEmpty(CoresBox.Text) || (IsVm && !IsPositiveOrEmpty(SocketsBox.Text)))
            return Loc.T("GuestSettings_CoresMin");

        if (MemoryBox.Text.Trim().Length > 0 && (!int.TryParse(MemoryBox.Text, out var memory) || memory < 16))
            return Loc.T("GuestSettings_MemoryMin");

        if (VlanBox.Text.Trim().Length > 0 && (!int.TryParse(VlanBox.Text, out var tag) || tag is < 1 or > 4094))
            return Loc.T("GuestSettings_VlanRange");

        return null;
    }

    private static bool IsPositiveOrEmpty(string text)
    {
        return text.Trim().Length == 0 || (int.TryParse(text, out var value) && value >= 1);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (Validate() is { } error)
        {
            SetStatus(error);
            return;
        }

        var changes = new Dictionary<string, string>(StringComparer.Ordinal);

        void Change(string key, string value)
        {
            if (Get(key) != value) changes[key] = value;
        }

        Change(IsVm ? "name" : "hostname", NameBox.Text.Trim());
        Change("onboot", OnBootCheck.IsChecked == true ? "1" : "0");
        Change("protection", ProtectionCheck.IsChecked == true ? "1" : "0");
        Change("startup", StartupBox.Text.Trim());
        Change("cores", CoresBox.Text.Trim());
        Change("memory", MemoryBox.Text.Trim());
        Change("net0", BuildNet0());

        if (IsVm)
        {
            Change("sockets", SocketsBox.Text.Trim());
            Change("balloon", BalloonBox.Text.Trim());
            Change("ostype", ComboChoices.Selected(OsTypeBox));
            Change("boot", BootBox.Text.Trim());
        }
        else
        {
            Change("cpulimit", CpuLimitBox.Text.Trim());
            Change("nameserver", NameserverBox.Text.Trim());
            Change("searchdomain", SearchDomainBox.Text.Trim());
        }

        if (changes.Count == 0)
        {
            SetStatus(Loc.T("GuestSettingsWindow_M04"));
            return;
        }

        _busy = true;
        BtnSave.IsEnabled = false;
        SetStatus(Loc.T("GuestSettingsWindow_M05", changes.Count));

        try
        {
            await _api.UpdateGuestConfigAsync(_guest.Node, _guest.Kind, _guest.VmId, changes);
            SetStatus(Loc.T("GuestSettingsWindow_M06"));
            await Task.Delay(600);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("AppSettingsWindow_M03", ex.Message));
        }
        finally
        {
            _busy = false;
            BtnSave.IsEnabled = true;
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}