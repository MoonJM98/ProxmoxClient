using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views;

namespace ProxmoxClient.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        // 처리하지 못한 예외(앱이 죽는 원인)를 기록 파일에 남긴다 — 설정 > 일반 > 진단에서 보거나 저장한다
        AppLog.InstallGlobalHandlers();
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) => AppLog.LogDispatcherException(e.Exception);
        L.InitializeCulture();
        AccessibleNames.Install();
        AvaloniaXamlLoader.Load(this);
        // 휴대폰·태블릿은 같은 모양에 손가락 크기만 키운다(데스크톱은 WPF 와 같은 크기)
        if (PlatformInfo.IsTouch)
            Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://ProxmoxClient.UI/"))
            {
                Source = new Uri("avares://ProxmoxClient.UI/Themes/Mobile.axaml")
            });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, _) => AppLifecycle.SetForeground(true);
            activatable.Deactivated += (_, _) => AppLifecycle.SetForeground(false);
        }

        switch (ApplicationLifetime)
        {
            case IActivityApplicationLifetime activity:
                // 액티비티가 다시 만들어져도 같은 셸(페이지 스택·연결된 터미널)을 다시 붙인다
                activity.MainViewFactory = () => AppShell.Shared;
                break;
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = AppShell.Shared;
                break;
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.Exit += (_, _) => VpnTunnel.Shutdown(); // 터널(openvpn 프로세스)을 남기지 않는다
                desktop.MainWindow = new Window
                {
                    Title = "Proxmox Client",
                    Width = 1280,
                    Height = 820,
                    MinWidth = 900,
                    MinHeight = 600,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ProxmoxClient.UI/Assets/app.ico"))),
                    Content = new AppShell()
                };
                break;
        }

        if (ScreenSecurity.IsSupported) _ = ApplyScreenSecurityAsync();
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>저장된 "화면 보호" 설정을 시작할 때 건다(지원하는 플랫폼만).</summary>
    private static async Task ApplyScreenSecurityAsync()
    {
        try
        {
            var settings = await new Core.Settings.AppSettingsStore().LoadAsync();
            ScreenSecurity.Set(settings.SecureScreen);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"screen security setting not loaded: {ex.Message}");
        }
    }
}
