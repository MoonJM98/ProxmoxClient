using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ProxmoxClient.Mobile.Localization;
using ProxmoxClient.Mobile.Services;
using ProxmoxClient.Mobile.Views;

namespace ProxmoxClient.Mobile;

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

        switch (ApplicationLifetime)
        {
            case IActivityApplicationLifetime activity:
                activity.MainViewFactory = () => new AppShell();
                break;
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new AppShell();
                break;
            case IClassicDesktopStyleApplicationLifetime desktop:
                // 개발용 — 데스크톱에서 휴대폰 크기 창으로 띄운다
                desktop.MainWindow = new Window
                {
                    Title = "Proxmox Client",
                    Width = 412,
                    Height = 860,
                    Content = new AppShell()
                };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
