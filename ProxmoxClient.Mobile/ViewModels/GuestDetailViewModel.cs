using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Mobile.Localization;
using ProxmoxClient.Mobile.Services;

namespace ProxmoxClient.Mobile.ViewModels;

/// <summary>게스트(VM·CT) 상세 — 상태·사용량, 전원 작업, IP 주소, 스냅숏, 설정.</summary>
public sealed partial class GuestDetailViewModel : ViewModelBase
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(4);

    /// <summary>PVE 스냅숏 이름 규칙 — 영문자로 시작, 영숫자·밑줄·하이픈(최대 40자).</summary>
    private static readonly Regex SnapshotName = new("^[A-Za-z][A-Za-z0-9_-]{1,39}$", RegexOptions.CultureInvariant);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly PeriodicRefresher _refresher;
    private bool _detailsLoaded;

    public GuestDetailViewModel(ServerSession session, PveResource guest)
    {
        Session = session;
        Guest = guest;
        Status = guest.Status;
        _refresher = new PeriodicRefresher(RefreshInterval, RefreshStatusAsync);
    }

    public ServerSession Session { get; }

    /// <summary>클러스터 목록과 같은 객체 — 여기서 고친 값이 목록에도 바로 보인다.</summary>
    public PveResource Guest { get; }

    public string Title => $"{Guest.VmId} · {Guest.Name}";

    public string KindText => Guest.Kind == ResourceKind.Qemu ? L.T("Guest_KindVm") : L.T("Guest_KindCt");

    public bool IsQemu => Guest.Kind == ResourceKind.Qemu;

    /// <summary>실시간 상태(일시정지 VM 은 "paused").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanStart), nameof(CanShutdown), nameof(CanReboot),
        nameof(CanStop), nameof(CanSuspend), nameof(CanResume), nameof(CanReset), nameof(IsRunning))]
    public partial string Status { get; private set; }

    public string StatusText => StatusLabel(Status);

    public bool IsRunning => Status == "running";

    public bool CanStart => !IsTaskRunning && Status == "stopped" && !Guest.IsTemplate;

    public bool CanShutdown => !IsTaskRunning && IsRunning;

    public bool CanReboot => !IsTaskRunning && IsRunning;

    public bool CanStop => !IsTaskRunning && (IsRunning || Status == "paused");

    public bool CanSuspend => !IsTaskRunning && IsRunning && IsQemu;

    public bool CanResume => !IsTaskRunning && Status is "paused" or "suspended";

    public bool CanReset => !IsTaskRunning && IsRunning && IsQemu;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanShutdown), nameof(CanReboot), nameof(CanStop),
        nameof(CanSuspend), nameof(CanResume), nameof(CanReset))]
    public partial bool IsTaskRunning { get; private set; }

    [ObservableProperty] public partial string? TaskText { get; private set; }

    public ObservableCollection<string> Addresses { get; } = [];

    [ObservableProperty] public partial string AddressHint { get; private set; } = string.Empty;

    public ObservableCollection<PveSnapshot> Snapshots { get; } = [];

    [ObservableProperty] public partial bool HasNoSnapshots { get; private set; }

    public ObservableCollection<ConfigEntry> Config { get; } = [];

    public override void OnAppearing()
    {
        _refresher.Start();
        _ = RefreshStatusAsync();
        if (!_detailsLoaded)
        {
            _detailsLoaded = true;
            _ = LoadDetailsAsync();
        }
    }

    public override void OnDisappearing(bool removed)
    {
        _refresher.Stop();
        if (!removed) return;

        _refresher.Dispose();
        _lifetime.Cancel();
    }

    private CancellationToken Lifetime => _lifetime.Token;

    private async Task RefreshStatusAsync()
    {
        try
        {
            var resources = await Session.Api.GetClusterResourcesAsync(Lifetime);
            if (resources.FirstOrDefault(r => r.VmId == Guest.VmId) is { } latest) Guest.CopyFrom(latest);

            Status = await Session.Api.GetGuestCurrentStatusAsync(Guest.Node, Guest.Kind, Guest.VmId, Lifetime)
                is { Length: > 0 } current
                ? current
                : Guest.Status;
        }
        catch (Exception) when (Lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // 일시적 실패는 다음 주기에 다시 — 상세 화면은 마지막 값을 유지한다
        }
    }

    [RelayCommand]
    private Task Reload()
    {
        return Task.WhenAll(RefreshStatusAsync(), LoadDetailsAsync());
    }

    private async Task LoadDetailsAsync()
    {
        await Task.WhenAll(LoadAddressesAsync(), LoadSnapshotsAsync(), LoadConfigAsync());
    }

    private async Task LoadAddressesAsync()
    {
        if (!Guest.IsRunning)
        {
            Addresses.Clear();
            AddressHint = L.T("Guest_AddressesStopped");
            return;
        }

        try
        {
            var addresses = await Session.Api.GetGuestAddressesAsync(Guest.Node, Guest.Kind, Guest.VmId, Lifetime);
            Addresses.Clear();
            foreach (var address in addresses) Addresses.Add(address);
            AddressHint = addresses.Count == 0
                ? IsQemu ? L.T("Guest_AddressesNoAgent") : L.T("Guest_AddressesNone")
                : string.Empty;
        }
        catch (Exception) when (!Lifetime.IsCancellationRequested)
        {
            AddressHint = IsQemu ? L.T("Guest_AddressesNoAgent") : L.T("Guest_AddressesNone");
        }
        catch
        {
        }
    }

    private async Task LoadSnapshotsAsync()
    {
        try
        {
            var snapshots = await Session.Api.GetSnapshotsAsync(Guest.Node, Guest.Kind, Guest.VmId, Lifetime);
            Snapshots.Clear();
            // "current" 는 현재 상태를 나타내는 가상 항목 — 되돌리거나 지울 수 없다
            foreach (var snapshot in snapshots.Where(s => s.Name != "current").OrderByDescending(s => s.SnapTimeUtc ?? DateTime.MinValue))
                Snapshots.Add(snapshot);
            HasNoSnapshots = Snapshots.Count == 0;
        }
        catch
        {
            HasNoSnapshots = Snapshots.Count == 0;
        }
    }

    private async Task LoadConfigAsync()
    {
        try
        {
            var config = await Session.Api.GetGuestConfigAsync(Guest.Node, Guest.Kind, Guest.VmId, Lifetime);
            Config.Clear();
            foreach (var (key, value) in config
                         .Where(pair => pair.Key is not "digest")
                         .OrderBy(pair => ConfigOrder(pair.Key))
                         .ThenBy(pair => pair.Key, StringComparer.Ordinal))
                Config.Add(new ConfigEntry(key, Uri.UnescapeDataString(value)));
        }
        catch
        {
        }
    }

    /// <summary>자주 보는 항목을 위로.</summary>
    private static int ConfigOrder(string key)
    {
        return key switch
        {
            "name" or "hostname" => 0,
            "description" => 1,
            "cores" or "sockets" or "cpu" or "memory" or "swap" or "balloon" => 2,
            "ostype" or "arch" => 3,
            _ when key.StartsWith("net", StringComparison.Ordinal) => 4,
            _ when key.StartsWith("rootfs", StringComparison.Ordinal) ||
                   key.StartsWith("scsi", StringComparison.Ordinal) ||
                   key.StartsWith("virtio", StringComparison.Ordinal) ||
                   key.StartsWith("sata", StringComparison.Ordinal) ||
                   key.StartsWith("ide", StringComparison.Ordinal) ||
                   key.StartsWith("mp", StringComparison.Ordinal) ||
                   key.StartsWith("efidisk", StringComparison.Ordinal) => 5,
            _ => 6
        };
    }

    // 서버 상태를 바꾸는 작업은 모두 ConfirmAndRunAsync 를 거친다 — 확인 없이 실행되는 경로가 없도록
    // 확인 문구(Confirmation)를 필수 인자로 받는다.

    [RelayCommand]
    private Task Start()
    {
        return RunPowerAsync(L.T("Power_Start"), L.T("Power_StartConfirm", Title),
            () => Session.Api.StartGuestAsync(Guest.Node, Guest.Kind, Guest.VmId, Lifetime));
    }

    [RelayCommand]
    private Task Shutdown()
    {
        return RunPowerAsync(L.T("Power_Shutdown"), L.T("Power_ShutdownConfirm", Title),
            () => Session.Api.ShutdownGuestAsync(Guest.Node, Guest.Kind, Guest.VmId, Lifetime));
    }

    [RelayCommand]
    private Task Reboot()
    {
        return RunPowerAsync(L.T("Power_Reboot"), L.T("Power_RebootConfirm", Title),
            () => Session.Api.RebootGuestAsync(Guest.Node, Guest.Kind, Guest.VmId, Lifetime));
    }

    [RelayCommand]
    private Task Stop()
    {
        return RunPowerAsync(L.T("Power_Stop"), L.T("Power_StopConfirm", Title),
            () => Session.Api.StopGuestAsync(Guest.Node, Guest.Kind, Guest.VmId, Lifetime), true);
    }

    [RelayCommand]
    private Task Suspend()
    {
        return RunPowerAsync(L.T("Power_Suspend"), L.T("Power_SuspendConfirm", Title),
            () => Session.Api.SuspendGuestAsync(Guest.Node, Guest.Kind, Guest.VmId, Lifetime));
    }

    [RelayCommand]
    private Task Resume()
    {
        return RunPowerAsync(L.T("Power_Resume"), L.T("Power_ResumeConfirm", Title),
            () => Session.Api.ResumeGuestAsync(Guest.Node, Guest.Kind, Guest.VmId, Lifetime));
    }

    [RelayCommand]
    private Task Reset()
    {
        return RunPowerAsync(L.T("Power_Reset"), L.T("Power_ResetConfirm", Title),
            () => Session.Api.ResetGuestAsync(Guest.Node, Guest.VmId, Lifetime), true);
    }

    [RelayCommand]
    private async Task CreateSnapshot()
    {
        var includeRamOption = IsQemu && IsRunning;
        var answer = await AppServices.Dialogs.PromptAsync(L.T("Snapshot_CreateTitle"), L.T("Snapshot_CreateText"),
            $"snap-{DateTime.Now:yyyyMMdd-HHmm}", watermark: L.T("Snapshot_Name"),
            checkLabel: includeRamOption ? L.T("Snapshot_IncludeRam") : null);
        if (answer is null) return;

        var name = answer.Text.Trim();
        if (!SnapshotName.IsMatch(name))
        {
            await AppServices.Dialogs.AlertAsync(L.T("Snapshot_CreateTitle"), L.T("Snapshot_NameInvalid"));
            return;
        }

        var includeRam = includeRamOption && answer.IsChecked;
        var ran = await ConfirmAndRunAsync(
            new Confirmation(L.T("Snapshot_CreateTitle"),
                L.T(includeRam ? "Snapshot_CreateConfirmRam" : "Snapshot_CreateConfirm", name, Title),
                L.T("Snapshot_Create")),
            L.T("Snapshot_Creating", name),
            () => Session.Api.CreateSnapshotAsync(Guest.Node, Guest.Kind, Guest.VmId, name, null, includeRam,
                Lifetime));
        if (ran) await LoadSnapshotsAsync();
    }

    [RelayCommand]
    private async Task RollbackSnapshot(PveSnapshot snapshot)
    {
        var ran = await ConfirmAndRunAsync(
            new Confirmation(L.T("Snapshot_RollbackTitle"), L.T("Snapshot_RollbackText", snapshot.Name, Title),
                L.T("Snapshot_Rollback"), true),
            L.T("Snapshot_RollingBack", snapshot.Name),
            () => Session.Api.RollbackSnapshotAsync(Guest.Node, Guest.Kind, Guest.VmId, snapshot.Name, Lifetime));
        if (ran) await RefreshStatusAsync();
    }

    [RelayCommand]
    private async Task DeleteSnapshot(PveSnapshot snapshot)
    {
        var ran = await ConfirmAndRunAsync(
            new Confirmation(L.T("Snapshot_DeleteTitle"), L.T("Snapshot_DeleteText", snapshot.Name),
                L.T("Common_Delete"), true),
            L.T("Snapshot_Deleting", snapshot.Name),
            () => Session.Api.DeleteSnapshotAsync(Guest.Node, Guest.Kind, Guest.VmId, snapshot.Name, Lifetime));
        if (ran) await LoadSnapshotsAsync();
    }

    private async Task RunPowerAsync(string action, string confirmText, Func<Task<string>> start,
        bool destructive = false)
    {
        var ran = await ConfirmAndRunAsync(new Confirmation(action, confirmText, action, destructive),
            L.T("Task_InProgress", action), start, action);
        if (!ran) return;

        await RefreshStatusAsync();
        if (IsRunning && Addresses.Count == 0) _ = LoadAddressesAsync();
    }

    /// <summary>
    ///     서버 상태를 바꾸는 작업의 유일한 실행 경로 — 먼저 확인을 받고, 승인하면 작업을 시작해 끝날 때까지
    ///     진행 표시줄을 보여 준다. 실패하면 서버가 남긴 상태를 알린다. 취소했거나 다른 작업이 진행 중이면 false.
    /// </summary>
    private async Task<bool> ConfirmAndRunAsync(Confirmation confirmation, string progressText,
        Func<Task<string>> start, string? doneText = null)
    {
        if (IsTaskRunning) return false;
        if (!await AppServices.Dialogs.ConfirmAsync(confirmation.Title, confirmation.Message,
                confirmation.ConfirmText, confirmation.Destructive))
            return false;
        if (IsTaskRunning) return false; // 확인을 기다리는 사이 다른 작업이 시작됨

        IsTaskRunning = true;
        TaskText = progressText;
        try
        {
            var upid = await start();
            if (upid.StartsWith("UPID:", StringComparison.Ordinal))
            {
                var result = await Session.Api.WaitTaskAsync(upid, TimeSpan.FromSeconds(1), Lifetime);
                if (result.IsOk)
                    AppServices.Dialogs.Toast(L.T("Task_Done", doneText ?? progressText));
                else
                    await AppServices.Dialogs.AlertAsync(L.T("Task_FailedTitle"), result.Status);
            }
        }
        catch (Exception) when (Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await AppServices.Dialogs.AlertAsync(L.T("Task_FailedTitle"), ErrorText.Describe(ex));
        }
        finally
        {
            IsTaskRunning = false;
            TaskText = null;
        }

        return true;
    }

    public static string StatusLabel(string status)
    {
        return status switch
        {
            "running" => L.T("Status_Running"),
            "stopped" => L.T("Status_Stopped"),
            "paused" => L.T("Status_Paused"),
            "suspended" => L.T("Status_Suspended"),
            "" => "-",
            _ => status
        };
    }

    [RelayCommand]
    private Task CopyText(string text)
    {
        return Views.ClipboardHelper.SetTextAsync(text);
    }
}

public sealed record ConfigEntry(string Key, string Value);

/// <summary>서버 작업 전 묻는 확인 대화상자 내용.</summary>
public sealed record Confirmation(string Title, string Message, string ConfirmText, bool Destructive = false);
