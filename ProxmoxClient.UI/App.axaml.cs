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
        L.InitializeCulture();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, _) => AppLifecycle.SetForeground(true);
            activatable.Deactivated += (_, _) => AppLifecycle.SetForeground(false);
        }

        // 같은 뷰모델·화면 코드를 쓰고, 배치만 수명 주기(창이 있는 데스크톱 / 단일 화면 모바일)로 나눈다
        PlatformInfo.FormFactor = ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            ? FormFactor.Desktop
            : FormFactor.Mobile;

        switch (ApplicationLifetime)
        {
            case IActivityApplicationLifetime activity:
                activity.MainViewFactory = () => new AppShell();
                break;
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new AppShell();
                break;
            case IClassicDesktopStyleApplicationLifetime desktop:
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

        base.OnFrameworkInitializationCompleted();
    }
}
