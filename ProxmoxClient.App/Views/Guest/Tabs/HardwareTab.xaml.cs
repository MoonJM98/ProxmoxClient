using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>게스트 창의 하드웨어 탭 — CPU·메모리·네트워크와 일반 설정을 바꾼다.</summary>
public partial class HardwareTab : UserControl
{
    private const string DefaultBridge = "vmbr0";
    private const string DefaultNicModel = "virtio";

    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private bool _busy;
    private IReadOnlyDictionary<string, string> _config = new Dictionary<string, string>();

    /// <summary>불러온 직후 네트워크 칸으로 만든 net0 — 저장 때 이와 같으면 사용자가 칸을 건드리지 않은 것이다.</summary>
    private string _loadedNet0 = string.Empty;

    /// <summary>게스트가 있는 노드의 CPU·메모리 — 최대치 안내와 저장 전 검사에 쓴다(못 읽으면 null).</summary>
    private PveNodeStatus? _host;

    public HardwareTab(ProxmoxApiClient api, PveResource guest)
    {
        InitializeComponent();
        _api = api;
        _guest = guest;

        ComboChoices.Fill(NicModelBox, ComboChoices.NicModels);
        ApplyKindVisibility();

        Loaded += async (_, _) =>
        {
            await LoadAsync();
            await LoadHostLimitsAsync();
        };
    }

    /// <summary>
    ///     노드가 가진 CPU·메모리를 칸 아래에 보여 준다. 소켓 × 코어가 노드의 논리 CPU 수를 넘으면
    ///     Proxmox 가 게스트를 시작하지 못하고, 메모리는 넘겨도 되지만 여유를 넘기면 스왑·OOM 위험이 있다.
    /// </summary>
    private async Task LoadHostLimitsAsync()
    {
        try
        {
            _host = await _api.GetNodeStatusAsync(_guest.Node);
            RowCores.Hint = Loc.T("HostLimit_Cores", _host.CpuCores, Math.Max(_host.CpuSockets, 1));
            var total = _host.MemTotalBytes;
            RowMemory.Hint = Loc.T("HostLimit_Memory", total / MiB,
                Math.Max(total - _host.MemUsedBytes, 0) / MiB);
        }
        catch (Exception ex)
        {
            App.Log($"[설정] 노드 {_guest.Node} 자원 조회 실패: {ex.Message}");
        }
    }

    private const long MiB = 1024 * 1024;

    private bool IsVm => _guest.Kind == ResourceKind.Qemu;

    /// <summary>게스트 종류에 해당하지 않는 항목은 숨긴다(VM 전용 / CT 전용).</summary>
    private void ApplyKindVisibility()
    {
        var vmOnly = IsVm ? Visibility.Visible : Visibility.Collapsed;
        var ctOnly = IsVm ? Visibility.Collapsed : Visibility.Visible;

        RowSockets.Visibility = vmOnly;
        RowBalloon.Visibility = vmOnly;
        RowNicModel.Visibility = vmOnly;
        RowMac.Visibility = vmOnly;

        RowCpuLimit.Visibility = ctOnly;
        RowIp.Visibility = ctOnly;
    }

    /// <summary>설정을 다시 읽는다. 성공하면 true(실패 문구는 상태 줄에 남긴다).</summary>
    private async Task<bool> LoadAsync()
    {
        try
        {
            _config = await _api.GetGuestConfigAsync(_guest.Node, _guest.Kind, _guest.VmId);
            await LoadBridgesAsync();

            CoresBox.Text = Get("cores");
            SocketsBox.Text = Get("sockets");
            MemoryBox.Text = Get("memory");
            BalloonBox.Text = Get("balloon");
            CpuLimitBox.Text = Get("cpulimit");

            ParseNet0(Get("net0"));
            _loadedNet0 = BuildNet0();
            SetStatus(Loc.T("GuestSettingsWindow_M02"));
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("GuestSettingsWindow_M03", ex.Message));
            return false;
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
        IpBox.Text = IsVm ? "dhcp" : string.Empty; // CT 는 ip 가 없으면 빈칸(저장해도 ip=dhcp 를 붙이지 않는다)
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

    /// <summary>
    ///     화면 칸으로 net0 을 만든다 — 이 화면에 없는 설정(mtu·rate·queues·link_down·CT 의 gw·ip6 등)은
    ///     원래 값 그대로 둔다(예전에는 새로 조립하며 지워졌다).
    /// </summary>
    private string BuildNet0()
    {
        return MergeNet0(Get("net0"), IsVm, new NicInput(
            ComboChoices.Selected(NicModelBox) is { Length: > 0 } m ? m : DefaultNicModel,
            MacBox.Text.Trim(),
            string.IsNullOrWhiteSpace(BridgeBox.Text) ? DefaultBridge : BridgeBox.Text.Trim(),
            VlanBox.Text.Trim(),
            NicFirewallCheck.IsChecked == true,
            IpBox.Text.Trim()));
    }

    /// <summary>간편 화면의 네트워크 칸 값.</summary>
    internal sealed record NicInput(string Model, string Mac, string Bridge, string Vlan, bool Firewall, string Ip);

    /// <summary>
    ///     기존 net0 에 화면 칸만 반영한다 — 이 화면에 없는 설정(mtu·rate·queues·link_down·CT 의 gw·ip6 등)은
    ///     원래 값 그대로 둔다.
    /// </summary>
    internal static string MergeNet0(string currentNet0, bool isVm, NicInput input)
    {
        var current = PropertyString.Parse(currentNet0);
        PropertyString updated;
        if (isVm)
        {
            // VM 형식은 첫 항목이 "모델=MAC" — 모델이 바뀔 수 있으므로 첫 항목을 새로 만들고 나머지를 뒤에 붙인다
            var oldModel = current.Items.Count > 0 ? current.Items[0].Key : null;
            var rest = current.Items.Where(kv => kv.Key != oldModel)
                .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value));
            var head = input.Mac.Length > 0 ? $"{input.Model}={input.Mac}" : input.Model;
            updated = PropertyString.Parse(head).With(rest);
        }
        else
        {
            // 빈 IP 는 ip 키를 뺀다(예전처럼 dhcp 로 바꾸지 않는다)
            updated = (current.Has("name") ? current : current.With("name", "eth0")).With("ip", input.Ip);
        }

        return updated
            .With("bridge", input.Bridge)
            .With("tag", input.Vlan)
            .With("firewall", input.Firewall ? "1" : null)
            .Format();
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

        // 소켓 × 코어가 노드의 논리 CPU 수를 넘으면 Proxmox 가 시작을 거부한다
        if (_host is { CpuCores: > 0 } host && TotalVcpus() > host.CpuCores)
            return Loc.T("HostLimit_TooManyCores", TotalVcpus(), host.CpuCores);

        return null;
    }

    /// <summary>입력한 소켓 × 코어(빈 칸은 서버 기본값 1).</summary>
    private int TotalVcpus()
    {
        static int OrOne(string text) => int.TryParse(text, out var v) && v > 0 ? v : 1;
        var sockets = IsVm ? OrOne(SocketsBox.Text) : 1;
        return sockets * OrOne(CoresBox.Text);
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

        Change("cores", CoresBox.Text.Trim());
        Change("memory", MemoryBox.Text.Trim());
        // 네트워크 칸을 건드리지 않았으면 net0 을 보내지 않는다(장치가 없는 게스트에 새로 생기지 않게)
        if (BuildNet0() != _loadedNet0) Change("net0", BuildNet0());

        if (IsVm)
        {
            Change("sockets", SocketsBox.Text.Trim());
            Change("balloon", BalloonBox.Text.Trim());
        }
        else
        {
            Change("cpulimit", CpuLimitBox.Text.Trim());
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
            // 비운 칸은 delete 로 보내 서버 기본값으로 되돌린다("memory=" 같은 빈 값은 서버가 거절한다)
            await _api.UpdateGuestConfigAsync(_guest.Node, _guest.Kind, _guest.VmId,
                Shared.ActionHelpers.UpdateForm(changes));
            // 다시 읽은 뒤에 써야 "불러왔습니다" 에 덮이지 않는다. 다시 읽기 실패 문구는 남긴다
            if (await LoadAsync()) SetStatus(Loc.T("GuestSettingsWindow_M06"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("AppSettingsWindow_M03", ex.Message));
            App.Log($"[설정] {_guest.Kind.Label()} {_guest.VmId} 저장 실패: {ex.Message}");
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