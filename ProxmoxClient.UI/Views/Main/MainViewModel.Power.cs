using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Main;

/// <summary>
///     게스트 전원 동작과 작업 추적 — WPF MainViewModel.Power 와 같은 확인 규칙.
///     시작·재개만 묻지 않고, 나머지(종료·정지·재부팅·일시 정지·최대 절전·리셋)는 확인 창을 거친다.
///     서버 쓰기는 모두 <see cref="WriteConfirmation" /> 을 지난다 — 확인하지 않은 흐름은 통신 계층이 막는다.
/// </summary>
public sealed partial class MainViewModel
{
    [RelayCommand] private Task StartAsync() => RunPowerAsync(GuestPowerAction.Start);

    [RelayCommand] private Task ShutdownAsync() => RunPowerAsync(GuestPowerAction.Shutdown);

    [RelayCommand] private Task StopAsync() => RunPowerAsync(GuestPowerAction.Stop);

    [RelayCommand] private Task RebootAsync() => RunPowerAsync(GuestPowerAction.Reboot);

    [RelayCommand] private Task SuspendAsync() => RunPowerAsync(GuestPowerAction.Suspend);

    [RelayCommand] private Task ResumeAsync() => RunPowerAsync(GuestPowerAction.Resume);

    [RelayCommand] private Task HibernateAsync() => RunPowerAsync(GuestPowerAction.Hibernate);

    [RelayCommand] private Task ResetAsync() => RunPowerAsync(GuestPowerAction.Reset);

    /// <summary>선택한 게스트에 전원 동작을 건다.</summary>
    private Task RunPowerAsync(GuestPowerAction action)
    {
        return SelectedGuest is { } guest ? RunGuestPowerAsync(guest, action) : Task.CompletedTask;
    }

    /// <summary>게스트 전원 동작 — 확인(필요할 때) → 요청 → 작업 완료까지 상태 추적 → 상태 반영.</summary>
    private async Task RunGuestPowerAsync(PveResource guest, GuestPowerAction action)
    {
        if (IsBusy) return;

        // [종료 | ▾] 는 일시 정지된 게스트에도 보인다(▾ 에 재개·정지) — 종료는 켜진 게스트에만
        if (action == GuestPowerAction.Shutdown && !guest.IsRunning)
        {
            StatusMessage = L.T("GuestPower_ShutdownNeedsRunning", guest.VmId);
            return;
        }

        if (!GuestPowerRules.IsAvailable(action, guest)) return;

        var label = GuestPowerRules.Label(action);
        // WPF 는 시작·재개만 묻지 않는다. 나머지는 같은 문구로 한 번 더 묻는다
        var asked = action is not (GuestPowerAction.Start or GuestPowerAction.Resume);
        if (asked)
        {
            var ok = await AppServices.Dialogs.ConfirmAsync(L.T("TableTab_ConfirmTitle"),
                L.T("GuestPower_Confirm", guest.Kind.Label(), guest.VmId, guest.Name, label), label,
                action is GuestPowerAction.Stop or GuestPowerAction.Reset);
            if (!ok) return;
        }

        SetBusy(true);
        try
        {
            string upid;
            using (WriteConfirmation.Begin(label, confirmed: asked))
                upid = await StartPowerAsync(action, guest);

            StatusMessage = L.T("MainViewModel_M08", guest.Kind.Label(), guest.VmId, guest.Name, label);
            await TrackTaskAsync(upid, guest);
        }
        catch (WriteCancelledException)
        {
            // 사용자가 변경 확인을 거절함 — 상태 문구를 바꾸지 않는다
        }
        catch (Exception ex)
        {
            StatusMessage = L.T("MainViewModel_M09", label, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Task<string> StartPowerAsync(GuestPowerAction action, PveResource guest)
    {
        var api = Session.Api;
        return action switch
        {
            GuestPowerAction.Start => api.StartGuestAsync(guest.Node, guest.Kind, guest.VmId),
            GuestPowerAction.Shutdown => api.ShutdownGuestAsync(guest.Node, guest.Kind, guest.VmId),
            GuestPowerAction.Stop => api.StopGuestAsync(guest.Node, guest.Kind, guest.VmId),
            GuestPowerAction.Reboot => api.RebootGuestAsync(guest.Node, guest.Kind, guest.VmId),
            GuestPowerAction.Suspend => api.SuspendGuestAsync(guest.Node, guest.Kind, guest.VmId),
            GuestPowerAction.Resume => api.ResumeGuestAsync(guest.Node, guest.Kind, guest.VmId),
            GuestPowerAction.Hibernate => api.HibernateGuestAsync(guest.Node, guest.Kind, guest.VmId),
            GuestPowerAction.Reset => api.ResetGuestAsync(guest.Node, guest.VmId),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    /// <summary>작업이 끝날 때까지 1초마다 확인하고, 끝나면 그 게스트 상태부터 실시간으로 반영한다.</summary>
    private async Task TrackTaskAsync(string upid, PveResource guest)
    {
        if (_disposed) return;

        try
        {
            var task = await Session.Api.GetTaskStatusAsync(upid).ConfigureAwait(true);
            UpsertTask(task);
            while (task.IsRunning)
            {
                await Task.Delay(TaskPollInterval).ConfigureAwait(true);
                if (_disposed) return;

                task = await Session.Api.GetTaskStatusAsync(upid).ConfigureAwait(true);
                UpsertTask(task);
            }

            StatusMessage = task.IsOk
                ? L.T("MainViewModel_M10", task.Type, task.Id)
                : L.T("MainViewModel_M11", task.Type, task.Id, task.Status);

            // 전체 새로고침을 기다리지 않고 해당 게스트 상태부터 실시간으로 반영
            await ApplyLiveGuestStatusAsync(guest).ConfigureAwait(true);
            await RefreshDataAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            StatusMessage = L.T("MainViewModel_M12", ex.Message);
        }
    }

    /// <summary>게스트 실시간 상태를 즉시 반영하고, 클러스터 인덱스가 따라올 때까지 우선 적용 대상으로 둔다.</summary>
    private async Task ApplyLiveGuestStatusAsync(PveResource guest)
    {
        try
        {
            var status = await Session.Api.GetGuestCurrentStatusAsync(guest.Node, guest.Kind, guest.VmId)
                .ConfigureAwait(true);
            if (status.Length == 0) return;

            _liveStatusOverrides[guest.VmId] =
                (status, Environment.TickCount64 + (long)LiveStatusHoldTime.TotalMilliseconds);
            if (!string.Equals(guest.Status, status, StringComparison.OrdinalIgnoreCase)) guest.UpdateStatus(status);
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            // 실시간 조회 실패 — 다음 새로고침이 값을 맞춘다
        }
    }

    /// <summary>새로고침 직후 — 인덱스가 아직 늦은 게스트는 실시간 상태로 되돌리고, 따라왔거나 만료된 항목은 해제.</summary>
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

    /// <summary>작업 목록 한 줄을 갱신하거나 맨 위에 넣는다(행 객체 유지 — 선택·스크롤 보존).</summary>
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
            Tasks[index].CopyFrom(task);
            return;
        }

        Tasks.Insert(0, task);
        while (Tasks.Count > TaskListCapacity) Tasks.RemoveAt(Tasks.Count - 1);
    }
}
