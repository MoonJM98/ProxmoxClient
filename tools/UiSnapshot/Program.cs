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
using ProxmoxClient.UI.Views.Main;

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
    .UseAppFonts().SetupWithoutStarting();


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
// --vpn x.ovpn: 로그인 전에 실제 OpenVPN 터널을 연다(--host 는 터널 너머 주소)
var vpnConfig = options.GetValueOrDefault("vpn");
await AppServices.Profiles.SaveAsync(new ConnectionProfile
{
    Name = "fake", Host = options.GetValueOrDefault("host", "127.0.0.1"), Port = port,
    UseVpn = vpnConfig is not null, VpnConfigPath = vpnConfig
});
AppServices.SecretStore.Set(SecretKey(), "secret");
var profiles = (ProfilesViewModel)nav.CurrentPage!.DataContext!;
await Wait(profiles.LoadAsync());
// 연결 전 화면 캡처: 데스크톱은 로그인 화면(인증 영역을 가져오며 처음 보는 인증서를 묻는다), 두 형태 모두 서버 설정 화면
if (desktop)
{
    WaitFor(() => profiles.Realms.Count > 0 || AppServices.Dialogs.Current is not null, 6000);
    if (AppServices.Dialogs.Current is { } firstTrust) firstTrust.ConfirmCommand.Execute(null); // 처음 보는 인증서 — 신뢰
    WaitFor(() => profiles.Realms.Count > 0, 6000);
    Shot("login");
}
await Wait(AppServices.Navigator.PushAsync(new ProfileEditViewModel(profiles.Profiles[0].Profile)));
Pump(800);
Shot("profile-edit");
// 화면 키보드 — 아래쪽 입력 칸에 포커스하고 키보드(360px)가 올라오면 그 칸이 키보드 위에 보여야 한다
if (!desktop)
{
    var shell = window.GetVisualDescendants().OfType<AppShell>().First();
    var lowest = nav.CurrentPage!.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible)
        .OrderByDescending(t => t.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value.Y).First();
    lowest.Focus();
    Pump(200);
    const double keyboard = 360;
    shell.ApplyKeyboardInset(keyboard);
    Pump(600);
    var bottom = lowest.TranslatePoint(new Avalonia.Point(0, lowest.Bounds.Height), window)!.Value.Y;
    var visible = bottom <= window.Height - keyboard + 1;
    Shot("keyboard-open");
    Console.WriteLine($"| 화면 키보드 | 포커스 칸이 키보드 위(≤{window.Height - keyboard}) | 칸 아래 끝 {bottom:F0} | {(visible ? "통과" : "실패")} |");
    if (!visible) failures++;
    shell.ApplyKeyboardInset(0);
    Pump(300);
}
if (AppServices.Dialogs.Current is { } editorDialog) editorDialog.CancelCommand.Execute(null);
await Wait(AppServices.Navigator.PopAsync());
Pump(300);
profiles.ConnectCommand.Execute(profiles.Profiles[0]);
WaitFor(() => AppServices.Dialogs.Current is not null || nav.CurrentPage?.DataContext is ClusterViewModel or MainViewModel);
AppServices.Dialogs.Current?.ConfirmCommand.Execute(null); // 인증서 신뢰
WaitFor(() => nav.CurrentPage?.DataContext is ClusterViewModel or MainViewModel, 15000);
// 데스크톱 로그인 후 화면은 WPF 와 같은 메인 화면(Views/Main) — 탭·선택 게스트를 캡처하고 끝낸다
ClusterViewModel cluster;
if (desktop && nav.CurrentPage?.DataContext is MainViewModel mainVm)
{
    if (target is "cluster" or "all")
    {
        MainViewCheck(mainVm); // 끝에 메인 화면을 닫는다(세션 정리 확인)
        if (target == "cluster")
        {
            VpnReport();
            Console.WriteLine(failures == 0 ? "DONE ok" : $"DONE failures={failures}");
            Directory.Delete(profileDir, true);
            return failures == 0 ? 0 : 2;
        }

        Console.WriteLine("FAIL all: main view closed the session; run targets separately on desktop");
        return 2;
    }

    // 다른 대상은 메인 화면 위에서 연다 — 목록 데이터는 같은 세션으로 읽어 온다(화면에는 붙이지 않음)
    Shot("main");
    cluster = new ClusterViewModel(mainVm.Session);
    await Wait(cluster.RefreshAsync(false));
}
else if (nav.CurrentPage?.DataContext is ClusterViewModel mobileCluster)
{
    cluster = mobileCluster;
    WaitFor(() => cluster.IsLoaded);
    Shot("cluster");
}
else
{
    Console.WriteLine("FAIL login: " + AppServices.Dialogs.Current?.Message);
    return 1;
}

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
if (target == "terminal") await TerminalCheck();
if (target == "expiry") await ExpiryCheck();
if (target == "realshell") await RealShellCheck();
VpnReport();

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

// --vpn 로 연 터널 — 상태를 찍고 끊은 뒤 openvpn 프로세스가 남지 않았는지
void VpnReport()
{
    if (vpnConfig is null) return;

    var vpn = VpnTunnel.Manager;
    Console.WriteLine($"VPN state={vpn.State} ip={vpn.VirtualIp} pid={vpn.ProcessId} in={vpn.BytesIn} out={vpn.BytesOut}");
    var pid = vpn.ProcessId;
    if (vpn.State != ProxmoxClient.Core.Vpn.VpnState.Connected) failures++;
    VpnTunnel.Shutdown();
    var gone = pid is null || !System.Diagnostics.Process.GetProcesses().Any(p => p.Id == pid);
    Console.WriteLine($"VPN after shutdown: process {(gone ? "gone" : "STILL RUNNING")}");
    if (!gone) failures++;
}

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

// 실제 셸 검사 — 가짜 서버를 FAKEPVE_REAL_SHELL=1 로 띄우면 termproxy 뒤에 진짜 bash(PTY)가 붙는다.
// 실제 프로그램(stty·vim·top·sleep·bash 자동 완성·대량 출력)으로 터미널 해석·입력·크기를 확인한다.
async Task RealShellCheck()
{
    var results = new List<(string Name, string Expect, string Got, bool Pass)>();
    void Check(string name, string expect, string got, bool pass) => results.Add((name, expect, got, pass));
    var vm = new TerminalViewModel(cluster.Session.Api, ProxmoxClient.Core.Models.ConsoleTarget.ForNode("pve1"), "pve1 · 셸");
    await Wait(AppServices.Navigator.PushAsync(vm));
    var page = (ProxmoxClient.UI.Views.Terminal.TerminalPage)nav.CurrentPage!;
    var term = page.Terminal;
    var buffer = term.Buffer;
    WaitFor(() => vm.IsConnected, 10000);
    string Screen()
    {
        lock (buffer.Gate)
            return string.Join("\n", Enumerable.Range(0, buffer.Rows).Select(r =>
                new string(buffer.GetVisibleLine(r, 0).Cells.Where(c => c.Width != 0)
                    .SelectMany(c => char.ConvertFromUtf32(c.Rune == 0 ? ' ' : c.Rune)).ToArray()).TrimEnd()));
    }
    string LastLine() => Screen().Split('\n').Last(l => l.Length > 0);
    WaitFor(() => Screen().Contains("root@pve1"), 8000);
    var input = page.GetVisualDescendants().OfType<TextBox>().First();
    input.Focus();
    Pump(300);
    void Type(string text) { input.Text = text; Pump(150); }
    void Key(Avalonia.Input.Key key, Avalonia.Input.RawInputModifiers mods = Avalonia.Input.RawInputModifiers.None)
    {
        window.KeyPress(key, mods, Avalonia.Input.PhysicalKey.None, null);
        window.KeyRelease(key, mods, Avalonia.Input.PhysicalKey.None, null);
        Pump(80);
    }
    void Run(string command) { Type(command); Key(Avalonia.Input.Key.Enter); }
    bool Prompt() => LastLine().StartsWith("root@pve1") && LastLine().EndsWith("#");

    Run("clear; stty size");
    WaitFor(() => Screen().Contains($"{buffer.Rows} {buffer.Columns}"), 4000);
    Check("PTY 크기(stty size)", $"{buffer.Rows} {buffer.Columns}", Screen().Split('\n').Skip(1).FirstOrDefault() ?? "", Screen().Contains($"{buffer.Rows} {buffer.Columns}"));

    Run("printf '\\e[1;31m한글 빨강\\e[0m 끝\\n'");
    WaitFor(() => Screen().Contains("한글 빨강 끝"), 4000);
    var red = false;
    lock (buffer.Gate)
        for (var r = 0; r < buffer.Rows; r++)
            red |= buffer.GetVisibleLine(r, 0).Cells.Any(c => c.Rune == '한' && c.Width == 2 && c.Flags.HasFlag(ProxmoxClient.UI.Views.Terminal.CellFlags.Bold) &&
                                                             ProxmoxClient.UI.Views.Terminal.TerminalColor.IsIndexed(c.Foreground, out var ix) && ix == 1);
    Check("SGR·한글 출력(printf)", "'한' 굵게·빨강·2칸", red ? "맞음" : "다름", red);

    Type("ech");
    Key(Avalonia.Input.Key.Tab);
    Pump(400);
    var completed = LastLine().EndsWith("echo");
    Check("Tab 자동 완성(bash)", "ech → echo", LastLine(), completed);
    Key(Avalonia.Input.Key.U, Avalonia.Input.RawInputModifiers.Control); // 줄 지우기

    Run("sleep 100");
    Pump(300);
    Key(Avalonia.Input.Key.C, Avalonia.Input.RawInputModifiers.Control);
    WaitFor(Prompt, 3000);
    Check("Ctrl+C 로 중단(sleep)", "프롬프트로 돌아옴", Prompt() ? "돌아옴" : LastLine(), Prompt());

    var file = "/tmp/fakepve-shell/home/vim-test.txt";
    File.Delete(file);
    Run("vim -u NONE -N vim-test.txt");
    WaitFor(() => buffer.IsAlternateScreen, 5000);
    Pump(400);
    Type("i"); // 글자 키는 입력 칸으로 들어온다(합성 KeyPress 에는 글자가 없다)
    Type("hello 한글");
    Key(Avalonia.Input.Key.Enter);
    Type("둘째 줄");
    Pump(300);
    Shot("realshell-vim");
    Key(Avalonia.Input.Key.Escape);
    Type(":wq");
    Key(Avalonia.Input.Key.Enter);
    WaitFor(() => !buffer.IsAlternateScreen, 5000);
    var saved = File.Exists(file) ? File.ReadAllText(file) : "(없음)";
    Check("vim 편집·저장(대체 화면)", "hello 한글\\n둘째 줄", saved.Replace("\n", "\\n"), saved == "hello 한글\n둘째 줄\n");

    Run("top -d 1");
    WaitFor(() => Screen().Contains("load average") && Screen().Contains("PID"), 6000);
    var firstClock = Screen().Split('\n')[0];
    Pump(2200); // 1초마다 다시 그린다
    Shot("realshell-top");
    var top = Screen().Contains("PID") && Screen().Split('\n')[0] != firstClock;
    Type("q");
    WaitFor(Prompt, 4000);
    Check("top(전체 화면·1초 갱신)", "부하·PID 표 갱신, q 로 종료", top ? (Prompt() ? "정상" : "종료 안 됨") : "표 없음/갱신 없음", top && Prompt());

    // 큰 출력 — 화면 스레드를 막지 않고 끝까지 받는지(해석은 수신 스레드)
    var sw = System.Diagnostics.Stopwatch.StartNew();
    Run("clear; seq 1 50000; echo END-OF-SEQ");
    WaitFor(() => Screen().Contains("END-OF-SEQ") && Prompt(), 20000);
    sw.Stop();
    var big = Screen().Contains("50000") && Screen().Contains("END-OF-SEQ");
    Check("대량 출력(seq 50000)", "끝까지·프롬프트 복귀", $"{sw.ElapsedMilliseconds} ms, 스크롤백 {buffer.ScrollbackCount}줄", big);

    var oldColumns = buffer.Columns;
    window.Width -= 160;
    Pump(800);
    Run("clear; stty size");
    WaitFor(() => Screen().Contains($"{buffer.Rows} {buffer.Columns}"), 4000);
    Check("창 크기 변경 → PTY", $"열 {oldColumns}→{buffer.Columns}", Screen().Split('\n').Skip(1).FirstOrDefault() ?? "",
        buffer.Columns < oldColumns && Screen().Contains($"{buffer.Rows} {buffer.Columns}"));
    window.Width += 160;
    Pump(500);

    Run("exit");
    WaitFor(() => !vm.IsConnected, 5000);
    Check("셸 종료(exit) → 연결 끊김 표시", "끊김·[재연결]", vm.IsConnected ? "연결됨" : vm.StateText, !vm.IsConnected);
    Shot("realshell-exit");

    Console.WriteLine();
    Console.WriteLine("| 실제 셸 | 기대 | 결과 | 판정 |");
    Console.WriteLine("|---|---|---|---|");
    foreach (var r in results)
    {
        Console.WriteLine($"| {r.Name} | {r.Expect} | {r.Got} | {(r.Pass ? "통과" : "실패")} |");
        if (!r.Pass) failures++;
    }

    await Wait(AppServices.Navigator.PopAsync());
}

// 세션 만료 — 서버가 티켓 갱신까지 거부하면 한 번 알리고 로그인 화면으로 돌아가는지
async Task ExpiryCheck()
{
    using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
    var expire = Task.Run(() => http.GetAsync($"https://127.0.0.1:{port}/api2/json/__fake/expire"));
    WaitFor(() => expire.IsCompleted, 5000);
    // 화면 스레드를 멈추지 않게 돌리며 기다린다(헤드리스는 직접 펌프해야 이어진다)
    var request = Task.Run(() => cluster.Session.Api.GetVersionAsync());
    WaitFor(() => request.IsCompleted, 8000);
    Console.WriteLine("request after expiry: " + (request.Exception?.InnerException?.Message ?? request.Status.ToString()));

    WaitFor(() => AppServices.Dialogs.Current is not null, 5000);
    var title = AppServices.Dialogs.Current?.Title;
    Shot("expiry-dialog");
    AppServices.Dialogs.Current?.ConfirmCommand.Execute(null);
    WaitFor(() => nav.CurrentPage?.DataContext is ProfilesViewModel, 5000);
    var back = nav.CurrentPage?.DataContext is ProfilesViewModel;
    Shot("expiry-login");
    var pass = title == L.T("Session_ExpiredTitle") && back;
    Console.WriteLine($"| 세션 만료 | 알림 '{L.T("Session_ExpiredTitle")}' → 로그인 화면 | 알림 '{title}', {(back ? "로그인 화면" : "그대로")} | {(pass ? "통과" : "실패")} |");
    if (!pass) failures++;
}

// 터미널 검사 — 가짜 termproxy 셸에 붙어 출력 해석(한글·색·선 그리기·대체 화면)과 입력(키·IME·고정 Ctrl·붙여넣기),
// 선택·스크롤백·크기 변경을 확인한다. 서버가 받은 입력은 로그의 'TERM input ...' 줄로 비교한다.
async Task TerminalCheck()
{
    var logPath = options.GetValueOrDefault("serverlog", "");
    var logOffset = 0L;
    List<string> Log(string prefix)
    {
        Pump(500);
        if (logPath.Length == 0 || !File.Exists(logPath)) return [];
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(logOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var text = reader.ReadToEnd();
        logOffset = fs.Position;
        return text.Split('\n').Where(l => l.StartsWith("TERM " + prefix)).Select(l => l[5..].Trim()).ToList();
    }

    var results = new List<(string Name, string Expect, string Got, bool Pass)>();
    void Check(string name, string expect, string got, bool pass) => results.Add((name, expect, got, pass));

    var vm = new TerminalViewModel(cluster.Session.Api, ProxmoxClient.Core.Models.ConsoleTarget.ForNode("pve1"), "pve1 · 셸");
    await Wait(AppServices.Navigator.PushAsync(vm));
    var page = (ProxmoxClient.UI.Views.Terminal.TerminalPage)nav.CurrentPage!;
    var term = page.Terminal;
    var buffer = term.Buffer;
    WaitFor(() => vm.IsConnected, 10000);
    Pump(1200);
    string Screen()
    {
        lock (buffer.Gate)
            return string.Join("\n", Enumerable.Range(0, buffer.Rows).Select(r =>
                new string(buffer.GetVisibleLine(r, 0).Cells.Where(c => c.Width != 0)
                    .SelectMany(c => char.ConvertFromUtf32(c.Rune == 0 ? ' ' : c.Rune)).ToArray()).TrimEnd()));
    }

    Check("연결·배너", "OK 뒤 배너·프롬프트", vm.IsConnected ? "연결됨" : vm.StateText,
        vm.IsConnected && Screen().Contains("가짜 Proxmox 셸") && Screen().Contains("root@pve1"));
    var resize = Log("resize").LastOrDefault() ?? "(없음)";
    Check("PTY 크기", $"resize {buffer.Columns} {buffer.Rows}", resize, resize == $"resize {buffer.Columns} {buffer.Rows}");
    Shot("terminal-connected");

    var input = page.GetVisualDescendants().OfType<TextBox>().First();
    input.Focus();
    Pump(200);
    void Type(string text) { input.Text = text; Pump(150); }
    void Key(Avalonia.Input.Key key, Avalonia.Input.RawInputModifiers mods = Avalonia.Input.RawInputModifiers.None)
    {
        window.KeyPress(key, mods, Avalonia.Input.PhysicalKey.None, null);
        window.KeyRelease(key, mods, Avalonia.Input.PhysicalKey.None, null);
        Pump(100);
    }

    Log("input");
    Type("ls");
    Key(Avalonia.Input.Key.Enter);
    var inputs = Log("input");
    Check("글자·Enter", "'ls' '\\r'", string.Join(" ", inputs), inputs.SequenceEqual(["input 'ls'", "input '\\r'"]));
    Pump(300);
    var lsRow = Enumerable.Range(0, buffer.Rows).FirstOrDefault(r => buffer.GetVisibleLine(r, 0).Cells.Any(c => c.Rune == '공'), -1);
    var wide = lsRow >= 0 && buffer.GetVisibleLine(lsRow, 0).Cells is var cells &&
               Array.FindIndex(cells, c => c.Rune == '공') is var i && cells[i].Width == 2 && cells[i + 1].Width == 0;
    Check("한글 폭(2칸)", "'공' 2칸 + 이어진 칸", wide ? "2칸" : "어긋남", wide);

    Type("한글");
    inputs = Log("input");
    Check("IME 한글 입력", "'한글'", string.Join(" ", inputs), inputs.SequenceEqual(["input '한글'"]));
    Key(Avalonia.Input.Key.Back);
    Key(Avalonia.Input.Key.Back);
    inputs = Log("input");
    Check("지우기", "'\\x7f' ×2", string.Join(" ", inputs), inputs.Count == 2 && inputs.All(x => x == "input '\\x7f'"));

    Key(Avalonia.Input.Key.C, Avalonia.Input.RawInputModifiers.Control);
    Key(Avalonia.Input.Key.Up);
    Key(Avalonia.Input.Key.Tab);
    Key(Avalonia.Input.Key.F5);
    inputs = Log("input");
    Check("Ctrl+C · ↑ · Tab · F5", "'\\x03' '\\x1b[A' '\\t' '\\x1b[15~'", string.Join(" ", inputs),
        inputs.SequenceEqual(["input '\\x03'", "input '\\x1b[A'", "input '\\t'", "input '\\x1b[15~'"]));

    // 특수 키 막대의 고정 Ctrl(터치) → 다음 글자 하나에만
    if (!desktop)
    {
        var ctrl = page.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => b.Content as string == "Ctrl");
        ctrl.IsChecked = true;
        Type("d");
        Type("d");
        inputs = Log("input");
        Check("고정 Ctrl + d, 다음 d", "'\\x04' 'd'", string.Join(" ", inputs), inputs.SequenceEqual(["input '\\x04'", "input 'd'"]));
        Key(Avalonia.Input.Key.Back);
        Log("input");
    }

    Type("colors");
    Key(Avalonia.Input.Key.Enter);
    Pump(500);
    var rgb = false;
    lock (buffer.Gate)
        for (var r = 0; r < buffer.Rows && !rgb; r++)
            rgb = buffer.GetVisibleLine(r, 0).Cells.Any(c => ProxmoxClient.UI.Views.Terminal.TerminalColor.IsRgb(c.Background, out _));
    Check("색(256·24비트)", "24비트 바탕색 칸", rgb ? "있음" : "없음", rgb);
    Shot("terminal-colors");

    Type("top");
    Key(Avalonia.Input.Key.Enter);
    Pump(500);
    var alt = buffer.IsAlternateScreen && Screen().Contains("pve1 — CPU");
    Check("대체 화면(top)", "1049 켜짐·틀 그리기", alt ? "대체 화면" : "아님", alt);
    Shot("terminal-top");
    Type("q");
    Pump(400);
    Check("대체 화면 복귀", "원래 화면·배너 보존", buffer.IsAlternateScreen ? "대체 화면" : "복귀",
        !buffer.IsAlternateScreen && Screen().Contains("colors"));

    Type("seq 300");
    Key(Avalonia.Input.Key.Enter);
    Pump(600);
    var before = term.ScrollOffset;
    var mid = term.TranslatePoint(new Avalonia.Point(term.Bounds.Width / 2, term.Bounds.Height / 2), window)!.Value;
    if (desktop)
        window.MouseWheel(mid, new Avalonia.Vector(0, 5), Avalonia.Input.RawInputModifiers.None);
    else
    {
        var t = window.TouchBegin(mid, Avalonia.Input.RawInputModifiers.None);
        for (var step = 1; step <= 10; step++)
        {
            window.TouchMove(t, mid + new Avalonia.Vector(0, step * 30), Avalonia.Input.RawInputModifiers.None);
            Pump(20);
        }

        window.TouchEnd(t, mid + new Avalonia.Vector(0, 300), Avalonia.Input.RawInputModifiers.None);
    }

    Pump(300);
    Check("스크롤백", "위로 올려 보기", $"{before} → {term.ScrollOffset}줄", before == 0 && term.ScrollOffset > 0 && buffer.ScrollbackCount > 200);
    Shot("terminal-scrollback");
    Type("x");
    Check("입력하면 맨 아래로", "0", term.ScrollOffset.ToString(), term.ScrollOffset == 0);
    Key(Avalonia.Input.Key.Back);
    Log("input");

    // 선택 → 복사할 글자
    var rowY = term.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;
    var cw = term.Bounds.Width / buffer.Columns;
    var promptRow = buffer.CursorY;
    var ch = (term.Bounds.Height - 8) / buffer.Rows;
    var p0 = rowY + new Avalonia.Vector(6 + cw * 2, 4 + ch * (promptRow - 1) + ch / 2);
    var p1 = rowY + new Avalonia.Vector(6 + cw * 4, 4 + ch * (promptRow - 1) + ch / 2);
    if (desktop)
    {
        window.MouseDown(p0, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        window.MouseMove(p1, Avalonia.Input.RawInputModifiers.LeftMouseButton);
        window.MouseUp(p1, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
    }
    else
    {
        var t = window.TouchBegin(p0, Avalonia.Input.RawInputModifiers.None);
        Pump(700); // 길게 누르기 → 단어 선택
        window.TouchEnd(t, p0, Avalonia.Input.RawInputModifiers.None);
    }

    Pump(300);
    var selected = term.GetSelectedText();
    Check("선택", "글자가 골라짐", $"'{selected}'", selected.Length > 0);
    Shot("terminal-selection");
    term.ClearSelection();

    // 창 크기 변경 → PTY 크기
    var oldColumns = buffer.Columns;
    window.Width -= 120;
    Pump(800);
    resize = Log("resize").LastOrDefault() ?? "(없음)";
    Check("크기 변경", $"열 줄어듦({oldColumns}→{buffer.Columns})", resize,
        buffer.Columns < oldColumns && resize == $"resize {buffer.Columns} {buffer.Rows}");
    window.Width += 120;
    Pump(500);

    // 연결 끊기 → 다시 연결
    await Wait(vm.DisconnectAsync());
    Pump(300);
    var disconnected = !vm.IsConnected;
    await Wait(vm.ConnectAsync());
    WaitFor(() => vm.IsConnected, 8000);
    Check("연결 끊기·다시 연결", "끊김 → 연결됨", $"{(disconnected ? "끊김" : "?")} → {(vm.IsConnected ? "연결됨" : vm.StateText)}",
        disconnected && vm.IsConnected);
    Shot("terminal-reconnected");

    Console.WriteLine();
    Console.WriteLine("| 터미널 조작 | 기대 | 결과 | 판정 |");
    Console.WriteLine("|---|---|---|---|");
    foreach (var r in results)
    {
        Console.WriteLine($"| {r.Name} | {r.Expect} | {r.Got} | {(r.Pass ? "통과" : "실패")} |");
        if (!r.Pass) failures++;
    }

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

    // 커서는 서버 응답 전에 바로 — 서버가 보낸 커서 모양을 손가락 자리에 그린다. 가짜 서버는 포인터를 받아도 화면을
    // 다시 보내지 않으므로 손가락 둘레 픽셀이 바뀌었다면 앱이 직접 그린 것이다(실제 터치 입력 경로로)
    WaitFor(() => surface.HasCursorShape, 3000);
    g.Mode = TouchInputMode.Touch;
    var fingerView = new Avalonia.Point(surface.Bounds.Width * 0.6, surface.Bounds.Height * 0.4);
    var fingerWin = surface.TranslatePoint(fingerView, window)!.Value;
    var cursorAt = fingerWin + new Avalonia.Vector(30, 10);
    int Changed(Avalonia.Media.Imaging.Bitmap a, Avalonia.Media.Imaging.Bitmap b, Avalonia.Point at)
    {
        var box = new PixelRect((int)at.X - 2, (int)at.Y - 2, 24, 40);
        var sa = new byte[box.Width * box.Height * 4];
        var sb = new byte[sa.Length];
        unsafe
        {
            fixed (byte* pa = sa) a.CopyPixels(box, (nint)pa, sa.Length, box.Width * 4);
            fixed (byte* pb = sb) b.CopyPixels(box, (nint)pb, sb.Length, box.Width * 4);
        }

        var n = 0;
        for (var k = 0; k < sa.Length; k += 4)
            if (Math.Abs(sa[k] - sb[k]) + Math.Abs(sa[k + 1] - sb[k + 1]) + Math.Abs(sa[k + 2] - sb[k + 2]) > 60) n++;
        return n;
    }

    Pump(200);
    var before = window.CaptureRenderedFrame()!;
    var touch = window.TouchBegin(fingerWin, Avalonia.Input.RawInputModifiers.None);
    Pump(30);
    window.TouchMove(touch, cursorAt, Avalonia.Input.RawInputModifiers.None); // 끌기 시작 — 서버 화면 갱신 없음
    Pump(60);
    var during = window.CaptureRenderedFrame()!;
    var changedPixels = Changed(before, during, cursorAt);
    Shot("console-local-cursor");
    window.TouchEnd(touch, cursorAt, Avalonia.Input.RawInputModifiers.None);
    Pump(300);
    Events();
    Check("커서를 서버보다 먼저(모양 커서)", "모양 받음, 손가락 자리 픽셀에 커서가 그려짐(30px 이상 바뀜)",
        $"모양 {(surface.HasCursorShape ? "받음" : "없음")}, 바뀐 픽셀 {changedPixels}", surface.HasCursorShape && changedPixels >= 30);

    // 끌기 매끄러움 — 손가락은 고르게(이벤트마다 12px, 약 1.5px/ms) 움직이지만 Android 처럼 터치 점이 두 개씩 묶여
    // 와서 시각 간격이 0ms·16ms 로 번갈아 올 때, 원격 커서도 이벤트마다 고르게 움직여야 한다(가속이 튀면 끊겨 보인다)
    g.Mode = TouchInputMode.Mouse;
    g.ResetCursor(new Avalonia.Point(40, 300));
    var dragAt = center - new Avalonia.Vector(150, 0);
    g.Down(9, dragAt, now);
    now += 700;
    g.Tick(now); // 길게 눌러 끌기 시작
    var steps = new List<double>();
    var prevCursor = g.Cursor;
    for (var i = 1; i <= 14; i++)
    {
        now += i % 2 == 0 ? 0 : 16;
        dragAt += new Avalonia.Vector(12, 0);
        g.Move(9, dragAt, now);
        if (i > 4) steps.Add(g.Cursor.X - prevCursor.X); // 처음 몇 개는 속도가 자리 잡는 구간
        prevCursor = g.Cursor;
    }

    g.Up(9, dragAt, now);
    now += 300;
    var mean = steps.Average();
    var cv = Math.Sqrt(steps.Sum(v => (v - mean) * (v - mean)) / steps.Count) / mean;
    var dragEvents = Events();
    Check("끌기 매끄러움(묶여 오는 터치)", "커서 이동 폭 변동 ≤ 10%, 버튼 누른 채", $"변동 {cv:P0}, 마스크 {Masks(dragEvents)}",
        cv <= 0.10 && Masks(dragEvents).StartsWith("1"));

    // 실제 터치 입력 경로로 끌기 — 화면 위 다른 손짓(뒤로 가기 밀기 등)이 터치를 가로채면 커서가 툭 움직이고 멈춘다.
    // 손가락을 떼지 않고 오른쪽으로 240px(30 단계) 끄는 동안 커서가 끝까지 따라와야 한다(마우스 모드).
    if (!desktop || tablet)
    {
        g.Mode = TouchInputMode.Mouse;
        g.ResetCursor(new Avalonia.Point(100, 300));
        Events();
        var touchFrom = surface.TranslatePoint(new Avalonia.Point(surface.Bounds.Width * 0.3, surface.Bounds.Height / 2), window)!.Value;
        var t = window.TouchBegin(touchFrom, Avalonia.Input.RawInputModifiers.None);
        Pump(30);
        var startX = g.Cursor.X;
        for (var i = 1; i <= 30; i++)
        {
            window.TouchMove(t, touchFrom + new Avalonia.Vector(i * 8, 0), Avalonia.Input.RawInputModifiers.None);
            Pump(16);
        }

        var midX = g.Cursor.X;
        window.TouchEnd(t, touchFrom + new Avalonia.Vector(240, 0), Avalonia.Input.RawInputModifiers.None);
        Pump(300);
        var moves = Events().Count(e => e.StartsWith("pointer"));
        Check("실제 터치로 계속 끌기(가로 240px)", "커서가 끝까지 따라감(150px 이상), 이동 20개 이상",
            $"커서 +{midX - startX:F0}px, 이동 {moves}개, 화면 {(nav.CurrentPage == page ? "그대로" : "바뀜")}",
            midX - startX >= 150 && moves >= 20 && nav.CurrentPage == page);
    }

    // 실시간 마우스 끌기 — 8ms 마다 움직인 이동이 서버에 고르게 도착하는가(보내는 쪽 간격 두기·타이머 오차 없음)
    if (desktop)
    {
        Events();
        var liveFrom = surface.TranslatePoint(new Avalonia.Point(200, 200), window)!.Value;
        window.MouseDown(liveFrom, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        for (var i = 1; i <= 60; i++)
        {
            window.MouseMove(liveFrom + new Avalonia.Vector(i * 3, 0), Avalonia.Input.RawInputModifiers.LeftMouseButton);
            Thread.Sleep(8);
            Dispatcher.UIThread.RunJobs();
        }

        window.MouseUp(liveFrom + new Avalonia.Vector(180, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        var times = Events().Where(e => e.StartsWith("pointer mask=1"))
            .Select(e => double.Parse(e.Split(' ').First(p => p.StartsWith("t="))[2..], CultureInfo.InvariantCulture)).ToList();
        var gaps = times.Zip(times.Skip(1), (a, b) => (b - a) * 1000).ToList();
        var maxGap = gaps.Count > 0 ? gaps.Max() : 999;
        Check("실시간 마우스 끌기(8ms 간격)", "이동 50개 이상 도착, 최대 간격 ≤ 25ms",
            $"{times.Count}개, 최대 {maxGap:F0}ms", times.Count >= 50 && maxGap <= 25);
    }

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

// 데스크톱 메인 화면 — 첫 화면, 탭 네 개(게스트·노드·스토리지·작업), 선택한 게스트 상세를 캡처한다
void MainViewCheck(MainViewModel main)
{
    WaitFor(() => main.VisibleGuestCount > 0, 15000);
    Pump(800);
    Shot("main-guests");
    if (main.Guests.FirstOrDefault(g => g.VmId == 100) is { } vm100) main.SelectedGuest = vm100;
    Pump(1500);
    Shot("main-guest-selected");
    // 정지된 VM(시작 버튼만) · 컨테이너(콘솔 버튼만, ▾ 없음)
    if (main.Guests.FirstOrDefault(g => g.VmId == 102) is { } vm102) main.SelectedGuest = vm102;
    Pump(1000);
    Shot("main-guest-stopped");
    if (main.Guests.FirstOrDefault(g => g.VmId == 200) is { } ct200) main.SelectedGuest = ct200;
    Pump(1000);
    Shot("main-guest-ct");
    if (main.Guests.FirstOrDefault(g => g.VmId == 100) is { } back) main.SelectedGuest = back;
    Pump(500);
    main.NavIndex = 1;
    Pump(1500);
    Shot("main-nodes");
    main.NavIndex = 2;
    Pump(800);
    Shot("main-storage");
    main.NavIndex = 3;
    Pump(800);
    Shot("main-tasks");
    main.NavIndex = 0;
    Pump(500);
    CheckStatus("main");
    // 서버 전환(프로필 목록으로 돌아가기) — 화면이 제거되며 세션이 정리되고 뷰모델이 제거 상태가 돼야 한다
    Wait(AppServices.Navigator.PopToRootAsync()).GetAwaiter().GetResult();
    Pump(300);
    Console.WriteLine(main.IsRemoved ? "POP ok removed" : "FAIL pop: view model still active");
    if (!main.IsRemoved) failures++;
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
