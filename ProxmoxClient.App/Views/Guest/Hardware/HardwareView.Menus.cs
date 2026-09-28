using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api.Domains;
using ProxmoxClient.Core.Api.Versioning;

namespace ProxmoxClient.App.Views.Guest.Hardware;

/// <summary>하드웨어 화면의 추가 메뉴·편집·디스크 작업 연결.</summary>
public partial class HardwareView
{
    private const int MaxNet = 32;
    private const int MaxPci = 16;
    private const int MaxSerial = 4;
    private const int MaxVirtiofs = 10;
    private const int MaxCtDevices = 256;

    /// <summary>추가 메뉴 — 웹 UI 순서. 자리가 없거나 이미 있는 장치(EFI·TPM·오디오·RNG·CloudInit)는 끈다.</summary>
    private void BuildAddMenu()
    {
        AddMenu.Items.Clear();
        if (_ctx is not { } ctx) return;

        if (_isCt)
        {
            AddItem("CtAdd_MountPoint", true, () => CtEditors.CreateMountPointAsync(ctx));
            AddItem("CtAdd_Device", ctx.Count("dev") < MaxCtDevices,
                () => Task.FromResult(CtEditors.Device(ctx, null)), Config(ctx, "dev0"));
            return;
        }

        var hasCloudInit = RowGrid.ItemsSource is IEnumerable<HardwareRow> rows
                           && rows.Any(r => r.Item == HardwareItem.CloudInit);
        var maxUsb = PassthroughEditors.MaxUsb(ctx.Get("ostype"), ctx.Get("machine"));
        AddItem("HwAdd_HardDisk", true, () => DiskEditors.CreateDiskAsync(ctx));
        AddItem("HwAdd_ImportDisk", true, () => DiskEditors.ImportDiskAsync(ctx));
        AddItem("HwAdd_Cdrom", true, () => DiskEditors.CdromAsync(ctx, null));
        AddItem("HwAdd_Network", ctx.Count("net") < MaxNet, () => DeviceEditors.NetworkAsync(ctx, null));
        AddItem("HwAdd_Efi", !Has(ctx, "efidisk0"), () => DiskEditors.EfiAsync(ctx));
        AddItem("HwAdd_Tpm", !Has(ctx, "tpmstate0"), () => DiskEditors.TpmAsync(ctx), Config(ctx, "tpmstate0"));
        AddItem("HwAdd_Usb", ctx.Count("usb") < maxUsb, () => PassthroughEditors.UsbAsync(ctx, null));
        AddItem("HwAdd_Pci", ctx.Count("hostpci") < MaxPci, () => PassthroughEditors.PciAsync(ctx, null));
        AddItem("HwAdd_Serial", ctx.Count("serial") < MaxSerial, () => Task.FromResult(DeviceEditors.Serial(ctx)));
        AddItem("HwAdd_CloudInit", !hasCloudInit, () => DiskEditors.CloudInitAsync(ctx));
        AddItem("HwAdd_Audio", !Has(ctx, "audio0"), () => Task.FromResult(DeviceEditors.Audio(ctx)));
        AddItem("HwAdd_Rng", !Has(ctx, "rng0"), () => Task.FromResult(DeviceEditors.Rng(ctx)));
        AddItem("HwAdd_Virtiofs", ctx.Count("virtiofs") < MaxVirtiofs,
            () => PassthroughEditors.VirtiofsAsync(ctx, null), Config(ctx, "virtiofs0"));
    }

    private static bool Has(HardwareContext ctx, string key)
    {
        return ctx.Config.Current.ContainsKey(key) || ctx.Effective.ContainsKey(key);
    }

    /// <summary>그 설정(장치)을 저장하는 기능 — 서버가 모르는 설정(예: 7.1 전의 tpmstate0)이면 메뉴에 두지 않는다.</summary>
    private static ApiFeature Config(HardwareContext ctx, string key)
    {
        return ctx.Api.Guests.Feature(nameof(GuestsApi.SetConfigAsync), key);
    }

    /// <param name="requires">메뉴가 쓰는 API 기능 — 서버가 못 쓰면 메뉴 항목을 두지 않는다.</param>
    private void AddItem(string labelKey, bool enabled, Func<Task<HardwareEdit>> open, ApiFeature? requires = null)
    {
        if (requires is { IsAvailable: false }) return;
        var item = new MenuItem { Header = Loc.T(labelKey), IsEnabled = enabled };
        item.Click += async (_, _) => await OpenEditorAsync(open);
        AddMenu.Items.Add(item);
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        OpenMenu(BtnAdd);
    }

    private void OnDiskClick(object sender, RoutedEventArgs e)
    {
        OpenMenu(BtnDisk);
    }

    /// <summary>버튼 아래에 메뉴를 연다(웹 UI 의 ▾ 버튼).</summary>
    private static void OpenMenu(Button button)
    {
        if (button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>편집 창에 필요한 목록(저장소·브리지 등)을 불러온 뒤 창을 연다. 불러오기 실패는 상태 줄에.</summary>
    private async Task OpenEditorAsync(Func<Task<HardwareEdit>> open)
    {
        if (_busy) return;

        if (await LoadDialogAsync(open) is { } edit) await ApplyEditAsync(edit);
    }

    /// <summary>
    ///     편집 창에 필요한 목록을 불러온다 — 그동안 다른 작업을 막고, 실패하면 상태 줄에 보인다(null).
    /// </summary>
    private async Task<T?> LoadDialogAsync<T>(Func<Task<T>> load) where T : class
    {
        _busy = true;
        UpdateButtons();
        try
        {
            StatusText.Text = Loc.T("Hw_Loading");
            var result = await load();
            StatusText.Text = string.Empty;
            return result;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("TableTab_ActionFailed", ex.Message);
            return null;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_busy || _ctx is not { } ctx || Selected is not { HasEditor: true } row) return;

        var key = row.Key;
        var open = _isCt ? CtEditor(ctx, row) : VmEditor(ctx, row, key);
        if (open is not null) await OpenEditorAsync(open);
    }

    /// <summary>CT 리소스 줄의 편집 창.</summary>
    private static Func<Task<HardwareEdit>>? CtEditor(HardwareContext ctx, HardwareRow row)
    {
        return row.Item switch
        {
            HardwareItem.Memory or HardwareItem.Swap => () => CtEditors.MemoryAsync(ctx),
            HardwareItem.Cores => () => CtEditors.CpuAsync(ctx),
            HardwareItem.RootFs or HardwareItem.MountPoint =>
                () => Task.FromResult(CtEditors.EditMountPoint(ctx, row.Key)),
            HardwareItem.Unused => () => Task.FromResult(CtEditors.AttachUnused(ctx, row.Key)),
            HardwareItem.Device => () => Task.FromResult(CtEditors.Device(ctx, row.Key)),
            _ => null
        };
    }

    /// <summary>VM 하드웨어 줄의 편집 창.</summary>
    private static Func<Task<HardwareEdit>>? VmEditor(HardwareContext ctx, HardwareRow row, string key)
    {
        return row.Item switch
        {
            HardwareItem.Memory => () => SystemEditors.MemoryAsync(ctx),
            HardwareItem.Processors => () => SystemEditors.ProcessorAsync(ctx),
            HardwareItem.Bios => () => Task.FromResult(SystemEditors.Bios(ctx)),
            HardwareItem.Display => () => Task.FromResult(SystemEditors.Display(ctx)),
            HardwareItem.Machine => () => SystemEditors.MachineAsync(ctx),
            HardwareItem.ScsiHw => () => Task.FromResult(SystemEditors.ScsiHw(ctx)),
            HardwareItem.Disk => () => Task.FromResult(DiskEditors.EditDisk(ctx, key)),
            HardwareItem.Unused => () => Task.FromResult(DiskEditors.AttachUnused(ctx, key)),
            HardwareItem.Cdrom => () => DiskEditors.CdromAsync(ctx, key),
            HardwareItem.Network => () => DeviceEditors.NetworkAsync(ctx, key),
            HardwareItem.Usb => () => PassthroughEditors.UsbAsync(ctx, key),
            HardwareItem.Pci => () => PassthroughEditors.PciAsync(ctx, key),
            HardwareItem.Audio => () => Task.FromResult(DeviceEditors.Audio(ctx)),
            HardwareItem.Rng => () => Task.FromResult(DeviceEditors.Rng(ctx)),
            HardwareItem.Virtiofs => () => PassthroughEditors.VirtiofsAsync(ctx, key),
            _ => null
        };
    }

    // ------------------------------------------------------------ 디스크 작업

    private async void OnMoveDisk(object sender, RoutedEventArgs e)
    {
        if (_busy || _ctx is not { } ctx || Selected is not { } row) return;
        if (await LoadDialogAsync(() => DiskActions.MoveAsync(ctx, row.Key)) is { } dialog)
            await ApplyDiskActionAsync(dialog.Edit, dialog.Request);
    }

    private async void OnReassignDisk(object sender, RoutedEventArgs e)
    {
        if (_busy || _ctx is not { } ctx || Selected is not { } row) return;
        if (await LoadDialogAsync(() => DiskActions.ReassignAsync(ctx, row.Key)) is { } dialog)
            await ApplyDiskActionAsync(dialog.Edit, dialog.Request);
    }

    private async void OnResizeDisk(object sender, RoutedEventArgs e)
    {
        if (_busy || _ctx is not { } ctx || Selected is not { IsUsedDisk: true } row) return;
        var dialog = DiskActions.Resize(ctx, row.Key);
        await ApplyDiskActionAsync(dialog.Edit, dialog.Request);
    }

    /// <summary>EFI 새 인증서 등록 — BitLocker 를 쓰면 복구 키가 필요할 수 있어 먼저 확인한다.</summary>
    private async void OnEnrollCerts(object sender, RoutedEventArgs e)
    {
        if (_busy || _ctx is not { } ctx || Selected?.Item != HardwareItem.Efi) return;
        var answer = ThemedMessageBox.Show(Window.GetWindow(this)!, Loc.T("Hw_EnrollConfirm"),
            Loc.T("Hw_DiskEnroll"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        await SendAsync(DiskActions.EnrollCertificates(ctx), background: true, Loc.T("GuestSettingsWindow_M06"));
    }
}
