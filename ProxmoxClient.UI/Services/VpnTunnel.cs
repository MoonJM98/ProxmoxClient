using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Core.Vpn;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Services;

/// <summary>
///     로그인 전에 여는 OpenVPN 터널(WPF MainViewModel.Vpn 과 같은 규칙) — 앱 전체에 하나. 프로필이 VPN 을 쓰면 먼저 터널을
///     열고, 다른 .ovpn 이 필요한 서버로 바꾸면 지금 터널을 끊고 새로 연다. 앱을 끝내면 끊는다.
///     데스크톱(Windows·Linux·macOS)은 openvpn 프로세스를 관리 인터페이스로 다룬다. Android 는 앱이 터널을 직접 만들 수 없어
///     (VpnService 와 OpenVPN 엔진이 필요) 지원하지 않는다 — OpenVPN for Android 등으로 먼저 연결하도록 안내한다.
/// </summary>
public static class VpnTunnel
{
    private static OpenVpnManager? _manager;

    public static bool IsSupported => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    public static OpenVpnManager Manager => _manager ??= new OpenVpnManager();

    public static VpnState State => _manager?.State ?? VpnState.Disconnected;

    /// <summary>프로필이 VPN 을 쓰면 터널을 연다(이미 같은 설정으로 열려 있으면 그대로). 실패하면 예외.</summary>
    public static async Task EnsureAsync(ConnectionProfile profile, Action<string>? status = null,
        CancellationToken ct = default)
    {
        if (!profile.UseVpn) return;
        if (!IsSupported) throw new OpenVpnException(L.T("Vpn_NotSupportedHere"));
        if (string.IsNullOrWhiteSpace(profile.VpnConfigPath))
            throw new OpenVpnException(L.T("ProfileEditor_VpnConfigRequired"));

        var vpn = Manager;
        // 다른 .ovpn 이 필요한 서버로 바꾸면 지금 터널을 끊는다 — 옛 터널로는 닿지 않거나 엉뚱한 곳에 닿는다
        if (vpn.State is VpnState.Connected or VpnState.Reconnecting or VpnState.Connecting &&
            !SameConfig(vpn.ConfigPath, profile.VpnConfigPath))
            await vpn.DisconnectAsync();

        if (vpn.State is VpnState.Connected or VpnState.Reconnecting) return;

        status?.Invoke(L.T("MainViewModel_M03"));
        KillStale();
        await vpn.ConnectAsync(profile.VpnConfigPath, profile.VpnExePath, ct);
        RememberPid(vpn.ProcessId);
    }

    /// <summary>앱이 강제로 끝나 남은 openvpn(지난번에 이 앱이 띄운 것)을 정리한다 — 두 터널이 경로를 다투지 않게.</summary>
    private static void KillStale()
    {
        try
        {
            if (!File.Exists(PidFile) || !int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid)) return;

            File.Delete(PidFile);
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.ProcessName.Contains("openvpn", StringComparison.OrdinalIgnoreCase)) process.Kill();
        }
        catch (Exception)
        {
            // 이미 끝났거나 다른 프로세스 — 그대로 둔다
        }
    }

    private static void RememberPid(int? pid)
    {
        try
        {
            if (pid is null) return;

            Directory.CreateDirectory(Path.GetDirectoryName(PidFile)!);
            File.WriteAllText(PidFile, pid.Value.ToString());
        }
        catch (Exception)
        {
            // 기록 실패는 무시 — 정상 종료 때는 Shutdown 이 끊는다
        }
    }

    private static string PidFile => Core.Settings.JsonSettingsStore<object>.InAppData("openvpn.pid");

    /// <summary>앱 종료 — 터널을 끊는다(남겨 두면 openvpn 프로세스가 계속 돈다).</summary>
    public static void Shutdown()
    {
        var vpn = _manager;
        if (vpn is null) return;

        try
        {
            // 앱 종료 이벤트는 동기라 잠깐(최대 5초)만 정상 종료를 기다린다 — 넘으면 Dispose 가 프로세스를 바로 끝낸다
            if (vpn.State is not VpnState.Disconnected) vpn.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // 끝내는 중 — 아래 Dispose 가 프로세스를 정리한다
        }

        vpn.Dispose();
        _manager = null;
        try
        {
            File.Delete(PidFile);
        }
        catch (Exception)
        {
            // 없거나 지울 수 없음 — 다음 연결이 확인한다
        }
    }

    private static bool SameConfig(string? a, string? b)
    {
        if (a is null || b is null) return false;

        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
