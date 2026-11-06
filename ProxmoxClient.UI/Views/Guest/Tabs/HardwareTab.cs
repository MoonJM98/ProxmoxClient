using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Guest.Hardware;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Guest.Tabs;

/// <summary>
///     게스트 창의 하드웨어 "간편" 탭 — CPU·메모리·네트워크와 일반 설정을 바꾼다(WPF HardwareTab 과 같은 칸·저장 규칙).
///     WPF 의 XAML 대신 코드로 만든다. 칸은 412px 폭에 맞춰 세로로 쌓는다.
/// </summary>
public sealed class HardwareTab : UserControl
{
    private const string DefaultBridge = "vmbr0";
    private const string DefaultNicModel = "virtio";
    private const long MiB = 1024 * 1024;

    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private readonly TextBox _sockets = HardwareInput.Numeric(false, 140);
    private readonly TextBox _cores = HardwareInput.Numeric(false, 140);
    private readonly TextBox _cpuLimit = HardwareInput.Numeric(true, 140);
    private readonly TextBox _memory = HardwareInput.Numeric(false, 140);
    private readonly TextBox _balloon = HardwareInput.Numeric(false, 140);
    private readonly AutoCompleteBox _bridge = new()
    {
        Width = HardwareInput.PcWidth(220), HorizontalAlignment = HardwareInput.PcAlign, FilterMode = AutoCompleteFilterMode.None
    };
    private readonly ComboBox _nicModel = new() { Width = HardwareInput.PcWidth(260), HorizontalAlignment = HardwareInput.PcAlign };
    private readonly TextBox _mac = new() { Width = HardwareInput.PcWidth(220), HorizontalAlignment = HardwareInput.PcAlign };
    private readonly TextBox _vlan = HardwareInput.Numeric(false, 140);
    private readonly TextBox _ip = new();
    private readonly CheckBox _nicFirewall = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _status = new()
    {
        Classes = { "DialogStatus" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap
    };
    private readonly Control _rowSockets;
    private readonly Control _rowCores;
    private readonly Control _rowCpuLimit;
    private readonly Control _rowMemory;
    private readonly Control _rowBalloon;
    private readonly Control _rowNicModel;
    private readonly Control _rowMac;
    private readonly Control _rowIp;
    private readonly Button _save;
    private bool _busy;
    private bool _loaded;
    /// <summary>
    ///     대기 중 변경까지 반영한 설정(…/pending 의 Effective) — 하드웨어 화면과 같다. 현재 값(config)으로 채우면
    ///     재시작 후 적용될 변경을 모른 채 저장해 그 변경을 지워 버린다.
    /// </summary>
    private GuestPendingConfig _pending = GuestPendingConfig.Empty;

    /// <summary>불러온 직후 네트워크 칸으로 만든 net0 — 저장 때 이와 같으면 사용자가 칸을 건드리지 않은 것이다.</summary>
    private string _loadedNet0 = string.Empty;

    /// <summary>게스트가 있는 노드의 CPU·메모리 — 최대치 안내와 저장 전 검사에 쓴다(못 읽으면 null).</summary>
    private PveNodeStatus? _host;

    public HardwareTab(ProxmoxApiClient api, PveResource guest)
    {
        _api = api;
        _guest = guest;

        ComboChoices.Fill(_nicModel, ComboChoices.NicModels);

        _rowSockets = HardwareInput.Form(Loc.T("GuestSettingsWindow_11"), _sockets);
        _rowCores = HardwareInput.Form(Loc.T("CreateGuestWindow_08"), _cores);
        _rowCpuLimit = HardwareInput.Form(Loc.T("GuestSettingsWindow_12"), _cpuLimit, Loc.T("GuestSettingsWindow_13"));
        _rowMemory = HardwareInput.Form(Loc.T("CreateGuestWindow_09"), _memory);
        _rowBalloon = HardwareInput.Form(Loc.T("GuestSettingsWindow_14"), _balloon, Loc.T("GuestSettingsWindow_15"));
        _rowNicModel = HardwareInput.Form(Loc.T("GuestSettingsWindow_18"), _nicModel);
        _rowMac = HardwareInput.Form(Loc.T("GuestSettingsWindow_19"), _mac, Loc.T("GuestSettingsWindow_20"));
        _rowIp = HardwareInput.Form("IP", _ip, Loc.T("CreateGuestWindow_19"));

        // WPF: 저장 단추(강조색 · 최소 폭 120 · 여백 없음)
        _save = HardwareInput.Button(Loc.T("AppSettingsWindow_12"), "IconSave");
        _save.Classes.Add("AccentButton");
        _save.MinWidth = 120;
        _save.Click += async (_, _) => await OnSaveAsync();

        Content = BuildLayout();
        ApplyKindVisibility();

        AttachedToVisualTree += async (_, _) =>
        {
            if (_loaded) return;

            _loaded = true;
            await LoadAsync();
            await LoadHostLimitsAsync();
        };
    }

    private bool IsVm => _guest.Kind == ResourceKind.Qemu;

    private Control BuildLayout()
    {
        // WPF: 데스크톱은 칸 줄을 FormRow 로 두고 여백 20,16 · 저장 줄의 i 아이콘(InfoTip)을 쓴다.
        // 모바일은 412px 폭에 맞춰 세로로 쌓고 설명은 본문 아래 글로 둔다.
        var desktop = PlatformInfo.IsDesktop;
        var body = new StackPanel
        {
            Margin = desktop ? new Thickness(20, 16, 20, 10) : new Thickness(16, 12, 16, 10),
            Spacing = desktop ? 0 : 10
        };
        body.Children.Add(Section(Loc.T("CreateGuestWindow_07")));
        body.Children.Add(_rowSockets);
        body.Children.Add(_rowCores);
        body.Children.Add(_rowCpuLimit);
        body.Children.Add(_rowMemory);
        body.Children.Add(_rowBalloon);
        body.Children.Add(Section(Loc.T("GuestSettingsWindow_16")));
        body.Children.Add(HardwareInput.Form(Loc.T("GuestSettingsWindow_17"), _bridge, Loc.T("CreateGuestWindow_24")));
        body.Children.Add(_rowNicModel);
        body.Children.Add(_rowMac);
        body.Children.Add(HardwareInput.Form(Loc.T("GuestSettingsWindow_21"), _vlan, Loc.T("GuestSettingsWindow_22")));
        body.Children.Add(_rowIp);
        body.Children.Add(HardwareInput.Form(Loc.T("FirewallWindow_01"), _nicFirewall));
        if (!desktop)
            body.Children.Add(new TextBlock
            {
                Text = Loc.T("GuestSettingsWindow_26"), Classes = { "subtle" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12
            });

        var footer = new DockPanel { Margin = desktop ? new Thickness(20, 0, 20, 16) : new Thickness(16, 0, 16, 12) };
        DockPanel.SetDock(_save, Dock.Right);
        footer.Children.Add(_save);
        if (desktop)
        {
            var tip = HardwareInput.InfoTip(Loc.T("GuestSettingsWindow_26"));
            DockPanel.SetDock(tip, Dock.Right);
            footer.Children.Add(tip);
        }

        footer.Children.Add(_status);

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = body
        });
        grid.Children.Add(footer);
        Grid.SetRow(footer, 1);
        return grid;
    }

    /// <summary>구역 제목 — WPF FormSection(데스크톱), 모바일은 기존 구역 글자.</summary>
    private static TextBlock Section(string text)
    {
        return new TextBlock { Text = text, Classes = { PlatformInfo.IsDesktop ? "FormSection" : "section" } };
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
            HardwareInput.SetHint(_rowCores, Loc.T("HostLimit_Cores", _host.CpuCores, Math.Max(_host.CpuSockets, 1)));
            var total = _host.MemTotalBytes;
            HardwareInput.SetHint(_rowMemory,
                Loc.T("HostLimit_Memory", total / MiB, Math.Max(total - _host.MemUsedBytes, 0) / MiB));
        }
        catch (Exception ex)
        {
            HardwareLog.Warn($"[설정] 노드 {_guest.Node} 자원 조회 실패: {ex.Message}");
        }
    }

    /// <summary>게스트 종류에 해당하지 않는 항목은 숨긴다(VM 전용 / CT 전용).</summary>
    private void ApplyKindVisibility()
    {
        _rowSockets.IsVisible = IsVm;
        _rowBalloon.IsVisible = IsVm;
        _rowNicModel.IsVisible = IsVm;
        _rowMac.IsVisible = IsVm;

        _rowCpuLimit.IsVisible = !IsVm;
        _rowIp.IsVisible = !IsVm;
    }

    /// <summary>설정을 다시 읽는다. 성공하면 true(실패 문구는 상태 줄에 남긴다).</summary>
    private async Task<bool> LoadAsync()
    {
        try
        {
            _pending = await _api.GetGuestPendingAsync(_guest.Node, _guest.Kind, _guest.VmId);
            await LoadBridgesAsync();

            _cores.Text = Get("cores");
            _sockets.Text = Get("sockets");
            _memory.Text = Get("memory");
            _balloon.Text = Get("balloon");
            _cpuLimit.Text = Get("cpulimit");

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
            _bridge.ItemsSource = await _api.GetNodeBridgesAsync(_guest.Node);
        }
        catch (Exception ex)
        {
            HardwareLog.Warn($"[설정] 브리지 목록 조회 실패: {ex.Message}");
        }
    }

    private string Get(string key)
    {
        return _pending.Effective.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private void ParseNet0(string net0)
    {
        _bridge.Text = DefaultBridge;
        ComboChoices.Select(_nicModel, DefaultNicModel);
        _ip.Text = IsVm ? "dhcp" : string.Empty; // CT 는 ip 가 없으면 빈칸(저장해도 ip=dhcp 를 붙이지 않는다)
        _mac.Text = string.Empty;
        _vlan.Text = string.Empty;
        _nicFirewall.IsChecked = false;
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
                ComboChoices.Select(_nicModel, key);
                _mac.Text = value;
                continue;
            }

            switch (key)
            {
                case "bridge":
                    _bridge.Text = value;
                    break;
                case "tag":
                    _vlan.Text = value;
                    break;
                case "ip":
                    _ip.Text = value;
                    break;
                case "firewall":
                    _nicFirewall.IsChecked = value == "1";
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
        return MergeNet0(Get("net0"), IsVm, new HardwareTab.NicInput(
            ComboChoices.Selected(_nicModel) is { Length: > 0 } m ? m : DefaultNicModel,
            _mac.Text?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(_bridge.Text) ? DefaultBridge : _bridge.Text!.Trim(),
            _vlan.Text?.Trim() ?? string.Empty,
            _nicFirewall.IsChecked == true,
            _ip.Text?.Trim() ?? string.Empty));
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
        if (!IsPositiveOrEmpty(_cores.Text ?? string.Empty) || (IsVm && !IsPositiveOrEmpty(_sockets.Text ?? string.Empty)))
            return Loc.T("GuestSettings_CoresMin");

        var memoryText = _memory.Text?.Trim() ?? string.Empty;
        if (memoryText.Length > 0 && (!int.TryParse(memoryText, out var memory) || memory < 16))
            return Loc.T("GuestSettings_MemoryMin");

        var vlanText = _vlan.Text?.Trim() ?? string.Empty;
        if (vlanText.Length > 0 && (!int.TryParse(vlanText, out var tag) || tag is < 1 or > 4094))
            return Loc.T("GuestSettings_VlanRange");

        if (IsVm && !GuestInputChecks.IsMacOrEmpty(_mac.Text ?? string.Empty))
            return Loc.T("HwEd_MacInvalid");

        // 벌룬(최소 메모리)이 메모리보다 크면 서버가 거절한다(0 은 끄기)
        if (IsVm && !GuestInputChecks.IsBalloonOk(_balloon.Text ?? string.Empty, memoryText.Length > 0
                ? memoryText
                : Get("memory")))
            return Loc.T("GuestSettings_BalloonTooLarge");

        if (!IsVm && !GuestInputChecks.IsCtIpv4(_ip.Text ?? string.Empty))
            return Loc.T("GuestSettings_CtIpInvalid");

        // 소켓 × 코어가 노드의 논리 CPU 수를 넘으면 Proxmox 가 시작을 거부한다
        if (_host is { CpuCores: > 0 } host && TotalVcpus() > host.CpuCores)
            return Loc.T("HostLimit_TooManyCores", TotalVcpus(), host.CpuCores);

        return null;
    }

    /// <summary>입력한 소켓 × 코어(빈 칸은 서버 기본값 1).</summary>
    private int TotalVcpus()
    {
        static int OrOne(string text) => int.TryParse(text, out var v) && v > 0 ? v : 1;
        var sockets = IsVm ? OrOne(_sockets.Text ?? string.Empty) : 1;
        return sockets * OrOne(_cores.Text ?? string.Empty);
    }

    private static bool IsPositiveOrEmpty(string text)
    {
        return text.Trim().Length == 0 || (int.TryParse(text, out var value) && value >= 1);
    }

    private async Task OnSaveAsync()
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

        Change("cores", (_cores.Text ?? string.Empty).Trim());
        Change("memory", (_memory.Text ?? string.Empty).Trim());
        // 네트워크 칸을 건드리지 않았으면 net0 을 보내지 않는다(장치가 없는 게스트에 새로 생기지 않게)
        if (BuildNet0() != _loadedNet0) Change("net0", BuildNet0());

        if (IsVm)
        {
            Change("sockets", (_sockets.Text ?? string.Empty).Trim());
            Change("balloon", (_balloon.Text ?? string.Empty).Trim());
        }
        else
        {
            Change("cpulimit", (_cpuLimit.Text ?? string.Empty).Trim());
        }

        if (changes.Count == 0)
        {
            SetStatus(Loc.T("GuestSettingsWindow_M04"));
            return;
        }

        _busy = true;
        _save.IsEnabled = false;
        SetStatus(Loc.T("GuestSettingsWindow_M05", changes.Count));

        try
        {
            // 비운 칸은 delete 로 보내 서버 기본값으로 되돌린다("memory=" 같은 빈 값은 서버가 거절한다)
            using var flow = WriteConfirmation.Begin(Loc.T("GuestTab_Hardware"));
            // 읽을 때 받은 digest — 그 사이 다른 곳에서 설정이 바뀌었으면 서버가 거절한다(덮어쓰지 않음)
            await _api.UpdateGuestConfigAsync(_guest.Node, _guest.Kind, _guest.VmId,
                _pending.AddDigest(ActionHelpers.UpdateForm(changes)));
            // 다시 읽은 뒤에 써야 "불러왔습니다" 에 덮이지 않는다. 다시 읽기 실패 문구는 남긴다
            if (await LoadAsync()) SetStatus(Loc.T("GuestSettingsWindow_M06"));
        }
        catch (WriteCancelledException)
        {
            SetStatus(string.Empty); // 확인 창에서 취소 — 저장하지 않았다
        }
        catch (ProxmoxApiException ex) when (ex.IsDigestMismatch)
        {
            // 다른 곳에서 바뀐 설정을 다시 읽어 칸을 채운 뒤 알린다
            if (await LoadAsync()) SetStatus(Loc.T("Digest_Changed"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("AppSettingsWindow_M03", ex.Message));
            HardwareLog.Warn($"[설정] {_guest.Kind.Label()} {_guest.VmId} 저장 실패: {ex.Message}");
        }
        finally
        {
            _busy = false;
            _save.IsEnabled = true;
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
    }
}
