using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.UI;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;
using ProxmoxClient.UI.Views;
using ProxmoxClient.UI.Views.Shared;

// 사용법: --port 8006 --out shots --mode desktop|mobile --target cluster|datacenter|node|storage|guest:100|lxc:200|all
var options = ParseArgs(args);
var port = int.Parse(options.GetValueOrDefault("port", "8006"));
var outDir = options.GetValueOrDefault("out", "shots");
var desktop = options.GetValueOrDefault("mode", "desktop") == "desktop";
var target = options.GetValueOrDefault("target", "all");
Directory.CreateDirectory(outDir);

CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture =
    CultureInfo.GetCultureInfo(options.GetValueOrDefault("culture", "ko-KR"));
var profileDir = Path.Combine(Path.GetTempPath(), "uisnap-" + Guid.NewGuid().ToString("N"));
AppServices.ProfilesDirectory = profileDir;
AppServices.SecretStore = new MemorySecretStore();
AppBuilder.Configure<App>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont().SetupWithoutStarting();
// 앱 초기화가 수명 주기로 화면 형태를 정하므로 그 뒤에 덮어쓴다
typeof(PlatformInfo).GetProperty(nameof(PlatformInfo.FormFactor))!
    .SetValue(null, desktop ? FormFactor.Desktop : FormFactor.Mobile);

var failures = 0;
Dispatcher.UIThread.UnhandledException += (_, e) =>
{
    failures++;
    Console.WriteLine($"EXCEPTION {e.Exception.GetType().Name}: {e.Exception.Message}");
    e.Handled = true;
};

var window = new Window
{
    Width = desktop ? 1280 : 412, Height = desktop ? 820 : 860, Content = new AppShell()
};
window.Show();
Pump(600);
var nav = window.GetVisualDescendants().OfType<NavigationPage>().First();
var shot = 0;

// 로그인(프로필 저장 → 연결 → 인증서 신뢰)
await AppServices.Profiles.SaveAsync(new ConnectionProfile { Name = "fake", Host = "127.0.0.1", Port = port });
AppServices.SecretStore.Set(SecretKey(), "secret");
var profiles = (ProfilesViewModel)nav.CurrentPage!.DataContext!;
await Wait(profiles.LoadAsync());
profiles.ConnectCommand.Execute(profiles.Profiles[0]);
WaitFor(() => AppServices.Dialogs.Current is not null || nav.CurrentPage?.DataContext is ClusterViewModel);
AppServices.Dialogs.Current?.ConfirmCommand.Execute(null); // 인증서 신뢰
WaitFor(() => nav.CurrentPage?.DataContext is ClusterViewModel, 15000);
if (nav.CurrentPage?.DataContext is not ClusterViewModel cluster)
{
    Console.WriteLine("FAIL login: " + AppServices.Dialogs.Current?.Message);
    return 1;
}

WaitFor(() => cluster.IsLoaded);
Shot("cluster");

if (target is "datacenter" or "all")
{
    if (FeatureRegistry.Datacenter is null) Console.WriteLine("SKIP datacenter: not registered");
    else
    {
        await Wait(cluster.OpenDatacenterCommand.ExecuteAsync(null));
        WalkNav("dc");
        await Wait(AppServices.Navigator.PopAsync());
    }
}

if (target is "node" or "all")
{
    if (FeatureRegistry.Node is null) Console.WriteLine("SKIP node: not registered");
    else
    {
        await Wait(AppServices.Navigator.PushViewAsync("node", FeatureRegistry.Node(cluster.Session, cluster.Nodes[0])));
        WalkNav("node");
        await Wait(AppServices.Navigator.PopAsync());
    }
}

if (target is "storage" or "all")
{
    if (FeatureRegistry.Storage is null) Console.WriteLine("SKIP storage: not registered");
    else
    {
        await Wait(cluster.OpenStorageCommand.ExecuteAsync(cluster.Storages[0]));
        WalkNav("storage");
        await Wait(AppServices.Navigator.PopAsync());
    }
}

if (target.StartsWith("preview:", StringComparison.Ordinal))
{
    var area = target["preview:".Length..];
    var context = new PreviewContext(cluster.Session, cluster.Guests.First(g => g.VmId == 100),
        cluster.Guests.First(g => g.VmId == 200), cluster.Nodes.First(n => n.Node == "pve1"),
        cluster.Storages.First(s => s.Storage == "local"));
    var tabs = PreviewRegistry.Get(area);
    if (tabs.Count == 0) Console.WriteLine($"SKIP preview:{area}: nothing registered");
    else
    {
        var navTabs = tabs.Select(t => new NavTab { Id = t.Id, LabelKey = t.LabelKey, IconKey = t.IconKey, Create = () => t.Create(context) })
            .ToList();
        new NavWindow(area, area, "IconList", navTabs).Show();
        WalkNav(area.Replace('/', '-'));
        if (area == "selftest" && desktop) SelfTestWrites();
        await Wait(AppServices.Navigator.PopAsync());
    }
}

foreach (var (prefix, vmid) in new[] { ("guest", 100), ("lxc", 200) })
{
    if (!(target == "all" || target.StartsWith(prefix, StringComparison.Ordinal))) continue;

    var id = target.Contains(':') ? int.Parse(target.Split(':')[1]) : vmid;
    var guest = cluster.Guests.FirstOrDefault(g => g.VmId == id);
    if (guest is null) continue;

    var detail = new GuestDetailViewModel(cluster.Session, guest);
    foreach (var entry in detail.ManagementEntries)
    {
        var feature = (GuestFeature)entry.Parameter!;
        await Wait(AppServices.Navigator.PushViewAsync(feature.Id, feature.Create(cluster.Session, guest)));
        Pump(1200);
        Shot($"{prefix}{id}-{feature.Id}");
        WalkSubTabs($"{prefix}{id}-{feature.Id}");
        CheckStatus($"{prefix}{id}-{feature.Id}");
        await Wait(AppServices.Navigator.PopAsync());
    }

    if (detail.ManagementEntries.Count == 0) Console.WriteLine($"SKIP {prefix}: no guest features registered");
}

Console.WriteLine(failures == 0 ? "DONE ok" : $"DONE failures={failures}");
Directory.Delete(profileDir, true);
return failures == 0 ? 0 : 2;

// ------------------------------------------------------------------ helpers

// 쓰기 확인 관문 점검: 폼 확인 뒤에도 실제 요청 전에 "변경 확인"이 뜨는지, 취소하면 요청이 안 나가는지
void SelfTestWrites()
{
    var table = window.GetVisualDescendants().OfType<TableTab>().Last();
    Button ButtonOf(string labelKey) => table.GetVisualDescendants().OfType<Button>()
        .First(b => ToolTip.GetTip(b) as string == L.T(labelKey));

    ButtonOf("Action_Add").Command?.Execute(null);
    ButtonOf("Action_Add").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    WaitFor(() => AppServices.Dialogs.Sheet is not null);
    Shot("selftest-form");
    var form = (FormDialog)AppServices.Dialogs.Sheet!;
    form.GetVisualDescendants().OfType<TextBox>().First().Text = "abc";
    form.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == L.T("Common_Ok"))
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    WaitFor(() => AppServices.Dialogs.Current is not null);
    Shot("selftest-write-confirm");
    Console.WriteLine("WRITE-GUARD after form: " + (AppServices.Dialogs.Current?.Title ?? "NO DIALOG (BUG)"));
    AppServices.Dialogs.Current?.CancelCommand.Execute(null); // 취소 → POST 가 나가면 안 된다
    Pump(800);

    var list = table.GetVisualDescendants().OfType<ListBox>().First();
    list.SelectedIndex = 0;
    Pump(200);
    ButtonOf("Action_Delete").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    WaitFor(() => AppServices.Dialogs.Current is not null);
    Console.WriteLine("DELETE confirm: " + (AppServices.Dialogs.Current?.Message ?? "NO DIALOG (BUG)"));
    AppServices.Dialogs.Current?.ConfirmCommand.Execute(null); // 확인 → 같은 흐름이라 다시 묻지 않고 DELETE
    Pump(1000);
    Console.WriteLine("after delete confirm, extra dialog: " + (AppServices.Dialogs.Current?.Title ?? "none"));
    AppServices.Dialogs.Current?.CancelCommand.Execute(null);
}

string SecretKey()
{
    var id = AppServices.Profiles.LoadAsync().GetAwaiter().GetResult()[0].Id;
    return $"profile.{id:N}.password";
}

void WalkNav(string name)
{
    Pump(800);
    var navWindow = window.GetVisualDescendants().OfType<NavWindow>().LastOrDefault();
    if (navWindow is null)
    {
        Shot(name);
        CheckStatus(name);
        return;
    }

    var list = navWindow.GetVisualDescendants().OfType<ListBox>().First();
    for (var i = 0; i < list.ItemCount; i++)
    {
        if (desktop)
        {
            list.SelectedIndex = i;
            Pump(1200);
            var label = TabLabel(list, i);
            Shot($"{name}-{i:00}-{label}");
            WalkSubTabs($"{name}-{i:00}");
            CheckStatus($"{name}/{label}");
        }
        else
        {
            var label = TabLabel(list, i);
            list.SelectedIndex = i; // 모바일: 탭 내용을 새 페이지로 연다
            Pump(1200);
            Shot($"{name}-{i:00}-{label}");
            CheckStatus($"{name}/{label}");
            Wait(AppServices.Navigator.PopAsync()).GetAwaiter().GetResult();
            Pump(300);
        }
    }
}

void WalkSubTabs(string name)
{
    var current = nav.CurrentPage as Control;
    var subTabs = current?.GetVisualDescendants().OfType<SubTabsView>().FirstOrDefault();
    if (subTabs is null) return;

    var chips = subTabs.GetVisualDescendants().OfType<RadioButton>().ToList();
    for (var i = 1; i < chips.Count; i++)
    {
        chips[i].IsChecked = true;
        Pump(1000);
        Shot($"{name}-sub{i}");
        CheckStatus($"{name}/sub{i}");
    }
}

string TabLabel(ListBox list, int i)
{
    var text = (list.ContainerFromIndex(i) as Control)?.GetVisualDescendants().OfType<TextBlock>()
        .Select(t => t.Text).LastOrDefault(t => !string.IsNullOrEmpty(t)) ?? i.ToString();
    return new string(text.Where(char.IsLetterOrDigit).ToArray());
}

// 표·편집 화면의 상태 줄에 실패 문구가 있으면 알린다
void CheckStatus(string name)
{
    var failPrefixes = new[] { L.T("MainViewModel_M07", ""), L.T("TableTab_ActionFailed", "") }
        .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    var page = nav.CurrentPage as Control;
    foreach (var text in page?.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "") ?? [])
        if (failPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)))
        {
            failures++;
            Console.WriteLine($"FAIL {name}: {text}");
        }

    if (AppServices.Dialogs.Current is { } dialog)
    {
        Console.WriteLine($"DIALOG {name}: {dialog.Title} / {dialog.Message}");
        dialog.CancelCommand.Execute(null);
    }
}

void Shot(string name)
{
    Pump(250);
    var file = Path.Combine(outDir, $"{++shot:000}-{name}.png");
    window.CaptureRenderedFrame()!.Save(file);
    Console.WriteLine("shot " + file);
}

void Pump(int ms)
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(10);
    }
}

void WaitFor(Func<bool> condition, int ms = 8000)
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (!condition() && DateTime.UtcNow < end) Pump(50);
}

async Task Wait(Task task)
{
    WaitFor(() => task.IsCompleted, 15000);
    if (task.IsCompleted) await task;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var map = new Dictionary<string, string>();
    for (var i = 0; i + 1 < args.Length; i += 2) map[args[i].TrimStart('-')] = args[i + 1];
    return map;
}
