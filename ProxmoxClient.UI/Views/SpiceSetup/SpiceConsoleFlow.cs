using System.ComponentModel;
using Avalonia.Threading;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.SpiceSetup;

/// <summary>
///     SPICE 콘솔 흐름 — 클라이언트 확인(없으면 설치를 묻고 설치 화면) → 서버에서 SPICE 연결 정보 → 클라이언트로 열기.
///     OS 별 차이는 <see cref="ISpiceLauncher" /> 가 맡는다. 콘솔 창과 메인 화면의 콘솔 메뉴가 함께 쓴다.
///     진행·결과는 <paramref name="report" /> 로 알린다(상태 줄).
/// </summary>
public static class SpiceConsoleFlow
{
    public static async Task LaunchAsync(ProxmoxApiClient api, string node, int vmid, string title,
        Action<string> report)
    {
        var launcher = SpiceLaunchers.Current;
        // 클라이언트의 감시 작업은 백그라운드에서 알려 오므로 화면 스레드로 넘긴다
        Action<string> fromAnyThread = text => Dispatcher.UIThread.Post(() => report(text));

        if (!await EnsureViewerAsync(launcher, report)) return;

        try
        {
            report(Loc.T("ConsoleWindow_M15"));
            var spice = await api.GetSpiceProxyAsync(node, vmid);

            var result = await launcher.OpenAsync(spice.ToVvFile(title), title, fromAnyThread);
            switch (result.State)
            {
                case SpiceOpenState.ViewerMissing:
                    report(Loc.T("ConsoleWindow_M22"));
                    break;
                case SpiceOpenState.Failed:
                    report(result.Message ?? Loc.T("ConsoleWindow_RemoteViewerFailed"));
                    break;
            }
        }
        catch (ProxmoxApiException ex)
        {
            report(Loc.T("ConsoleWindow_M17", ex.Message));
        }
        catch (Exception ex) when (ex is CertificateTrustException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or Win32Exception)
        {
            report(Loc.T("ConsoleWindow_M18", ex.Message));
        }
    }

    /// <summary>클라이언트가 있으면 true. 없으면 설치를 묻고, 설치 화면이 닫힌 뒤 다시 확인한다.</summary>
    private static async Task<bool> EnsureViewerAsync(ISpiceLauncher launcher, Action<string> report)
    {
        if (launcher.Probe().Available) return true;

        var install = await Ask.ConfirmAsync(Loc.T(launcher.Guide.InstallAskKey), Loc.T("ConsoleWindow_M20"));
        if (!install)
        {
            report(Loc.T("ConsoleWindow_M21"));
            return false;
        }

        var setup = new SpiceSetupPage(launcher);
        var closed = setup.Closed;
        await AppServices.Navigator.PushViewAsync(Loc.T(launcher.Guide.TitleKey), setup);
        await closed;

        if (launcher.Probe().Available) return true;

        report(Loc.T("ConsoleWindow_M22"));
        return false;
    }
}
