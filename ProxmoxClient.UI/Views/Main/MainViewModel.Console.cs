using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Vnc;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Settings;
using ProxmoxClient.UI.Views.SpiceSetup;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views.Main;

/// <summary>
///     콘솔 단추 ▾ 메뉴(WPF MainWindow.ConsoleMenu 와 같은 규칙) — 메뉴를 열 때 VM 설정을 읽어 RDP·SPICE 항목을 보이고 끈다.
///     VNC 는 기본 항목(<see cref="OpenConsoleCommand" />), 직렬 터미널은 직렬 포트가 있을 때 켜진다.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>메뉴를 연 뒤 읽은 콘솔 종류(아직 못 읽었으면 null — 그동안 RDP·SPICE 항목은 숨긴다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRdpConsoleItem), nameof(ShowSpiceConsoleItem), nameof(SpiceConsoleEnabled),
        nameof(SerialConsoleEnabled))]
    internal partial GuestConsoleKinds? ConsoleKinds { get; set; }

    /// <summary>RDP 항목 — 디스플레이가 rdp 일 때 보인다(설정을 못 읽었으면 보여 직접 고르게 한다).</summary>
    public bool ShowRdpConsoleItem => ConsoleKinds is { } kinds && (kinds.Rdp || !kinds.Known);

    /// <summary>SPICE 항목 — 디스플레이가 qxl 일 때만 보인다.</summary>
    public bool ShowSpiceConsoleItem => ConsoleKinds?.Spice == true;

    /// <summary>SPICE 항목은 실행 중일 때만 누를 수 있다(WPF 와 같다).</summary>
    public bool SpiceConsoleEnabled => ShowSpiceConsoleItem && SelectedGuest is { IsRunning: true };

    public string SerialConsoleLabel => L.T("Console_KindSerial");

    /// <summary>직렬 터미널 — 직렬 포트가 있고 실행 중일 때(WPF 와 같다).</summary>
    public bool SerialConsoleEnabled => ConsoleKinds?.Serial == true && SelectedGuest is { IsRunning: true };

    [RelayCommand]
    private async Task OpenSerialConsoleAsync()
    {
        if (SelectedGuest is { } guest) await ConsoleLauncher.OpenSerialAsync(Session, guest);
    }

    /// <summary>메뉴를 열 때 호출 — 선택한 VM 의 설정을 읽어 콘솔 종류를 정한다.</summary>
    public async Task RefreshConsoleKindsAsync()
    {
        ConsoleKinds = null;
        if (SelectedGuest is not { } guest || guest.IsTemplate) return;

        var detected = await GuestConsoleKinds.DetectAsync(Session.Api, guest);
        if (ReferenceEquals(SelectedGuest, guest)) ConsoleKinds = detected; // 그 사이 다른 게스트를 골랐으면 버린다
    }

    [RelayCommand]
    private async Task OpenRdpConsoleAsync()
    {
        if (SelectedGuest is not { } guest) return;

        await AppServices.Navigator.PushAsync(new ConsoleViewModel(Session, guest, ConsoleProtocol.Rdp));
    }

    [RelayCommand]
    private async Task OpenSpiceConsoleAsync()
    {
        if (SelectedGuest is not { } guest) return;

        await SpiceConsoleFlow.LaunchAsync(Session.Api, guest.Node, guest.VmId,
            $"{guest.Kind.Label()} {guest.VmId} - {guest.Name}", text => StatusMessage = text);
    }

    /// <summary>[설정] — 앱 설정 화면. 저장하면 새로고침 간격 등을 다시 읽는다.</summary>
    [RelayCommand]
    private Task OpenAppSettingsAsync() => SettingsModule.OpenAppSettingsAsync(ReloadSettingsAsync);
}
