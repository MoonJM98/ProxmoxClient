#if DEBUG
using System.Text;
using Android.Content;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Core.Settings;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;
using ProxmoxClient.UI.Views;
using ProxmoxClient.UI.Views.Main;
using ProxmoxClient.UI.Views.Terminal;

namespace ProxmoxClient.Droid;

/// <summary>
///     디버그 빌드 전용 자동 검사 고리(에뮬레이터 끝까지 검사, .github/workflows/android-e2e.yml) — 배포 빌드에는 없다.
///     adb 로 방송을 보내 앱을 움직이고, 결과는 logcat 의 PCTEST 태그로 남긴다.
///     받는 쪽은 android.permission.DUMP 를 가진 보낸 이(adb shell·시스템)만 받는다 — 다른 앱은 보낼 수 없다.
///     <code>adb shell am broadcast -a io.github.moonjm98.proxmoxclient.debug.TEST -p io.github.moonjm98.proxmoxclient --es cmd state</code>
/// </summary>
internal static class DebugTestHook
{
    public const string Tag = "PCTEST";
    private const string Action = "io.github.moonjm98.proxmoxclient.debug.TEST";

    public static void Register(Context context)
    {
        ContextCompat.RegisterReceiver(context, new Receiver(), new IntentFilter(Action), "android.permission.DUMP", null,
            ContextCompat.ReceiverExported);
        Log("hook registered");
    }

    public static void Log(string text)
    {
        Android.Util.Log.Info(Tag, text);
    }

    private sealed class Receiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent is null) return;

            var cmd = intent.GetStringExtra("cmd") ?? "state";
            var extras = new Dictionary<string, string?>();
            foreach (var key in intent.Extras?.KeySet() ?? [])
                extras[key] = intent.Extras!.Get(key)?.ToString();
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await RunAsync(cmd, extras);
                    Log($"OK {cmd}");
                }
                catch (Exception ex)
                {
                    Log($"ERR {cmd} {ex.GetType().Name}: {ex.Message}");
                }
            });
        }
    }

    private static string Arg(Dictionary<string, string?> extras, string key, string fallback = "")
    {
        return extras.TryGetValue(key, out var value) && value is not null ? value : fallback;
    }

    private static async Task RunAsync(string cmd, Dictionary<string, string?> extras)
    {
        switch (cmd)
        {
            case "setup":
                await SetupAsync(Arg(extras, "host", "10.0.2.2"), int.Parse(Arg(extras, "port", "8006")),
                    Arg(extras, "user", "root@pam"), Arg(extras, "password", "secret"));
                break;
            case "connect":
                await ConnectAsync();
                break;
            case "shell":
                await OpenShellAsync(Arg(extras, "node", "pve1"));
                break;
            case "type":
                var text = extras.ContainsKey("b64")
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(Arg(extras, "b64")))
                    : Arg(extras, "text");
                if (Arg(extras, "enter") == "true") text += "\r";
                Terminal().Terminal.Send(text, false);
                break;
            case "reconnect":
                await TerminalVm().ConnectAsync();
                break;
            case "disconnect":
                await TerminalVm().DisconnectAsync();
                break;
            case "ime":
                Terminal().Terminal.FocusInput();
                break;
            case "secure":
                var on = Arg(extras, "on") == "true";
                var store = new AppSettingsStore();
                var settings = await store.LoadAsync();
                await store.SaveAsync(settings with { SecureScreen = on });
                ScreenSecurity.Set(on);
                break;
            case "dialog":
                _ = AppServices.Dialogs.PromptAsync("E2E", "Dialog overlay safe-area check", watermark: "input");
                break;
            case "dismiss":
                AppServices.Dialogs.TryCancelCurrent();
                AppServices.Dialogs.CloseSheet();
                break;
            case "root":
                await AppServices.Navigator.PopToRootAsync();
                break;
            case "screen":
                foreach (var line in ScreenLines()) Log("screen| " + line);
                break;
            case "state":
                break;
            default:
                throw new ArgumentException("unknown command");
        }

        LogState();
    }

    private static async Task SetupAsync(string host, int port, string user, string password)
    {
        var existing = (await AppServices.Profiles.LoadAsync()).FirstOrDefault(p => p.Host == host && p.Port == port);
        var profile = existing ?? new ConnectionProfile { Name = "fake", Host = host, Port = port, UserName = user };
        if (existing is null) await AppServices.Profiles.SaveAsync(profile);
        AppServices.Profiles.RememberSecret(profile, password);
        if (AppServices.Navigator.CurrentPage?.DataContext is ProfilesViewModel profiles) await profiles.LoadAsync();
    }

    /// <summary>첫 서버에 연결 — 처음 보는 인증서 질문은 신뢰로 답한다.</summary>
    private static async Task ConnectAsync()
    {
        if (AppServices.Navigator.CurrentPage?.DataContext is not ProfilesViewModel profiles)
            throw new InvalidOperationException("not on the profile list");

        var connect = profiles.ConnectCommand.ExecuteAsync(profiles.Profiles[0]);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!connect.IsCompleted && DateTime.UtcNow < deadline)
        {
            if (AppServices.Dialogs.Current is { } dialog)
            {
                Log($"confirm dialog: {dialog.Title} / {dialog.Message.Replace('\n', ' ')}");
                dialog.ConfirmCommand.Execute(null);
            }

            await Task.Delay(200);
        }

        await connect;
        if (Session() is null) throw new InvalidOperationException("not logged in");
    }

    private static async Task OpenShellAsync(string node)
    {
        var session = Session() ?? throw new InvalidOperationException("not logged in");
        await ConsoleLauncher.OpenNodeShellAsync(session.Api, node);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && AppServices.Navigator.CurrentPage?.DataContext is not TerminalViewModel { IsConnected: true })
            await Task.Delay(200);
    }

    private static ServerSession? Session()
    {
        return AppServices.Navigator.CurrentPage?.DataContext switch
        {
            ClusterViewModel cluster => cluster.Session,
            MainViewModel main => main.Session,
            _ => null
        };
    }

    private static TerminalPage Terminal()
    {
        return AppServices.Navigator.CurrentPage as TerminalPage ?? throw new InvalidOperationException("not on a terminal page");
    }

    private static TerminalViewModel TerminalVm()
    {
        return Terminal().DataContext as TerminalViewModel ?? throw new InvalidOperationException("no terminal view model");
    }

    private static List<string> ScreenLines()
    {
        if (AppServices.Navigator.CurrentPage is not TerminalPage page) return [];

        var buffer = page.Terminal.Buffer;
        lock (buffer.Gate)
            return Enumerable.Range(0, buffer.Rows)
                .Select(r => new string(buffer.GetVisibleLine(r, 0).Cells.Where(c => c.Width != 0)
                    .SelectMany(c => char.ConvertFromUtf32(c.Rune == 0 ? ' ' : c.Rune)).ToArray()).TrimEnd())
                .Where(line => line.Length > 0).ToList();
    }

    /// <summary>한 줄 상태: 페이지 형식·제목·터미널 연결·마지막 화면 줄·안전 영역·키보드 여백·창 보호.</summary>
    private static void LogState()
    {
        var page = AppServices.Navigator.CurrentPage;
        var shell = page?.FindAncestorOfType<AppShell>();
        var top = page is null ? null : TopLevel.GetTopLevel(page);
        var terminal = page?.DataContext as TerminalViewModel;
        var last = ScreenLines().LastOrDefault() ?? "";
        var origin = page is not null && top is not null ? page.TranslatePoint(new Avalonia.Point(0, 0), top) : null;
        Log($"state page={page?.GetType().Name ?? "none"} vm={page?.DataContext?.GetType().Name ?? "none"} " +
            $"title=\"{(page?.DataContext as ViewModelBase)?.Title}\" " +
            $"terminal={(terminal is null ? "none" : terminal.IsConnected ? "connected" : "disconnected")} " +
            $"dialog={AppServices.Dialogs.Current is not null} sheet={AppServices.Dialogs.IsSheetOpen} " +
            $"safe={top?.InsetsManager?.SafeAreaPadding} kb={shell?.KeyboardInset ?? -1} " +
            $"top={top?.Bounds.Width}x{top?.Bounds.Height} page@{origin} {page?.Bounds.Size} " +
            $"secure={ScreenSecurity.Enabled} activity={(MainActivity.Current is null ? "none" : "alive")} " +
            $"last=\"{last}\"");
    }
}
#endif
