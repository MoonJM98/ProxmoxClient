using System.IO;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views;
using ProxmoxClient.Core.Settings;

namespace ProxmoxClient.App;

public partial class App : Application
{
    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProxmoxClient", "logs", $"{DateTime.Now:yyyy-MM-dd}.log");

    private void OnStartup(object sender, StartupEventArgs e)
    {
        InstallExceptionHandlers();
        ApplySavedLanguage();
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception.ToString());
            ThemedMessageBox.Show(
                Loc.T("App_M01", args.Exception.Message, LogFilePath),
                Loc.T("App_M02"), MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        ShutdownMode = ShutdownMode.OnExplicitShutdown; // 로그인 창이 마지막 창이어도 자동 종료 방지
        var login = new LoginWindow();
        if (login.ShowDialog() != true || login.ResultProfile is null)
        {
            Shutdown();
            return;
        }

        var main = new MainWindow(login.ResultProfile);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose; // 독립 콘솔 창이 남아 있어도 메인 창 종료 시 앱 종료
        main.Show();
    }

    /// <summary>첫 창(로그인)이 뜨기 전에 언어를 적용한다 — 설정을 못 읽어도 시스템 언어로 진행.</summary>
    private static void ApplySavedLanguage()
    {
        var language = AppSettings.AutoLanguage;
        try
        {
            language = new AppSettingsStore().LoadAsync().GetAwaiter().GetResult().Language;
        }
        catch (Exception ex)
        {
            Log($"[언어] 설정을 읽지 못해 시스템 언어를 사용합니다: {ex.Message}");
        }

        Loc.SetLanguage(language);
    }

    private static void InstallExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log(args.ExceptionObject.ToString() ?? string.Empty);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception.ToString());
            args.SetObserved();
        };
    }

    internal static void Log(string text)
    {
        try
        {
            var path = LogFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 로그 실패는 무시
        }
    }
}