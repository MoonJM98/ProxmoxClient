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
        // 모바일은 같은 모양에 손가락 크기만 키운다(데스크톱은 WPF 와 같은 크기)
        if (!PlatformInfo.IsDesktop)
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
