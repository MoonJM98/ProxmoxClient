using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.ViewModels;

/// <summary>게스트 전원 동작과 작업 추적 — 명령·즉시 상태 반영·완료 대기.</summary>
public partial class MainViewModel
{
    private void UpsertTask(PveTask task)
    {
        var index = -1;
        for (var i = 0; i < Tasks.Count; i++)
            if (string.Equals(Tasks[i].Upid, task.Upid, StringComparison.Ordinal))
            {
                index = i;
                break;
            }

        if (index >= 0)
        {
            Tasks[index].CopyFrom(task); // 행 교체(Replace) 대신 값 갱신 — 행 컨테이너·선택 유지
        }
        else
        {
            Tasks.Insert(0, task);
            while (Tasks.Count > TaskListCapacity) Tasks.RemoveAt(Tasks.Count - 1);
        }
    }
    private bool CanRunGuestPower()
    {
        return IsConnected && !IsBusy && SelectedGuest is { IsTemplate: false };
    }
    [RelayCommand(CanExecute = nameof(CanRunGuestPower))]
    private Task StartGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.StartGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Start"), SelectedGuest!,
            confirm: false);
    }
    [RelayCommand(CanExecute = nameof(CanRunGuestPower))]
    private Task StopGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.StopGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Stop"), SelectedGuest!);
    }
    [RelayCommand(CanExecute = nameof(CanRunGuestPower))]
    private Task ShutdownGuestAsync()
    {
        // [종료 | ▾] 는 일시 정지된 게스트에도 보인다(▾ 에 재개·정지) — 종료는 켜진 게스트에만
        if (SelectedGuest is { IsRunning: false } paused)
        {
            StatusMessage = Loc.T("GuestPower_ShutdownNeedsRunning", paused.VmId);
            return Task.CompletedTask;
        }

        return RunGuestPowerAsync(
            g => Api!.ShutdownGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Shutdown"), SelectedGuest!);
    }
    [RelayCommand(CanExecute = nameof(CanRunGuestPower))]
    private Task RebootGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.RebootGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Reboot"), SelectedGuest!);
    }
    [RelayCommand(CanExecute = nameof(CanSuspendGuest))]
    private Task SuspendGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.SuspendGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Suspend"), SelectedGuest!);
    }
    private bool CanSuspendGuest()
    {
        return IsConnected && SelectedGuest is { IsRunning: true, Kind: ResourceKind.Qemu };
    }
    [RelayCommand(CanExecute = nameof(CanResumeGuest))]
    private Task ResumeGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.ResumeGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Resume"), SelectedGuest!,
            confirm: false);
    }
    private bool CanResumeGuest()
    {
        return IsConnected && SelectedGuest is { Status: "paused", Kind: ResourceKind.Qemu };
    }
    [RelayCommand(CanExecute = nameof(CanHibernateGuest))]
    private Task HibernateGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.HibernateGuestAsync(g.Node, g.Kind, g.VmId), Loc.T("GuestPower_Hibernate"), SelectedGuest!);
    }
    private bool CanHibernateGuest()
    {
        return IsConnected && SelectedGuest is { IsRunning: true, Kind: ResourceKind.Qemu };
    }
    [RelayCommand(CanExecute = nameof(CanHibernateGuest))]
    private Task ResetGuestAsync()
    {
        return RunGuestPowerAsync(
            g => Api!.ResetGuestAsync(g.Node, g.VmId), Loc.T("GuestPower_Reset"), SelectedGuest!);
    }
    /// <summary>콘솔 창 등 외부에서 특정 게스트의 전원 동작 실행(선택된 게스트와 무관). 상태상 불가능한 동작은 무시.</summary>
    public Task RunGuestPowerForAsync(PveResource guest, GuestPowerAction action)
    {
        if (Api is not { } api || !IsConnected || !GuestPowerRules.IsAvailable(action, guest))
            return Task.CompletedTask;

        Func<PveResource, Task<string>>? operation = action switch
        {
            GuestPowerAction.Start => g => api.StartGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Shutdown => g => api.ShutdownGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Stop => g => api.StopGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Reboot => g => api.RebootGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Suspend => g => api.SuspendGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Resume => g => api.ResumeGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Hibernate => g => api.HibernateGuestAsync(g.Node, g.Kind, g.VmId),
            GuestPowerAction.Reset => g => api.ResetGuestAsync(g.Node, g.VmId),
            _ => null
        };

        return operation is null
            ? Task.CompletedTask
            : RunGuestPowerAsync(operation, GuestPowerRules.Label(action), guest,
                confirm: action is not (GuestPowerAction.Start or GuestPowerAction.Resume));
    }

    /// <summary>
    ///     게스트를 멈추거나 다시 시작하는 전원 동작 전에 한 번 더 묻는 창(메인 창이 넣는다) — 웹 UI 와 같다.
    ///     시작·재개는 묻지 않는다. null 이면 묻지 않는다.
    /// </summary>
    public Func<string, bool>? ConfirmPowerAction { get; set; }
    private async Task RunGuestPowerAsync(
        Func<PveResource, Task<string>> operation, string label, PveResource guest, bool confirm = true)
    {
        if (Api is null) return;
        if (confirm && ConfirmPowerAction is { } ask
                    && !ask(Loc.T("GuestPower_Confirm", guest.Kind.Label(), guest.VmId, guest.Name, label)))
            return;

        IsBusy = true;
        try
        {
            var upid = await operation(guest).ConfigureAwait(true);
            StatusMessage = Loc.T("MainViewModel_M08", guest.Kind.Label(), guest.VmId, guest.Name, label);
            await TrackTaskAsync(upid, guest).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.T("MainViewModel_M09", label, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
    /// <summary>
    ///     게스트 실시간 상태를 조회해 즉시 반영하고, 클러스터 인덱스가 따라올 때까지 우선 적용 대상으로 등록한다.
    ///     콘솔 창·상세 패널의 전원 버튼이 다음 새로고침을 기다리지 않고 바로 바뀐다.
    /// </summary>
    private async Task ApplyLiveGuestStatusAsync(PveResource guest)
    {
        if (Api is null) return;

        try
        {
            var status = await Api.GetGuestCurrentStatusAsync(guest.Node, guest.Kind, guest.VmId).ConfigureAwait(true);
            if (status.Length == 0) return;

            _liveStatusOverrides[guest.VmId] =
                (status, Environment.TickCount64 + (long)LiveStatusHoldTime.TotalMilliseconds);
            if (!string.Equals(guest.Status, status, StringComparison.OrdinalIgnoreCase)) guest.UpdateStatus(status);
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            App.Log($"[상태] {guest.Kind.Label()} {guest.VmId} 실시간 상태 조회 실패: {ex.Message}");
        }
    }
    /// <summary>새로고침 직후 호출 — 인덱스가 아직 늦은 게스트는 실시간 상태로 되돌리고, 따라왔거나 만료된 항목은 해제.</summary>
    private void ApplyLiveStatusOverrides()
    {
        if (_liveStatusOverrides.Count == 0) return;

        var now = Environment.TickCount64;
        var released = new List<int>();
        foreach (var (vmid, live) in _liveStatusOverrides)
        {
            var guest = Guests.FirstOrDefault(g => g.VmId == vmid);
            if (guest is null || now > live.ExpiresAt
                              || string.Equals(guest.Status, live.Status, StringComparison.OrdinalIgnoreCase))
            {
                released.Add(vmid);
                continue;
            }

            guest.UpdateStatus(live.Status);
        }

        foreach (var vmid in released) _liveStatusOverrides.Remove(vmid);
    }
    private async Task TrackTaskAsync(string upid, PveResource? guest = null)
    {
        if (Api is null) return;

        try
        {
            var task = await Api.GetTaskStatusAsync(upid).ConfigureAwait(true);
            UpsertTask(task);
            while (task.IsRunning)
            {
                await Task.Delay(TaskPollInterval).ConfigureAwait(true);
                if (Api is null) return;

                task = await Api.GetTaskStatusAsync(upid).ConfigureAwait(true);
                UpsertTask(task);
            }

            StatusMessage = task.IsOk
                ? Loc.T("MainViewModel_M10", task.Type, task.Id)
                : Loc.T("MainViewModel_M11", task.Type, task.Id, task.Status);

            // 전체 새로고침(진행 중이면 건너뜀)을 기다리지 않고 해당 게스트 상태부터 실시간으로 반영
            if (guest is not null) await ApplyLiveGuestStatusAsync(guest).ConfigureAwait(true);

            await RefreshDataAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            StatusMessage = Loc.T("MainViewModel_M12", ex.Message);
        }
    }
}
