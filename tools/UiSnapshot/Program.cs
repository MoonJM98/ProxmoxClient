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
using ProxmoxClient.UI.Views.Console;

// 사용법: --port 8006 --out shots --mode desktop|mobile --target cluster|datacenter|node|storage|guest:100|lxc:200|all
var options = ParseArgs(args);
var port = int.Parse(options.GetValueOrDefault("port", "8006"));
var outDir = options.GetValueOrDefault("out", "shots");
var mode = options.GetValueOrDefault("mode", "desktop");
var tablet = mode == "tablet";
var desktop = mode is "desktop" or "tablet"; // 태블릿은 PC 와 같은 배치
var target = options.GetValueOrDefault("target", "all");
Directory.CreateDirectory(outDir);

CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture =
    CultureInfo.GetCultureInfo(options.GetValueOrDefault("culture", "ko-KR"));
var profileDir = Path.Combine(Path.GetTempPath(), "uisnap-" + Guid.NewGuid().ToString("N"));
AppServices.ProfilesDirectory = profileDir;
AppServices.SecretStore = new MemorySecretStore();
// 테마가 앱 초기화 때 화면 형태를 읽으므로 먼저 정한다
PlatformInfo.FormFactor = tablet ? FormFactor.Tablet : desktop ? FormFactor.Desktop : FormFactor.Mobile;
AppBuilder.Configure<App>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont().SetupWithoutStarting();


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

if (target == "console") await ConsoleUsabilityCheck();
if (target == "compact") await CompactButtonCheck();

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

// 좁은 화면 버튼 규칙 — 아이콘만 보이는지, 길게 누르면 이름 알림만(실행 안 함), 짧게 누르면 실행되는지
async Task CompactButtonCheck()
{
    var tab = PreviewRegistry.Get("selftest")[0];
    var ctx = new PreviewContext(cluster.Session, cluster.Guests.First(g => g.VmId == 100),
        cluster.Guests.First(g => g.VmId == 200), cluster.Nodes.First(), cluster.Storages.First());
    await Wait(AppServices.Navigator.PushViewAsync("compact", tab.Create(ctx)));
    Pump(1200);
    Shot("compact-buttons");
    var add = nav.CurrentPage!.GetVisualDescendants().OfType<Button>()
        .First(b => ToolTip.GetTip(b) as string == L.T("Action_Add"));
    var compact = add.Content is null;
    Console.WriteLine($"| 좁은 화면 버튼 | 아이콘만(글자 숨김) | {(compact ? "아이콘만" : "글자 보임: " + add.Content)} | {(desktop ? (compact ? "실패" : "통과(데스크톱은 글자)") : (compact ? "통과" : "실패"))} |");
    if (desktop == compact) failures++;
    if (desktop) return;

    var center = add.TranslatePoint(new Avalonia.Point(add.Bounds.Width / 2, add.Bounds.Height / 2), window)!.Value;
    var touch = window.TouchBegin(center, Avalonia.Input.RawInputModifiers.None);
    Pump(800); // 길게 누르기
    window.TouchEnd(touch, center, Avalonia.Input.RawInputModifiers.None);
    Pump(400);
    var toast = AppServices.Dialogs.ToastText;
    var opened = AppServices.Dialogs.Sheet is not null;
    Shot("compact-longpress-toast");
    var pass = toast == L.T("Action_Add") && !opened;
    Console.WriteLine($"| 길게 누르기 | 이름 알림 '{L.T("Action_Add")}', 실행 안 함 | 알림 '{toast}', 폼 {(opened ? "열림" : "안 열림")} | {(pass ? "통과" : "실패")} |");
    if (!pass) failures++;
    AppServices.Dialogs.Sheet?.GetType(); // (열렸으면 아래에서 닫는다)
    if (AppServices.Dialogs.Sheet is IDismissible d1) d1.Dismiss();

    touch = window.TouchBegin(center, Avalonia.Input.RawInputModifiers.None);
    Pump(100);
    window.TouchEnd(touch, center, Avalonia.Input.RawInputModifiers.None);
    WaitFor(() => AppServices.Dialogs.Sheet is not null, 3000);
    opened = AppServices.Dialogs.Sheet is not null;
    Console.WriteLine($"| 짧게 누르기 | 실행(입력 폼 열림) | 폼 {(opened ? "열림" : "안 열림")} | {(opened ? "통과" : "실패")} |");
    if (!opened) failures++;
    if (AppServices.Dialogs.Sheet is IDismissible d2) d2.Dismiss();
    await Wait(AppServices.Navigator.PopAsync());
}

// 모바일 콘솔 조작성 검사 — 제스처·특수키를 흉내 내고, 가짜 VNC 서버가 받은 이벤트(로그 'RFB ...')를 기대값과 비교한다
async Task ConsoleUsabilityCheck()
{
    var logPath = options.GetValueOrDefault("serverlog", "");
    var guest = cluster.Guests.First(g => g.VmId == 100);
    if (!guest.IsRunning) Console.WriteLine("WARN guest 100 not running");
    await Wait(AppServices.Navigator.PushAsync(new ConsoleViewModel(cluster.Session, guest)));
    var page = (ProxmoxClient.UI.Views.Console.ConsolePage)nav.CurrentPage!;
    var vm = (ConsoleViewModel)page.DataContext!;
    WaitFor(() => vm.IsConnected, 10000);
    Pump(1500);
    Shot("console-connected");
    var surface = page.GetVisualDescendants().OfType<ProxmoxClient.UI.Views.Console.VncSurface>().First();
    var g = surface.Gestures;
    var vp = surface.Viewport;
    long now = 1_000_000;
    var results = new List<(string Name, string Expect, string Got, bool Pass)>();
    var logOffset = 0L;

    List<string> Events()
    {
        Pump(400);
        if (logPath.Length == 0 || !File.Exists(logPath)) return [];
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(logOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var text = reader.ReadToEnd();
        logOffset = fs.Position;
        return text.Split('\n').Where(l => l.StartsWith("RFB pointer") || l.StartsWith("RFB key") || l.StartsWith("RFB qkey"))
            .Select(l => l[4..].Trim()).ToList();
    }

    // 마스크 변화만 간추린다(같은 마스크 이동은 하나로)
    string Masks(List<string> ev) => string.Join(" ", ev.Where(e => e.StartsWith("pointer"))
        .Select(e => e.Split(' ')[1].Split('=')[1]).Aggregate(new List<string>(), (acc, m) =>
        {
            if (acc.Count == 0 || acc[^1] != m) acc.Add(m);
            return acc;
        }));
    (int X, int Y) LastXY(List<string> ev)
    {
        var last = ev.LastOrDefault(e => e.StartsWith("pointer"));
        if (last is null) return (-1, -1);
        var parts = last.Split(' ');
        return (int.Parse(parts[2][2..]), int.Parse(parts[3][2..]));
    }
    void Check(string name, string expect, string got, bool pass) =>
        results.Add((name, expect, got, pass));

    var center = new Avalonia.Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2);
    void Tap(Avalonia.Point p, int fingers = 1, int holdMs = 80)
    {
        for (var i = 0; i < fingers; i++) g.Down(i + 1, p + new Avalonia.Vector(i * 30, 0), now);
        now += holdMs;
        g.Tick(now);
        for (var i = 0; i < fingers; i++) g.Up(i + 1, p + new Avalonia.Vector(i * 30, 0), now);
        now += 200;
    }
    void Drag(Avalonia.Point from, Avalonia.Vector by, int fingers = 1, int holdFirstMs = 0, int steps = 10)
    {
        for (var i = 0; i < fingers; i++) g.Down(i + 1, from + new Avalonia.Vector(i * 60, 0), now);
        if (holdFirstMs > 0) { now += holdFirstMs; g.Tick(now); }
        for (var s = 1; s <= steps; s++)
        {
            now += 16;
            for (var i = 0; i < fingers; i++) g.Move(i + 1, from + new Avalonia.Vector(i * 60, 0) + by * s / steps, now);
        }
        for (var i = 0; i < fingers; i++) g.Up(i + 1, from + new Avalonia.Vector(i * 60, 0) + by, now);
        now += 200;
    }

    Events(); // 연결 시 이벤트 버림
    g.Mode = ProxmoxClient.UI.Views.Console.TouchInputMode.Mouse;
    g.ResetCursor(new Avalonia.Point(400, 300));

    Tap(center);
    var ev = Events();
    Check("마우스 모드 · 한 손가락 탭", "왼쪽 클릭(0 1 0) @ 커서(400,300)", $"{Masks(ev)} @ {LastXY(ev)}",
        Masks(ev) == "0 1 0" && LastXY(ev) == (400, 300));

    Tap(center); now -= 150; Tap(center);
    ev = Events();
    Check("빠른 두 번 탭", "더블 클릭(0 1 0 1 0)", Masks(ev), Masks(ev) == "0 1 0 1 0");

    now += 1000; // 두 번 탭 직후가 아니게(바로 끌면 '두 번 탭 후 끌기' = 끌어다 놓기)
    Drag(center, new Avalonia.Vector(100, 0));
    ev = Events();
    var moved = LastXY(ev);
    Check("한 손가락 끌기(오른쪽 100px)", "버튼 없이 커서만 오른쪽으로", $"{Masks(ev)} → {moved}",
        Masks(ev) == "0" && moved.X > 400);
    var cursorAfterMove = moved;

    Tap(center, fingers: 2);
    ev = Events();
    Check("두 손가락 탭", "오른쪽 클릭(0 4 0) @ 커서", $"{Masks(ev)} @ {LastXY(ev)}",
        Masks(ev) == "0 4 0" && LastXY(ev) == cursorAfterMove);

    Tap(center, fingers: 3);
    ev = Events();
    Check("세 손가락 탭", "가운데 클릭(0 2 0)", Masks(ev), Masks(ev) == "0 2 0");

    Tap(center, holdMs: 600);
    ev = Events();
    Check("길게 누르기(0.6초) 후 떼기", "오른쪽 클릭(0 4 0)", Masks(ev), Masks(ev) == "0 4 0");

    Drag(center, new Avalonia.Vector(0, 80), holdFirstMs: 600);
    ev = Events();
    var dragStart = ev.FirstOrDefault(e => e.Contains("mask=1"));
    var dragY = dragStart is null ? -1 : int.Parse(dragStart.Split(' ')[3][2..]);
    Check("길게 누른 뒤 끌기", "왼쪽 버튼 누른 채 아래로 이동 후 놓기(1 0)", $"{Masks(ev)} y {dragY}→{LastXY(ev).Y}",
        Masks(ev) == "1 0" && LastXY(ev).Y > dragY);

    Drag(center, new Avalonia.Vector(0, -120), fingers: 2);
    ev = Events();
    var downs = ev.Count(e => e.Contains("mask=16"));
    Check("두 손가락 위로 밀기", "아래로 스크롤(휠 16) 여러 번", $"휠 아래 {downs}회", downs >= 3);

    Drag(center, new Avalonia.Vector(0, 120), fingers: 2);
    ev = Events();
    var ups = ev.Count(e => e.Contains("mask=8"));
    Check("두 손가락 아래로 밀기", "위로 스크롤(휠 8) 여러 번", $"휠 위 {ups}회", ups >= 3);

    // 벌리기: 두 손가락 사이를 넓힌다
    g.Down(1, center - new Avalonia.Vector(40, 0), now);
    g.Down(2, center + new Avalonia.Vector(40, 0), now);
    for (var s = 1; s <= 10; s++)
    {
        now += 16;
        g.Move(1, center - new Avalonia.Vector(40 + s * 12, 0), now);
        g.Move(2, center + new Avalonia.Vector(40 + s * 12, 0), now);
    }
    g.Up(1, center, now); g.Up(2, center, now); now += 200;
    ev = Events();
    Check("벌리기(확대)", "확대 배율 > 1, 클릭 없음", $"배율 {vp.Zoom:0.00}, 이벤트 {ev.Count}개",
        vp.Zoom > 1.5 && !ev.Any(e => e.Contains("mask=1") || e.Contains("mask=4")));
    Shot("console-zoomed");

    vp.Fit();
    g.Mode = ProxmoxClient.UI.Views.Console.TouchInputMode.Touch;
    var target = vp.ToView(new Avalonia.Point(100, 50));
    Tap(target);
    ev = Events();
    var at = LastXY(ev);
    Check("터치 모드 · 탭", "누른 곳(100,50) 왼쪽 클릭", $"{Masks(ev)} @ {at}",
        Masks(ev) == "0 1 0" && Math.Abs(at.X - 100) <= 2 && Math.Abs(at.Y - 50) <= 2);

    Drag(vp.ToView(new Avalonia.Point(200, 200)), new Avalonia.Vector(60, 0));
    ev = Events();
    Check("터치 모드 · 끌기", "왼쪽 버튼 누른 채 끌기(0 1 0)", Masks(ev), Masks(ev) == "0 1 0");

    Tap(target, holdMs: 600);
    ev = Events();
    Check("터치 모드 · 길게 누르기", "오른쪽 클릭(0 4 0)", Masks(ev), Masks(ev) == "0 4 0");

    // 특수키·소프트 키보드
    var buttons = page.GetVisualDescendants().OfType<Button>().ToList();
    buttons.First(b => b.Content as string == "Esc").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    ev = Events();
    Check("특수키 Esc", "key 0xff1b 누름·뗌", string.Join(", ", ev),
        ev.Count == 2 && ev.All(e => e.EndsWith("0xff1b")));

    page.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
        .First(b => b.Content as string == "Ctrl").IsChecked = true;
    var input = page.GetVisualDescendants().OfType<TextBox>().First();
    input.Text = "c";
    ev = Events();
    Check("Ctrl 고정 + 키보드 c", "Ctrl↓ c↓ c↑ Ctrl↑", string.Join(", ", ev.Select(e => e.Replace("key ", ""))),
        ev.Count == 4 && ev[0].EndsWith("0xffe3") && ev[1].EndsWith("0x63") && ev[3].EndsWith("0xffe3") &&
        ev[3].Contains("down=0"));

    input.Text = "안";
    ev = Events();
    Check("한글 입력 '안'", "유니코드 keysym 0x100c548", string.Join(", ", ev),
        ev.Count == 2 && ev[0].EndsWith("0x100c548"));

    // ---- TeamViewer 방식: 두 번 탭한 뒤 끌기 = 끌어다 놓기 (마우스 모드)
    vp.Fit();
    g.Mode = TouchInputMode.Mouse;
    g.ResetCursor(new Avalonia.Point(400, 300));
    Events();
    Tap(center); now -= 100;
    Drag(center, new Avalonia.Vector(0, 90));
    ev = Events();
    Check("두 번 탭 후 끌기(TeamViewer)", "클릭 뒤 왼쪽 버튼 누른 채 끌기(0 1 0 1 0)", Masks(ev),
        Masks(ev) == "0 1 0 1 0" && LastXY(ev).Y > 300);

    // ---- 태블릿에 연결한 물리 마우스(포인터 종류 = 마우스): 버튼·휠을 그대로
    surface.Focus();
    Avalonia.Point Win(Avalonia.Point remote) => surface.TranslatePoint(vp.ToView(remote), window) ?? default;
    var rp = Win(new Avalonia.Point(300, 200));
    Events();
    window.MouseMove(rp, Avalonia.Input.RawInputModifiers.None);
    window.MouseDown(rp, Avalonia.Input.MouseButton.Right, Avalonia.Input.RawInputModifiers.None);
    window.MouseUp(rp, Avalonia.Input.MouseButton.Right, Avalonia.Input.RawInputModifiers.None);
    ev = Events();
    var mxy = LastXY(ev);
    Check("물리 마우스 오른쪽 클릭", "오른쪽 버튼(4 0) @ (300,200)", $"{Masks(ev)} @ {mxy}",
        Masks(ev).EndsWith("4 0") && Math.Abs(mxy.X - 300) <= 2 && Math.Abs(mxy.Y - 200) <= 2);

    window.MouseDown(rp, Avalonia.Input.MouseButton.Middle, Avalonia.Input.RawInputModifiers.None);
    window.MouseUp(rp, Avalonia.Input.MouseButton.Middle, Avalonia.Input.RawInputModifiers.None);
    ev = Events();
    Check("물리 마우스 가운데 클릭", "가운데 버튼(2 0)", Masks(ev), Masks(ev).EndsWith("2 0"));

    window.MouseWheel(rp, new Avalonia.Vector(0, -1), Avalonia.Input.RawInputModifiers.None);
    ev = Events();
    Check("물리 마우스 휠 아래", "휠 아래(16 0)", Masks(ev), Masks(ev).Contains("16"));

    window.MouseDown(rp, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
    window.MouseMove(Win(new Avalonia.Point(350, 260)), Avalonia.Input.RawInputModifiers.LeftMouseButton);
    window.MouseUp(Win(new Avalonia.Point(350, 260)), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
    ev = Events();
    Check("물리 마우스 왼쪽 끌기", "왼쪽 누른 채 이동 후 놓기(… 1 0) @ (350,260)", $"{Masks(ev)} @ {LastXY(ev)}",
        Masks(ev).EndsWith("1 0") && Math.Abs(LastXY(ev).X - 350) <= 2);

    // ---- 물리 키보드: 키 위치(스캔코드) + keysym — 서버가 QEMU 확장 키를 알리므로 qkey 로 와야 한다
    List<string> Keys() => Events().Where(e => e.StartsWith("key") || e.StartsWith("qkey")).ToList();
    string Codes(List<string> k) => string.Join(" ", k.Select(e =>
        (e.Contains("down=1") ? "↓" : "↑") + e.Split("keycode=").Last()));
    void Press(Avalonia.Input.Key key, Avalonia.Input.PhysicalKey pk, string? sym,
        Avalonia.Input.RawInputModifiers mods = Avalonia.Input.RawInputModifiers.None) =>
        window.KeyPress(key, mods, pk, sym);
    void Release(Avalonia.Input.Key key, Avalonia.Input.PhysicalKey pk, string? sym,
        Avalonia.Input.RawInputModifiers mods = Avalonia.Input.RawInputModifiers.None) =>
        window.KeyRelease(key, mods, pk, sym);
    surface.Focus();
    Keys();
    var ctrl = Avalonia.Input.RawInputModifiers.Control;
    Press(Avalonia.Input.Key.LeftCtrl, Avalonia.Input.PhysicalKey.ControlLeft, null, ctrl);
    Press(Avalonia.Input.Key.C, Avalonia.Input.PhysicalKey.C, "c", ctrl);
    Release(Avalonia.Input.Key.C, Avalonia.Input.PhysicalKey.C, "c", ctrl);
    Release(Avalonia.Input.Key.LeftCtrl, Avalonia.Input.PhysicalKey.ControlLeft, null);
    var k = Keys();
    Check("물리 키보드 Ctrl+C", "스캔코드 ↓1d ↓2e ↑2e ↑1d", Codes(k), Codes(k) == "↓0x1d ↓0x2e ↑0x2e ↑0x1d");

    var shift = Avalonia.Input.RawInputModifiers.Shift;
    Press(Avalonia.Input.Key.LeftShift, Avalonia.Input.PhysicalKey.ShiftLeft, null, shift);
    Press(Avalonia.Input.Key.A, Avalonia.Input.PhysicalKey.A, "A", shift);
    window.KeyTextInput("A");
    Release(Avalonia.Input.Key.A, Avalonia.Input.PhysicalKey.A, "A", shift);
    Release(Avalonia.Input.Key.LeftShift, Avalonia.Input.PhysicalKey.ShiftLeft, null);
    k = Keys();
    Check("물리 키보드 Shift+A (글자 중복 없음)", "↓2a ↓1e ↑1e ↑2a, keysym 'A'", Codes(k) + " / " + string.Join(",", k.Where(e => e.Contains("keycode=0x1e")).Select(e => e.Split(' ')[2])),
        Codes(k) == "↓0x2a ↓0x1e ↑0x1e ↑0x2a" && k.Any(e => e.Contains("keysym=0x41")));

    Press(Avalonia.Input.Key.Up, Avalonia.Input.PhysicalKey.ArrowUp, null);
    Release(Avalonia.Input.Key.Up, Avalonia.Input.PhysicalKey.ArrowUp, null);
    Press(Avalonia.Input.Key.F5, Avalonia.Input.PhysicalKey.F5, null);
    Release(Avalonia.Input.Key.F5, Avalonia.Input.PhysicalKey.F5, null);
    k = Keys();
    Check("물리 키보드 ↑ · F5", "확장 키 ↓c8 ↑c8, ↓3f ↑3f", Codes(k), Codes(k) == "↓0xc8 ↑0xc8 ↓0x3f ↑0x3f");

    Press(Avalonia.Input.Key.HangulMode, Avalonia.Input.PhysicalKey.Lang1, null);
    Release(Avalonia.Input.Key.HangulMode, Avalonia.Input.PhysicalKey.Lang1, null);
    k = Keys();
    Check("한국어 키보드 한/영 키", "스캔코드 ↓f2 ↑f2", Codes(k), Codes(k) == "↓0xf2 ↑0xf2");

    Press(Avalonia.Input.Key.LeftAlt, Avalonia.Input.PhysicalKey.AltLeft, null, Avalonia.Input.RawInputModifiers.Alt);
    page.GetVisualDescendants().OfType<TextBox>().First().Focus(); // 키보드 버튼을 눌러 입력 칸으로 포커스가 옮겨진 경우
    Pump(200);
    k = Keys();
    Check("포커스를 잃으면 눌린 키 해제", "Alt ↓38 ↑38", Codes(k), Codes(k) == "↓0x38 ↑0x38");

    Shot("console-keys");
    Console.WriteLine();
    Console.WriteLine("| 조작 | 기대 | 결과 | 판정 |");
    Console.WriteLine("|---|---|---|---|");
    foreach (var r in results)
    {
        Console.WriteLine($"| {r.Name} | {r.Expect} | {r.Got} | {(r.Pass ? "통과" : "실패")} |");
        if (!r.Pass) failures++;
    }

    await Wait(AppServices.Navigator.PopAsync());
}

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
