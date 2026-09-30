using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Vpn;

namespace ProxmoxClient.App.ViewModels;

/// <summary>OpenVPN 연결 — 연결·끊기·상태와 로그 표시.</summary>
public partial class MainViewModel
{
    private bool CanVpnConnect()
    {
        return SelectedProfile is { UseVpn: true } or { VpnConfigPath: not null } &&
               _vpn.State is not (VpnState.Connected or VpnState.Connecting or VpnState.Reconnecting
                   or VpnState.Disconnecting) &&
               !IsBusy;
    }
    [RelayCommand(CanExecute = nameof(CanVpnConnect))]
    private async Task VpnConnectAsync()
    {
        if (SelectedProfile is not { } profile) return;

        IsBusy = true;
        try
        {
            await _vpn.ConnectAsync(profile.VpnConfigPath!, profile.VpnExePath).ConfigureAwait(true);
            StatusMessage = Loc.T("MainViewModel_M13");
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.T("MainViewModel_M14", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
    private bool CanVpnDisconnect()
    {
        return _vpn.State is VpnState.Connected or VpnState.Connecting or VpnState.Reconnecting or VpnState.Error &&
               !IsBusy;
    }
    [RelayCommand(CanExecute = nameof(CanVpnDisconnect))]
    private async Task VpnDisconnectAsync()
    {
        IsBusy = true;
        try
        {
            await _vpn.DisconnectAsync().ConfigureAwait(true);
            StatusMessage = Loc.T("MainViewModel_M15");
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.T("MainViewModel_M16", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
    private void HandleVpnStateChanged(VpnState state)
    {
        RunOnUi(() =>
        {
            VpnState = state;
            VpnVirtualIp = _vpn.VirtualIp;
            VpnStatusText = DescribeVpnState(state, _vpn.LastError);
            VpnConnectCommand.NotifyCanExecuteChanged();
            VpnDisconnectCommand.NotifyCanExecuteChanged();
        });
    }
    private static string DescribeVpnState(VpnState state, string? lastError)
    {
        return state switch
        {
            VpnState.Disconnected => Loc.T("Vpn_NotConnected"),
            VpnState.Connecting => Loc.T("ConsoleWindow_06"),
            VpnState.Connected => Loc.T("ConsoleWindow_M08"),
            VpnState.Reconnecting => Loc.T("Vpn_Reconnecting"),
            VpnState.Disconnecting => Loc.T("Vpn_Disconnecting"),
            VpnState.Error => Loc.T("Vpn_Error", lastError ?? Loc.T("Vpn_UnknownError")),
            _ => state.ToString()
        };
    }
    private void OnVpnLogReceived(string line)
    {
        RunOnUi(() =>
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            VpnLogText = VpnLogText.Length + line.Length > VpnLogCapacityChars
                ? $"[{stamp}] {line}\n"
                : VpnLogText + $"[{stamp}] {line}\n";
        });
    }
    private void OnVpnStatsUpdated(long bytesIn, long bytesOut)
    {
        RunOnUi(() =>
        {
            VpnBytesIn = bytesIn;
            VpnBytesOut = bytesOut;
        });
    }
}
