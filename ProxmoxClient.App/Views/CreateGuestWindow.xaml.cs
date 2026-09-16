using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views;

public partial class CreateGuestWindow : Window
{
    private const int MinVmid = 100;
    private const int MaxVmid = 999_999_999;
    private const int MinMemoryMib = 16;
    private const int MinRootPasswordLength = 5;
    private const string DefaultBridge = "vmbr0";

    private readonly ProxmoxApiClient _api;
    private readonly IReadOnlyList<PveStorage> _storages;
    private bool _busy;

    public CreateGuestWindow(ProxmoxApiClient api, IReadOnlyList<PveNode> nodes, IReadOnlyList<PveStorage> storages)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _api = api;
        _storages = storages;

        NodeBox.ItemsSource = nodes.Where(n => n.IsOnline).Select(n => n.Node).ToList();
        if (NodeBox.Items.Count > 0) NodeBox.SelectedIndex = 0;

        Loaded += async (_, _) => await SuggestVmidAsync();
    }

    private bool IsVm => RadioVm.IsChecked == true;

    private async Task SuggestVmidAsync()
    {
        try
        {
            if (await _api.GetNextVmIdAsync() is { } next) VmidBox.Text = next.ToString();
        }
        catch (Exception ex)
        {
            App.Log($"[생성] 다음 VMID 조회 실패: {ex.Message}");
        }
    }

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (VmPanel is null || CtPanel is null) return;

        VmPanel.Visibility = IsVm ? Visibility.Visible : Visibility.Collapsed;
        CtPanel.Visibility = IsVm ? Visibility.Collapsed : Visibility.Visible;
        DiskSizeBox.Text = IsVm ? "32" : "8";
        _ = LoadNodeChoicesAsync(); // 디스크 저장소 필터(images/rootdir)가 종류에 따라 달라짐
    }

    private void OnNodeChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = LoadNodeChoicesAsync();
    }

    private List<string> StoragesWithContent(string node, string content)
    {
        return _storages
            .Where(s => s.Node == node && s.Content.Contains(content, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Storage)
            .ToList();
    }

    private async Task LoadNodeChoicesAsync()
    {
        if (NodeBox.SelectedItem is not string node || DiskStorageBox is null) return;

        DiskStorageBox.ItemsSource = StoragesWithContent(node, IsVm ? "images" : "rootdir");
        DiskStorageBox.SelectedIndex = DiskStorageBox.Items.Count > 0 ? 0 : -1;

        var isoStorages = StoragesWithContent(node, "iso");
        IsoStorageBox.ItemsSource = isoStorages;
        IsoStorageBox.SelectedIndex = isoStorages.Count > 0 ? 0 : -1;

        var templateStorages = StoragesWithContent(node, "vztmpl");
        TemplateStorageBox.ItemsSource = templateStorages;
        TemplateStorageBox.SelectedIndex = templateStorages.Count > 0 ? 0 : -1;

        try
        {
            var current = BridgeBox.Text;
            var bridges = await _api.GetNodeBridgesAsync(node);
            BridgeBox.ItemsSource = bridges;
            BridgeBox.Text = bridges.Contains(current) ? current
                : bridges.Contains(DefaultBridge) ? DefaultBridge
                : bridges.FirstOrDefault() ?? current;
        }
        catch (Exception ex)
        {
            App.Log($"[생성] 브리지 목록 조회 실패: {ex.Message}");
        }
    }

    private async void OnIsoStorageChanged(object sender, SelectionChangedEventArgs e)
    {
        ComboChoices.Fill(IsoBox, [("", Loc.T("CreateGuestWindow_M01"))]);
        IsoBox.SelectedIndex = 0;
        if (NodeBox.SelectedItem is not string node || IsoStorageBox.SelectedItem is not string storage) return;

        try
        {
            var files = await _api.GetStorageContentAsync(node, storage, "iso");
            ComboChoices.Fill(IsoBox, new[] { ("", Loc.T("CreateGuestWindow_M01")) }
                .Concat(files.Select(f => (f.Volid, VolumeLabel(f.Volid)))));
            IsoBox.SelectedIndex = IsoBox.Items.Count > 1 ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("CreateGuestWindow_M02", ex.Message));
        }
    }

    private async void OnTemplateStorageChanged(object sender, SelectionChangedEventArgs e)
    {
        TemplateBox.Items.Clear();
        if (NodeBox.SelectedItem is not string node || TemplateStorageBox.SelectedItem is not string storage) return;

        try
        {
            var files = await _api.GetStorageContentAsync(node, storage, "vztmpl");
            ComboChoices.Fill(TemplateBox, files.Select(f => (f.Volid, VolumeLabel(f.Volid))));
            TemplateBox.SelectedIndex = TemplateBox.Items.Count > 0 ? 0 : -1;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("CreateGuestWindow_M03", ex.Message));
        }
    }

    /// <summary>"local:iso/debian-12.iso" → "debian-12.iso"</summary>
    private static string VolumeLabel(string volid)
    {
        var slash = volid.LastIndexOf('/');
        return slash >= 0 ? volid[(slash + 1)..] : volid;
    }

    private string? Validate(out int vmid, out int cores, out int memory, out int diskGb, out int swap)
    {
        swap = 0;
        cores = memory = diskGb = 0;
        if (!int.TryParse(VmidBox.Text, out vmid) || vmid is < MinVmid or > MaxVmid)
            return Loc.T("CreateGuest_VmidMin", MinVmid);

        if (!int.TryParse(CoresBox.Text, out cores) || cores < 1) return Loc.T("CreateGuest_CoresMin");

        if (!int.TryParse(MemoryBox.Text, out memory) || memory < MinMemoryMib)
            return Loc.T("CreateGuest_MemoryMin", MinMemoryMib);

        if (!int.TryParse(DiskSizeBox.Text, out diskGb) || diskGb < 1) return Loc.T("CreateGuest_DiskMin");

        if (!IsVm)
        {
            if (!int.TryParse(SwapBox.Text, out swap) || swap < 0) return Loc.T("CreateGuest_SwapInvalid");

            if (ComboChoices.Selected(TemplateBox).Length == 0) return Loc.T("CreateGuestWindow_M04");

            if (RootPasswordBox.Password.Length < MinRootPasswordLength)
                return Loc.T("CreateGuest_RootPasswordMin", MinRootPasswordLength);
        }

        return null;
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (NodeBox.SelectedItem is not string node)
        {
            SetStatus(Loc.T("CreateGuestWindow_M05"));
            return;
        }

        if (DiskStorageBox.SelectedItem is not string diskStorage)
        {
            SetStatus(Loc.T("CreateGuestWindow_M06"));
            return;
        }

        if (Validate(out var vmid, out var cores, out var memory, out var diskGb, out var swap) is { } error)
        {
            SetStatus(error);
            return;
        }

        var bridge = string.IsNullOrWhiteSpace(BridgeBox.Text) ? DefaultBridge : BridgeBox.Text.Trim();

        _busy = true;
        BtnCreate.IsEnabled = false;
        SetStatus(Loc.T("CreateGuestWindow_M07"));

        try
        {
            if (IsVm)
            {
                var iso = ComboChoices.Selected(IsoBox);
                await _api.CreateQemuAsync(
                    node, vmid, NameBox.Text.Trim(), cores, memory, iso.Length > 0 ? iso : null,
                    diskStorage, diskGb, bridge);
            }
            else
            {
                await _api.CreateLxcAsync(
                    node, vmid, NameBox.Text.Trim(), cores, memory, swap,
                    ComboChoices.Selected(TemplateBox), diskStorage, diskGb, bridge, IpBox.Text.Trim(),
                    RootPasswordBox.Password);
            }

            SetStatus(Loc.T("CreateGuestWindow_M08"));
            await Task.Delay(900);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("CreateGuestWindow_M09", ex.Message));
        }
        finally
        {
            _busy = false;
            BtnCreate.IsEnabled = true;
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}