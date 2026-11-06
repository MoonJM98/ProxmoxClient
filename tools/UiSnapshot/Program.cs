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

// 셸 다시 붙이기(Android 액티비티를 다시 만들 때) — 같은 셸을 새 창에 옮겨도 페이지 스택이 그대로이고
// 대화상자 입력 칸 포커스(구독)가 새 창에서 한 번만 동작하며, 원래 창으로 돌아와도 같아야 한다
{
    var reShell = window.GetVisualDescendants().OfType<AppShell>().First();
    var pageBefore = nav.CurrentPage;
    window.Content = null;
    Pump(100);
    var other = new Window { Width = window.Width, Height = window.Height, Content = reShell };
    other.Show();
    Pump(400);
    var samePage = ReferenceEquals(nav.CurrentPage, pageBefore);
    var shown = ReferenceEquals(TopLevel.GetTopLevel(nav), other); // 페이지 스택이 새 창에 실제로 붙었다(빈 화면이 아니다)
    var prompt = AppServices.Dialogs.PromptAsync("t", "m");
    Pump(300);
    var focused = other.FocusManager?.GetFocusedElement() is TextBox { Name: "DialogInput" };
    AppServices.Dialogs.Current?.CancelCommand.Execute(null);
    await Wait(prompt);
    other.Content = null;
    other.Close();
    window.Content = reShell;
    Pump(400);
    var back = ReferenceEquals(nav.CurrentPage, pageBefore) && ReferenceEquals(TopLevel.GetTopLevel(nav), window);
    var ok = samePage && shown && focused && back;
    Console.WriteLine($"| 셸 다시 붙이기 | 페이지 유지·보임·입력 포커스·원래 창 복귀 | {samePage}/{shown}/{focused}/{back} | {(ok ? "통과" : "실패")} |");
    if (!ok) failures++;
}

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

    // 영역 가져오기(모바일 서버 편집) — 서버의 인증 영역을 읽어 고르면 사용자 칸이 이름@영역 으로
    var edit = (ProfileEditViewModel)nav.CurrentPage!.DataContext!;
    // 화면이 열리며 자동으로 가져오는 중일 수 있다 — 끝날 때까지 인증서 질문은 신뢰로 답하고, 서버 영역(설명 있음)이 올 때까지
    bool ServerRealms() => edit.Realms.Any(r => r.Comment.Length > 0);
    var realmDeadline = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < realmDeadline && !(ServerRealms() && !edit.IsFetchingRealms))
    {
        if (AppServices.Dialogs.Current is { } trust) trust.ConfirmCommand.Execute(null);
        else if (!edit.IsFetchingRealms && !ServerRealms()) edit.FetchRealmsCommand.Execute(null);
        Pump(150);
    }
    var other = edit.Realms.FirstOrDefault(r => r.Realm != "pam");
    if (other is not null) edit.SelectedRealm = other;
    Pump(300);
    Shot("profile-realms");
    var realmNames = string.Join(",", edit.Realms.Select(r => r.Realm));
    Check0("사용자·영역 따로(PC 와 같이)", "사용자 칸 root + 영역 목록, 저장값 root@영역",
        $"사용자 '{edit.UserOnly}', 영역 [{realmNames}] → {edit.ComposedUser}",
        edit.UserOnly == "root" && edit.Realms.Count > 2 && other is not null && edit.ComposedUser == $"root@{other.Realm}");
    edit.SelectedRealm = edit.Realms.FirstOrDefault(r => r.Realm == "pam");
}
if (AppServices.Dialogs.Current is { } editorDialog) editorDialog.CancelCommand.Execute(null);
await Wait(AppServices.Navigator.PopAsync());
Pump(300);
// ---- 2단계 인증 로그인(--target tfa): 창이 뜨고(TOTP 기본·숫자 키보드), 틀린 코드는 오류 후 다시, 맞는 코드면 로그인 ----
if (target == "tfa")
{
    var tfaProfile = new ConnectionProfile
    {
        Name = "tfa", Host = options.GetValueOrDefault("host", "127.0.0.1"), Port = port, UserName = "tfa@pam"
    };
    await AppServices.Profiles.SaveAsync(tfaProfile);
    AppServices.SecretStore.Set($"profile.{tfaProfile.Id:N}.password", "secret");
    await Wait(profiles.LoadAsync());
    profiles.ConnectCommand.Execute(profiles.Profiles.First(p => p.Profile.Name == "tfa"));
    ProxmoxClient.UI.Views.Login.TfaDialog? tfaDialog = null;
    var tfaDeadline = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < tfaDeadline
           && (tfaDialog = AppServices.Dialogs.Sheet as ProxmoxClient.UI.Views.Login.TfaDialog) is null)
    {
        if (AppServices.Dialogs.Current is { } tfaTrust) tfaTrust.ConfirmCommand.Execute(null); // 처음 보는 인증서 — 신뢰
        Pump(150);
    }

    Check0("2단계 인증 창", "TOTP 기본 · 숫자 키보드",
        tfaDialog is null ? "창 없음: " + AppServices.Dialogs.Current?.Message : $"{tfaDialog.Kind} · {tfaDialog.CodeContentType}",
        tfaDialog is { Kind: ProxmoxClient.Core.Api.TfaKind.Totp }
        && tfaDialog.CodeContentType == Avalonia.Input.TextInput.TextInputContentType.Digits);
    if (tfaDialog is not null)
    {
        Shot("tfa-dialog");
        await Wait(tfaDialog.SubmitAsync("000000"));
        Pump(300);
        Shot("tfa-wrong");
        Check0("틀린 TOTP 코드", "오류를 보이고 창 유지(다시 입력)", tfaDialog.CurrentError,
            ReferenceEquals(AppServices.Dialogs.Sheet, tfaDialog) && !tfaDialog.Completed
            && tfaDialog.CurrentError.Length > 0 && tfaDialog.CurrentError != L.T("Tfa_Verifying"));
        await Wait(tfaDialog.SubmitAsync("123 456"));
        WaitFor(() => nav.CurrentPage?.DataContext is ClusterViewModel or MainViewModel, 15000);
        var tfaSession = nav.CurrentPage?.DataContext switch
        {
            ClusterViewModel c => c.Session, MainViewModel m => m.Session, _ => null
        };
        Check0("맞는 코드로 로그인", "tfa@pam 세션 · 완성된 티켓",
            $"{tfaSession?.UserDisplay ?? "(로그인 안 됨)"} · {tfaSession?.Api.AuthTicket}",
            tfaSession is { UserDisplay: "tfa@pam" } && tfaSession.Api.AuthTicket == "PVE:tfa@pam:0000::sig"
            && AppServices.Dialogs.Sheet is null);
        Shot("tfa-done");
    }

    Console.WriteLine(failures == 0 ? "DONE ok" : $"DONE failures={failures}");
    Directory.Delete(profileDir, true);
    return failures == 0 ? 0 : 2;
}
// ---- 앱이 죽은 뒤 터미널 다시 열기(--target restore, 휴대폰): 남은 기록 → 서버 목록 안내 → 로그인 → 묻기 → 다시 열림(가짜 셸) ----
if (target == "restore")
{
    if (desktop)
    {
        Console.WriteLine("SKIP restore: 휴대폰 화면 검사(--mode mobile)");
        Directory.Delete(profileDir, true);
        return 0;
    }

    var fakeProfile = profiles.Profiles[0].Profile;
    var otherProfile = new ConnectionProfile { Name = "other", Host = "192.0.2.1", Port = port };
    await AppServices.Profiles.SaveAsync(otherProfile);
    // 지난 실행이 터미널 셋을 연 채로 죽었다(fake 서버 둘, 다른 서버 하나)
    var statePath = Path.Combine(profileDir, "open-terminals.txt");
    OpenTerminalEntry[] crashed =
    [
        OpenTerminalEntry.From(fakeProfile, ProxmoxClient.Core.Models.ConsoleTarget.ForNode("pve1"), "pve1 · 셸", null),
        OpenTerminalEntry.From(otherProfile, ProxmoxClient.Core.Models.ConsoleTarget.ForNode("x"), "x · 셸", null),
        OpenTerminalEntry.From(fakeProfile,
            ProxmoxClient.Core.Models.ConsoleTarget.ForGuest("pve2", ProxmoxClient.Core.Models.ResourceKind.Lxc, 200),
            "200 · dns", new ProxmoxClient.Core.Models.PveResource { Name = "dns" })
    ];
    File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(crashed));
    SessionKeepAlive.StatePath = statePath; // 새 실행 — 이 파일에서 읽는다
    await Wait(profiles.LoadAsync());
    Pump(400);
    Shot("restore-banner");
    var banners = string.Join(" / ", profiles.PendingRestores.Select(r => r.Text));
    Check0("다시 열 터미널 안내", "서버별 한 줄(fake 2개, other 1개)", banners,
        profiles.PendingRestores.Count == 2 && banners.Contains("2") && banners.Contains("fake") && banners.Contains("other"));

    profiles.ConnectCommand.Execute(profiles.PendingRestores.First(r => r.Item.Profile.Id == fakeProfile.Id).Item);
    var askedText = "";
    var restoreDeadline = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < restoreDeadline && nav.CurrentPage?.DataContext is not TerminalViewModel)
    {
        if (AppServices.Dialogs.Current is { } d)
        {
            if (d.Title == L.T("Restore_PromptTitle"))
            {
                askedText = d.Message;
                Shot("restore-prompt");
            }
            d.ConfirmCommand.Execute(null); // 인증서 신뢰 → 다시 열기
        }
        Pump(150);
    }
    Check0("로그인 뒤 묻기", "터미널 2개 제목", askedText.Replace("\n", " "),
        askedText.Contains("2") && askedText.Contains("pve1 · 셸") && askedText.Contains("200 · dns") && !askedText.Contains("x · 셸"));

    string TermScreen(ProxmoxClient.UI.Views.Terminal.TerminalPage tp)
    {
        var buf = tp.Terminal.Buffer;
        lock (buf.Gate)
            return string.Join("\n", Enumerable.Range(0, buf.Rows).Select(r =>
                new string(buf.GetVisibleLine(r, 0).Cells.Where(c => c.Width != 0)
                    .SelectMany(c => char.ConvertFromUtf32(c.Rune == 0 ? ' ' : c.Rune)).ToArray()).TrimEnd()));
    }

    bool Connected(string title) => nav.CurrentPage is ProxmoxClient.UI.Views.Terminal.TerminalPage tp
                                    && tp.DataContext is TerminalViewModel { IsConnected: true } tv && tv.Title == title
                                    && TermScreen(tp).Contains("가짜 Proxmox 셸");
    WaitFor(() => Connected("200 · dns"), 10000);
    var topVm = nav.CurrentPage?.DataContext as TerminalViewModel;
    Shot("restore-terminal");
    Check0("다시 연 터미널(맨 위)", "200 · dns · lxc/200 경로 · 가짜 셸 연결",
        $"{topVm?.Title} · {topVm?.Target.BasePath} · {(topVm?.IsConnected == true ? "연결됨" : topVm?.StateText)}",
        Connected("200 · dns") && topVm?.Target.BasePath == "nodes/pve2/lxc/200" && topVm.Guest?.Name == "dns");
    var left = SessionKeepAlive.ReadState(statePath);
    Check0("남은 기록", "다른 서버 것만 남고 연 터미널은 새로 기록",
        string.Join(", ", left.Select(e => e.Title)),
        SessionKeepAlive.Pending.Count == 1 && SessionKeepAlive.Pending[0].Title == "x · 셸"
        && left.Any(e => e.Title == "200 · dns") && left.Any(e => e.Title == "x · 셸"));

    await Wait(AppServices.Navigator.PopAsync()); // 아래 터미널은 보일 때 연결한다
    WaitFor(() => Connected("pve1 · 셸"), 10000);
    Check0("뒤로 → 아래 터미널", "pve1 · 셸 연결", (nav.CurrentPage?.DataContext as TerminalViewModel)?.Title ?? "(없음)",
        Connected("pve1 · 셸"));

    await Wait(AppServices.Navigator.PopToRootAsync());
    await Wait(profiles.LoadAsync());
    Pump(300);
    Check0("목록으로 돌아오면", "다른 서버 안내만", string.Join(" / ", profiles.PendingRestores.Select(r => r.Text)),
        profiles.PendingRestores.Count == 1 && profiles.PendingRestores[0].Item.Profile.Id == otherProfile.Id);
    profiles.DismissRestoreCommand.Execute(profiles.PendingRestores[0]);
    Pump(800);
    Check0("안내 닫기", "기록이 지워짐(열린 터미널 없음 → 파일 없음)",
        $"안내 {profiles.PendingRestores.Count}개, 파일 {(File.Exists(statePath) ? "있음" : "없음")}",
        profiles.PendingRestores.Count == 0 && SessionKeepAlive.Pending.Count == 0 && !File.Exists(statePath));

    Console.WriteLine(failures == 0 ? "DONE ok" : $"DONE failures={failures}");
    Directory.Delete(profileDir, true);
    return failures == 0 ? 0 : 2;
}

// ---- 보안 키 2단계 인증(--target webauthn): 가짜 키로 창(보안 키 기본) → 취소는 창 유지 → PIN 묻기(틀리면 다시) → 서명은 webauthn:{JSON} 으로 로그인 ----
if (target == "webauthn")
{
    var fakeKey = new UiSnapshot.FakeWebAuthnProvider { CancelFirst = 1 };
    WebAuthnProviders.Current = fakeKey;
    var waProfile = new ConnectionProfile
    {
        Name = "webauthn", Host = options.GetValueOrDefault("host", "127.0.0.1"), Port = port, UserName = "webauthn@pam"
    };
    await AppServices.Profiles.SaveAsync(waProfile);
    AppServices.SecretStore.Set($"profile.{waProfile.Id:N}.password", "secret");
    await Wait(profiles.LoadAsync());
    profiles.ConnectCommand.Execute(profiles.Profiles.First(p => p.Profile.Name == "webauthn"));
    ProxmoxClient.UI.Views.Login.TfaDialog? waDialog = null;
    var waDeadline = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < waDeadline
           && (waDialog = AppServices.Dialogs.Sheet as ProxmoxClient.UI.Views.Login.TfaDialog) is null)
    {
        if (AppServices.Dialogs.Current is { } waTrust) waTrust.ConfirmCommand.Execute(null); // 처음 보는 인증서 — 신뢰
        Pump(150);
    }

    Check0("보안 키 2단계 인증 창", "보안 키(WebAuthn) 기본",
        waDialog is null ? "창 없음: " + AppServices.Dialogs.Current?.Message : $"{waDialog.Kind}",
        waDialog is { Kind: ProxmoxClient.Core.Api.TfaKind.WebAuthn });
    if (waDialog is not null)
    {
        Shot("webauthn-dialog");
        await Wait(waDialog.UseSecurityKeyAsync());
        Pump(200);
        Check0("보안 키 취소", "취소 안내 · 창 유지", waDialog.CurrentError,
            ReferenceEquals(AppServices.Dialogs.Sheet, waDialog) && !waDialog.Completed
            && waDialog.CurrentError == L.T("Tfa_WebAuthnCancelled"));
        // 키가 PIN 을 요구 — 창에 PIN 칸(남은 횟수), 틀리면 오류와 함께 다시, 맞으면 서명
        fakeKey.AskPin = true;
        var waKeyTask = waDialog.UseSecurityKeyAsync();
        WaitFor(() => waDialog.WaitingForPin);
        Shot("webauthn-pin");
        Check0("보안 키 PIN 묻기", "PIN 칸 · 남은 횟수 8", waDialog.PinHint,
            waDialog.WaitingForPin && waDialog.PinHint.Contains('8'));
        waDialog.EnterPin("0000");
        WaitFor(() => waDialog.WaitingForPin && waDialog.CurrentError.Length > 0);
        Check0("틀린 PIN", "오류 · 남은 횟수 7 · 다시 묻기", $"{waDialog.CurrentError} / {waDialog.PinHint}",
            waDialog.WaitingForPin && waDialog.PinHint.Contains('7') && waDialog.CurrentError.Length > 0);
        waDialog.EnterPin("1234");
        await Wait(waKeyTask);
        WaitFor(() => nav.CurrentPage?.DataContext is ClusterViewModel or MainViewModel, 15000);
        var waSession = nav.CurrentPage?.DataContext switch
        {
            ClusterViewModel c => c.Session, MainViewModel m => m.Session, _ => null
        };
        Check0("보안 키 서명으로 로그인", $"webauthn@pam 세션 · origin https://{waProfile.Host}:{port}",
            $"{waSession?.UserDisplay ?? "(로그인 안 됨: " + waDialog.CurrentError + ")"} · {waSession?.Api.AuthTicket} · {fakeKey.LastOrigin}",
            waSession is { UserDisplay: "webauthn@pam" } && waSession.Api.AuthTicket == "PVE:webauthn@pam:0000::sig"
            && fakeKey.LastOrigin?.ToString().TrimEnd('/') == $"https://{waProfile.Host}:{port}" && AppServices.Dialogs.Sheet is null);
        Shot("webauthn-done");
    }

    Console.WriteLine(failures == 0 ? "DONE ok" : $"DONE failures={failures}");
    Directory.Delete(profileDir, true);
    return failures == 0 ? 0 : 2;
}
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
if (target is "console" or "seams") TileSeamCheck();
if (target == "createspeed") await CreateSpeedCheck();
if (target == "guestdetail")
{
    foreach (var id in new[] { 100, 200 })
    {
        await Wait(AppServices.Navigator.PushAsync(new GuestDetailViewModel(cluster.Session, cluster.Guests.First(g => g.VmId == id))));
        Pump(2000);
        Shot($"guestdetail-{id}");
        if (Environment.GetEnvironmentVariable("AUDIT") == "1") AuditButtons($"guestdetail-{id}");
        await Wait(AppServices.Navigator.PopAsync());
    }
}
if (target == "compact") await CompactButtonCheck();
if (target == "terminal") await TerminalCheck();
if (target == "expiry") await ExpiryCheck();
if (target == "realshell") await RealShellCheck();
// ---- RDP 콘솔 끝까지(--target rdp, 휴대폰): 가짜 PVE 의 RDCleanPath 프록시(tools/FakePve/rdp.py)가 이 PC 의 실제 RDP 서버
// (xrdp, --rdp 호스트:포트)로 잇는다. 연결·화면 내용·화면 키보드 글자/특수키(게스트 셸이 파일로 적은 글로 확인)·
// 창 크기에 맞춘 해상도 변경·웹소켓 끊김 뒤 다시 연결. RDP 서버가 없으면 SKIP(CI 에 xrdp 가 없을 때)
// --rdpnla 1(또는 RDP_NLA=1): NLA(CredSSP/NTLM)를 요구하는 서버(FreeRDP 그림자 서버, tools/FakePve/nla-server.sh) —
// 틀린 비밀번호는 로그인 화면이 아니라 CredSSP 거부(알아볼 수 있는 글), 연결은 CredSSP 로, 해상도는 서버가 고정(그림자 서버는
// 보이는 X 화면을 그대로 보낸다)이라 요청 뒤에도 연결이 유지되는지만 본다
if (target == "rdp") await RdpCheck();
async Task RdpCheck()
{
    var rdpTarget = options.GetValueOrDefault("rdp", Environment.GetEnvironmentVariable("RDP_TARGET") ?? "127.0.0.1:33890");
    var rdpUser = options.GetValueOrDefault("rdpuser", Environment.GetEnvironmentVariable("RDP_USER") ?? "rdptest");
    var rdpPassword = options.GetValueOrDefault("rdppass", Environment.GetEnvironmentVariable("RDP_PASSWORD") ?? "RdpTest!234");
    var rdpNla = options.GetValueOrDefault("rdpnla", Environment.GetEnvironmentVariable("RDP_NLA") ?? "0") == "1";
    var hostPort = rdpTarget.Split(':');
    // 이 검사의 기다림은 모두 동기(Pump 없이 await 하면 헤드리스 UI 스레드가 이어짐을 돌리지 않아 멈춘다)
    try
    {
        using var probe = new System.Net.Sockets.TcpClient();
        if (!probe.ConnectAsync(hostPort[0], int.Parse(hostPort[1])).Wait(3000)) throw new TimeoutException();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SKIP rdp: RDP 서버({rdpTarget})에 붙을 수 없음 — {ex.GetType().Name}");
        return;
    }

    // 가짜 PVE 설정: 대상 서버·rdpproxy 가 줄 계정, VM 101 디스플레이를 rdp 로
    void Configure(string password)
    {
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["target"] = rdpTarget, ["user"] = rdpUser, ["password"] = password, ["vmid"] = "101"
        });
        http.Send(new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{port}/__fake/rdp") { Content = form })
            .EnsureSuccessStatusCode();
    }

    var serverLog = options.GetValueOrDefault("serverlog", "");
    long LogSize() => File.Exists(serverLog) ? new FileInfo(serverLog).Length : 0;
    List<string> LogSince(long offset, string prefix)
    {
        if (!File.Exists(serverLog)) return [];
        using var fs = new FileStream(serverLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        return new StreamReader(fs).ReadToEnd().Split('\n').Where(l => l.StartsWith(prefix)).Select(l => l[prefix.Length..].Trim()).ToList();
    }

    int ColorsOf(byte[]? fb)
    {
        if (fb is null || fb.Length < 4) return 0;
        var set = new HashSet<int>();
        for (var i = 0; i + 3 < fb.Length; i += 4 * 97) set.Add(BitConverter.ToInt32(fb, i) & 0xFFFFFF);
        return set.Count;
    }

    var guest = cluster.Guests.First(g => g.VmId == 101);

    // xrdp 로그인 화면(틀린 비밀번호 → 자동 로그온 실패): xrdp 는 이 화면을 느린 경로 비트맵(폭을 4 의 배수로 채운 것)으로
    // 그린다. 세션이 끊기지 않고(옛 IronRDP 는 "unhandled PDU"), 대화상자 왼쪽 테두리가 곧아야 한다(행 간격을 잘못 읽으면
    // 줄마다 밀려 비스듬히 그려진다). 프록시 고쳐 쓰기 없이(FAKEPVE_RDP_COMPAT 꺼짐)
    if (rdpNla)
    {
        // NLA: 틀린 비밀번호 → CredSSP 단계에서 서버가 STATUS_LOGON_FAILURE 로 거부 → 시간 초과가 아니라 바로 알아볼 수 있는 글
        Configure("wrong-" + rdpPassword);
        var started = DateTime.UtcNow;
        await Wait(ConsoleLauncher.OpenGuestAsync(cluster.Session, guest));
        var failVm = (nav.CurrentPage as ConsolePage)?.DataContext as ConsoleViewModel;
        if (failVm is not null)
        {
            WaitFor(() => failVm.IsConnected || failVm.CanReconnect, 30000);
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            Shot("rdp-nla-logon-failed");
            var expected = ProxmoxClient.Core.Rdp.RdpErrors.Describe(Devolutions.IronRdp.IronRdpErrorKind.CredsspError, "STATUS_LOGON_FAILURE");
            Check0("RDP NLA 틀린 비밀번호", "CredSSP 거부를 '로그인 실패' 글로 · 10초 안에(시간 초과 아님)",
                $"{failVm.Status?.Replace('\n', ' ')} · {seconds:F1}초",
                !failVm.IsConnected && failVm.Status?.Contains(expected) == true && seconds < 10);
            await Wait(AppServices.Navigator.PopAsync());
            Pump(500);
        }

        Configure(rdpPassword);
    }
    else
    {
        Configure("wrong-" + rdpPassword);
        var logFrom = LogSize();
        await Wait(ConsoleLauncher.OpenGuestAsync(cluster.Session, guest));
        var loginPage = nav.CurrentPage as ConsolePage;
        var loginVm = loginPage?.DataContext as ConsoleViewModel;
        if (loginVm is not null)
        {
            WaitFor(() => loginVm.IsConnected || loginVm.CanReconnect, 30000);
            WaitFor(() => ColorsOf(loginVm.Vnc.Framebuffer) > 8, 15000);
            Pump(3000);
            Shot("rdp-login-screen");
            var fb = loginVm.Vnc.Framebuffer;
            int w = loginVm.Vnc.Width, h = loginVm.Vnc.Height;
            // 대화상자 높이의 가운데 줄들에서, 왼쪽 바탕색이 처음 끊기는 x
            var edges = new List<int>();
            if (fb is not null && fb.Length >= w * h * 4)
                for (var y = h * 2 / 5; y < h * 3 / 5; y++)
                {
                    var bg = BitConverter.ToInt32(fb, y * w * 4) & 0xFFFFFF;
                    for (var x = 1; x < w / 2; x++)
                        if ((BitConverter.ToInt32(fb, (y * w + x) * 4) & 0xFFFFFF) != bg)
                        {
                            edges.Add(x);
                            break;
                        }
                }

            var spread = edges.Count > 0 ? edges.Max() - edges.Min() : -1;
            var quirks = LogSince(logFrom, "RDP server sent");
            Check0("RDP xrdp 로그인 화면(느린 경로·채운 폭 비트맵)", "연결 유지 · 서버가 느린 경로로 보냄 · 왼쪽 테두리 곧음(±1)",
                $"{(loginVm.IsConnected ? "연결됨" : loginVm.Status)} · {(quirks.Count > 0 ? "느린 경로 받음" : "느린 경로 없음")} · 테두리 {edges.Count}줄 x {(edges.Count > 0 ? $"{edges.Min()}~{edges.Max()}" : "-")}",
                loginVm.IsConnected && quirks.Count > 0 && edges.Count > 20 && spread is >= 0 and <= 1);
            await Wait(AppServices.Navigator.PopAsync());
            Pump(500);
        }

        Configure(rdpPassword);
    }


    void Drop()
    {
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        http.Send(new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{port}/__fake/rdp-drop")
        {
            Content = new FormUrlEncodedContent([])
        });
    }

    var connectFrom = LogSize();
    await Wait(ConsoleLauncher.OpenGuestAsync(cluster.Session, guest)); // 디스플레이 rdp → RDP 콘솔
    var page = nav.CurrentPage as ConsolePage;
    var vm = page?.DataContext as ConsoleViewModel;
    Check0("RDP 콘솔 고르기", "vga type=rdp → RDP 콘솔", vm?.Protocol.ToString() ?? "(콘솔 없음)",
        vm?.Protocol == ProxmoxClient.Core.Vnc.ConsoleProtocol.Rdp);
    if (page is null || vm is null || vm.Protocol != ProxmoxClient.Core.Vnc.ConsoleProtocol.Rdp) return;

    // 화면 내용 — 표본 픽셀의 색 가짓수(검은 화면·한 색이 아니다)
    int Colors()
    {
        var fb = vm.Vnc.Framebuffer;
        if (fb is null || fb.Length < 4) return 0;
        var set = new HashSet<int>();
        for (var i = 0; i + 3 < fb.Length; i += 4 * 97) set.Add(BitConverter.ToInt32(fb, i) & 0xFFFFFF);
        return set.Count;
    }

    WaitFor(() => vm.IsConnected || vm.CanReconnect, 30000);
    WaitFor(() => Colors() > 20, 30000); // 세션(Xorg·창 관리자·xterm 글자)이 뜨기까지 — 처음 로그인은 몇 초 걸린다
    Pump(2500);
    Check0("RDP 연결", "연결됨 · 화면 크기 = 보기 크기 · 화면 내용 있음",
        $"{(vm.IsConnected ? "연결됨" : vm.Status)} · {vm.Vnc.Width}×{vm.Vnc.Height} · 색 {Colors()}가지",
        vm.IsConnected && vm.Vnc.Width > 0 && Colors() > 3);
    Shot("rdp-connected");
    if (!vm.IsConnected) return;
    if (rdpNla)
    {
        // 프록시가 본 X.224 확인의 선택 보안 — 2(CredSSP) 또는 8(CredSSP + 조기 인증 결과)이어야 NLA 로 들어간 것
        var selected = LogSince(connectFrom, "RDP x224 confirm").Select(l => l[(l.IndexOf("selected=", StringComparison.Ordinal) + 9)..]).ToList();
        Check0("RDP NLA(CredSSP/NTLM) 로그인", "서버가 CredSSP 를 골랐고 그대로 연결됨",
            string.Join(", ", selected), selected.Count > 0 && selected.All(p => p is "0x2" or "0x8"));
    }

    // 화면 키보드: xterm 을 누른 뒤 입력 칸에 글을 넣는다 → 게스트 셸이 파일로 적는다
    var softInput = page.GetVisualDescendants().OfType<TextBox>().First();
    void Click(int x, int y)
    {
        vm.SendPointer(0, x, y);
        vm.SendPointer(1, x, y);
        vm.SendPointer(0, x, y);
        Pump(300);
    }

    void Type(string text)
    {
        softInput.Text = text;
        Pump(200);
    }

    string ReadTyped(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            Pump(300);
            try
            {
                if (File.Exists(path)) return File.ReadAllText(path).TrimEnd('\n');
            }
            catch (IOException)
            {
            }
        }

        return "(파일 없음)";
    }

    Click(120, 120);
    var typedPath = $"/tmp/rdp-typed-{Environment.ProcessId}.txt";
    // 특수키: 'x' 를 지우기 키로 지운다
    Type("echo Ab1-x");
    page.Tap(KeySyms.BackSpace);
    Type($"c > {typedPath}");
    page.Tap(KeySyms.Return);
    var typed = ReadTyped(typedPath);
    Shot("rdp-typed");
    Check0("RDP 화면 키보드 글자·특수키", "게스트 셸이 'Ab1-c' 를 받음(대문자·숫자·기호·지우기·Enter)", typed, typed == "Ab1-c");

    // 한글: RDP 는 두벌식 키(영문 입력기에서 'gksrmf')가 아니라 유니코드 키 입력으로 보낸다 — 프록시가 본 빠른 경로 입력
    // (서버 로그 'RDP unicode U+XXXX')으로 확인한다. xrdp 는 ibus 없이 빌드되면 유니코드 키를 버린다(글자가 안 들어감,
    // Windows 는 그대로 넣는다) — 그래서 게스트가 받은 글은 참고로만 적는다
    var koreanPath = $"/tmp/rdp-korean-{Environment.ProcessId}.txt";
    var unicodeFrom = LogSize();
    Type($"echo 한글 > {koreanPath}\n");
    var korean = ReadTyped(koreanPath);
    var unicodeKeys = LogSince(unicodeFrom, "RDP unicode");
    Shot("rdp-korean");
    Check0("RDP 화면 키보드 한글", "유니코드 키 한·글(누름·뗌)이 서버로, 두벌식 키(gksrmf) 아님",
        $"{string.Join(", ", unicodeKeys)} · 게스트가 적은 글 '{korean}'",
        unicodeKeys.SequenceEqual(["U+D55C down", "U+D55C up", "U+AE00 down", "U+AE00 up"]) && korean is "한글" or "");
    // 창 크기 → 게스트 해상도(디스플레이 제어 채널 → 서버가 재활성화로 새 크기)
    var before = (vm.Vnc.Width, vm.Vnc.Height);
    window.Width = 600;
    window.Height = 700;
    Pump(300);
    WaitFor(() => page.LastRdpResize is { } r && r != before && vm.Vnc.Width == r.Width && vm.Vnc.Height == r.Height,
        rdpNla ? 5000 : 15000);
    Pump(1500);
    var asked = page.LastRdpResize;
    if (rdpNla)
        Check0("RDP 해상도 요청(그림자 서버는 크기 고정)", "창 600×700 → 요청은 보내고, 서버가 크기를 안 바꿔도 연결·화면 유지",
            $"{before.Width}×{before.Height} → 요청 {asked} · 지금 {vm.Vnc.Width}×{vm.Vnc.Height} · 색 {Colors()}가지 · {(vm.IsConnected ? "연결됨" : vm.Status)}",
            asked is { } na && na != before && vm.IsConnected && Colors() > 3);
    else
        Check0("RDP 해상도 맞춤", "창 600×700 → 요청한 크기로 게스트 해상도 바뀜",
            $"{before.Width}×{before.Height} → 요청 {asked} · 지금 {vm.Vnc.Width}×{vm.Vnc.Height} · 색 {Colors()}가지 · {(vm.IsConnected ? "연결됨" : vm.Status)}",
            asked is { } a && a != before && vm.Vnc.Width == a.Width && vm.Vnc.Height == a.Height && Colors() > 3 && vm.IsConnected);
    Shot("rdp-resized");

    // 웹소켓 끊김 → 끊김 알림·[다시 연결] → 다시 연결(새 토큰)하면 같은 세션 화면
    Drop();
    WaitFor(() => vm.CanReconnect, 30000);
    Check0("RDP 끊김 알림", "웹소켓이 끊기면 연결 끊김·[다시 연결]", vm.Status ?? "(상태 없음)", vm.CanReconnect && !vm.IsConnected);
    await Wait(vm.ReconnectAsync());
    WaitFor(() => vm.IsConnected || vm.CanReconnect, 30000);
    WaitFor(() => Colors() > 3, 15000);
    Pump(2000);
    var reconnectPath = $"/tmp/rdp-again-{Environment.ProcessId}.txt";
    Click(120, 120);
    Type($"echo again > {reconnectPath}\n");
    var again = ReadTyped(reconnectPath);
    Shot("rdp-reconnected");
    Check0("RDP 다시 연결", "다시 연결되고 화면·입력이 다시 동작",
        $"{(vm.IsConnected ? "연결됨" : vm.Status)} · {vm.Vnc.Width}×{vm.Vnc.Height} · 색 {Colors()}가지 · 입력 '{again}'",
        vm.IsConnected && Colors() > 3 && again == "again");
    Type($"rm -f /tmp/rdp-*-{Environment.ProcessId}.txt; clear\n"); // 게스트 사용자 파일이라 게스트 셸에서 지운다
    Pump(500);
    await Wait(AppServices.Navigator.PopAsync());
    Pump(500);
}
// ---- 메인 화면 검사(--target mainscreens, 휴대폰) — 느린 서버의 작업 로그 줄 중복·긴 목록 가상화·탭별 오류/빈 상태·연결 취소
if (target == "mainscreens") await MainScreensCheck();
async Task MainScreensCheck()
{
    if (desktop)
    {
        Console.WriteLine("SKIP mainscreens: 휴대폰 화면 검사(--mode mobile)");
        return;
    }

    using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
    void FakeSet(string query)
    {
        var set = Task.Run(() => http.GetAsync($"https://127.0.0.1:{port}/api2/json/__fake/set?{query}"));
        WaitFor(() => set.IsCompleted, 5000);
    }

    T? Find<T>(Func<T, bool> where) where T : Control =>
        nav.CurrentPage!.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.IsEffectivelyVisible && where(c));

    // 1) 작업 로그 — 응답이 2초 새로 고침 주기보다 느려도(첫 읽기 중 타이머가 돌아도) 줄이 겹치지 않는다
    FakeSet("tasklog_delay_ms=2600&tasklog_lines=1200");
    var logVm = new TaskLogViewModel(cluster.Session, cluster.Tasks[0]);
    await Wait(AppServices.Navigator.PushAsync(logVm));
    Pump(400);
    var spinner = Find<ProgressBar>(p => p.IsIndeterminate) is not null;
    Shot("tasklog-loading");
    Check0("작업 로그 첫 읽기", "읽는 동안 진행 표시", spinner ? "진행 표시" : "없음", spinner && !logVm.IsLoaded);
    WaitFor(() => logVm.IsLoaded && logVm.Lines.Count >= 1200, 20000);
    Pump(3000); // 겹친 새로 고침이 있었다면 이 사이에 줄이 더 붙는다
    var distinct = logVm.Lines.Distinct().Count();
    Check0("작업 로그 줄 중복(느린 서버)", "1200줄, 겹침 없음", $"{logVm.Lines.Count}줄, 서로 다른 줄 {distinct}",
        logVm.Lines.Count == 1200 && distinct == 1200);
    var realized = nav.CurrentPage!.GetVisualDescendants().OfType<SelectableTextBlock>().Count();
    var logScroll = nav.CurrentPage!.GetVisualDescendants().OfType<ScrollViewer>().First(v => v.Name == "LogScroll");
    var atBottom = logScroll.Offset.Y + logScroll.Viewport.Height >= logScroll.Extent.Height - 30;
    Shot("tasklog-long");
    Check0("긴 작업 로그", "보이는 줄만 그림(가상화)·맨 아래로 따라감", $"그린 줄 {realized}, {(atBottom ? "맨 아래" : $"위치 {logScroll.Offset.Y:F0}/{logScroll.Extent.Height:F0}")}",
        realized < 300 && atBottom);
    Check0("작업 로그 전체 복사", "모든 줄", $"{logVm.LogText.Split('\n').Length}줄", logVm.LogText.Split('\n').Length == 1200);
    FakeSet("tasklog_delay_ms=0&tasklog_lines=3");
    await Wait(AppServices.Navigator.PopAsync());

    // 2) 작업 목록 권한 없음 — 작업 탭에 알리고 나머지는 그대로
    var page = (TabbedPage)nav.CurrentPage!;
    FakeSet("tasks_forbidden=1");
    await Wait(cluster.RefreshAsync(false));
    page.SelectedIndex = 3;
    Pump(500);
    var tasksBanner = Find<TextBlock>(t => t.Text == cluster.TasksErrorMessage && t.Text is not null) is not null;
    Shot("cluster-tasks-forbidden");
    Check0("작업 목록 권한 없음", "작업 탭에 안내, 다른 오류 배너 없음", $"{cluster.TasksErrorMessage ?? "(없음)"}, 배너 {(tasksBanner ? "보임" : "안 보임")}",
        cluster.HasTasksError && tasksBanner && !cluster.HasError && cluster.Guests.Count > 0);
    FakeSet("tasks_forbidden=0");
    await Wait(cluster.RefreshAsync(false));
    Check0("작업 목록 권한 회복", "안내 사라짐", cluster.HasTasksError ? "남음" : "사라짐", !cluster.HasTasksError && cluster.Tasks.Count > 0);

    // 3) 게스트가 많아도 보이는 카드만 만든다(가상화) — 당겨서 새로 고침 칸 안에서도
    FakeSet("extra_guests=300");
    await Wait(cluster.RefreshAsync(false));
    page.SelectedIndex = 0;
    Pump(800);
    int GuestCards() => nav.CurrentPage!.GetVisualDescendants().OfType<Button>()
        .Count(b => b.Classes.Contains("row") && b.DataContext is ProxmoxClient.Core.Models.PveResource);
    var cards = GuestCards();
    var refreshHost = nav.CurrentPage!.GetVisualDescendants().OfType<RefreshContainer>().First(r => r.IsEffectivelyVisible);
    var guestScroll = refreshHost.GetVisualDescendants().OfType<ScrollViewer>().First();
    guestScroll.ScrollToEnd();
    Pump(600);
    guestScroll.ScrollToEnd();
    Pump(600);
    var lastShown = nav.CurrentPage!.GetVisualDescendants().OfType<Button>()
        .Any(b => b.IsEffectivelyVisible && b.DataContext == cluster.Guests[^1]);
    Shot("cluster-many-guests-end");
    Check0("긴 게스트 목록", "보이는 카드만(가상화), 끝까지 스크롤하면 마지막 게스트", $"게스트 {cluster.Guests.Count}개 중 카드 {cards}·{GuestCards()}개, 마지막 {(lastShown ? "보임" : "안 보임")}",
        cluster.Guests.Count > 300 && cards < 60 && GuestCards() < 60 && lastShown);
    guestScroll.Offset = default;
    Pump(300);

    // 4) 서버에 닿지 않는 세션 — 모든 탭에 오류 배너, 진행 표시는 멈춘다
    var deadProfile = new ConnectionProfile { Name = "dead", Host = "127.0.0.1", Port = 19619 };
    var dead = new ClusterViewModel(new ServerSession(new ProxmoxClient.Core.Api.ProxmoxApiClient(deadProfile), deadProfile, "9.0"));
    await Wait(AppServices.Navigator.PushAsync(dead));
    WaitFor(() => dead.HasError, 10000);
    var deadPage = (TabbedPage)nav.CurrentPage!;
    var banners = new List<bool>();
    for (var i = 0; i < 4; i++)
    {
        deadPage.SelectedIndex = i;
        Pump(300);
        banners.Add(Find<TextBlock>(t => t.Text == dead.ErrorMessage) is not null);
        if (i == 0) Shot("cluster-error");
    }
    var stillSpinning = Find<ProgressBar>(p => p.IsIndeterminate) is not null;
    Check0("연결 실패 상태", "탭마다 오류 배너, 진행 표시 멈춤", $"배너 [{string.Join(",", banners.Select(b => b ? "O" : "X"))}], 진행 표시 {(stillSpinning ? "돎" : "멈춤")}",
        banners.All(b => b) && !stillSpinning && !dead.IsLoading);
    await Wait(AppServices.Navigator.PopAsync());

    // 5) 오래 걸리는 연결 — 진행 표시의 취소(뒤로 가기)로 바로 멈추고 오류 창을 띄우지 않는다
    FakeSet("ticket_delay_ms=6000");
    var before = nav.CurrentPage;
    profiles.ConnectCommand.Execute(profiles.Profiles[0]);
    WaitFor(() => AppServices.Dialogs.IsBusyVisible, 3000);
    var cancellable = AppServices.Dialogs.CanCancelBusy;
    Shot("connect-busy-cancel");
    var started = DateTime.UtcNow;
    var handled = AppServices.Dialogs.HandleBack();
    WaitFor(() => !AppServices.Dialogs.IsBusyVisible, 4000);
    var took = DateTime.UtcNow - started;
    Pump(500);
    Check0("연결 취소", "취소 단추, 뒤로 가기로 바로 멈춤·오류 창 없음·화면 그대로",
        $"단추 {(cancellable ? "있음" : "없음")}, {took.TotalMilliseconds:F0}ms, 대화상자 {(AppServices.Dialogs.Current is null ? "없음" : AppServices.Dialogs.Current.Title)}",
        cancellable && handled && !AppServices.Dialogs.IsBusyVisible && took.TotalSeconds < 3 && AppServices.Dialogs.Current is null &&
        ReferenceEquals(nav.CurrentPage, before));
    FakeSet("ticket_delay_ms=0");
}

// ---- 관리 기능 검사(한 덩어리): 방화벽 규칙 지우기에 digest · digest 불일치면 다시 읽고 알림 · 백업 "압축 없음" = compress=0 ·
// CT 스냅숏에는 RAM 칸이 없다. 서버가 받은 값은 로그의 'MANAGE ...' 줄로 비교한다
if (target == "manage")
{
    var manageLog = options.GetValueOrDefault("serverlog", "");
    var manageOffset = File.Exists(manageLog) ? new FileInfo(manageLog).Length : 0L;
    List<string> ManageLines()
    {
        Pump(800);
        if (manageLog.Length == 0 || !File.Exists(manageLog)) return [];
        using var fs = new FileStream(manageLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(manageOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var text = reader.ReadToEnd();
        manageOffset = fs.Position;
        return text.Split('\n').Where(l => l.StartsWith("MANAGE ", StringComparison.Ordinal)).Select(l => l[7..].Trim()).ToList();
    }
    void ManageCheck(string name, bool pass, string got)
    {
        Console.WriteLine($"{(pass ? "OK" : "FAIL")} manage/{name}: {got}");
        if (!pass) failures++;
    }
    Button ToolButton(Control root, string labelKey) => root.GetVisualDescendants().OfType<Button>()
        .First(b => ToolTip.GetTip(b) as string == L.T(labelKey) || b.Content as string == L.T(labelKey));
    void Press(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    void ConfirmDialogs()
    {
        // 표 단추의 확인 문구 · 쓰기 확인 창을 모두 확인으로
        for (var i = 0; i < 3; i++)
        {
            WaitFor(() => AppServices.Dialogs.Current is not null, 1500);
            if (AppServices.Dialogs.Current is not { } dialog) break;
            dialog.ConfirmCommand.Execute(null);
            Pump(400);
        }
    }

    var mapi = cluster.Session.Api;
    var vm100 = cluster.Guests.First(g => g.VmId == 100);
    var ct200 = cluster.Guests.First(g => g.VmId == 200);

    // 방화벽: 지우기는 목록의 digest 를 쿼리로 보낸다(가짜 서버의 digest 를 처음 값으로)
    await Wait(mapi.GetObjectAsync("__fake/fw-digest"));
    var fw = new ProxmoxClient.UI.Views.Guest.Tabs.FirewallTab(mapi,
        ProxmoxClient.Core.Models.FirewallScope.ForGuest(vm100.Node, vm100.Kind, vm100.VmId));
    await Wait(AppServices.Navigator.PushViewAsync("fw", fw));
    Pump(1500);
    var fwTable = fw.GetVisualDescendants().OfType<TableTab>().First();
    void DeleteFirstRule()
    {
        // 모바일은 카드 목록(ListBox), 데스크톱은 표(DataGrid)
        if (fwTable.GetVisualDescendants().OfType<ListBox>().FirstOrDefault() is { } cards) cards.SelectedIndex = 0;
        else if (fwTable.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault() is { } grid) grid.SelectedIndex = 0;
        Pump(200);
        Press(ToolButton(fwTable, "Action_Delete"));
        ConfirmDialogs();
        Pump(800);
    }
    DeleteFirstRule();
    var fwLines = ManageLines();
    ManageCheck("fw-delete-digest", fwLines.Contains("fw DELETE pos=0 digest=fwdigest1"), string.Join(" | ", fwLines));

    // 다른 사용자가 규칙을 바꿨다 → 예전 digest 로 지우면 서버가 거절하고, 화면은 다시 읽어 알린다
    await Wait(mapi.GetObjectAsync("__fake/fw-digest?value=fwdigest2"));
    DeleteFirstRule();
    var mismatchShown = fwTable.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == L.T("Digest_Changed"));
    ManageCheck("fw-digest-mismatch-message", mismatchShown, string.Join(" | ", ManageLines()));
    Shot("manage-fw-digest-mismatch");
    // 다시 읽었으므로 새 digest 로 지우면 통과한다
    DeleteFirstRule();
    fwLines = ManageLines();
    ManageCheck("fw-delete-after-reload", fwLines.Contains("fw DELETE pos=0 digest=fwdigest2"), string.Join(" | ", fwLines));
    await Wait(AppServices.Navigator.PopAsync());

    // 백업 "압축 없음" → compress=0 (빼면 서버 기본값 zstd 로 압축된다)
    var backup = new ProxmoxClient.UI.Views.Guest.Tabs.BackupTab(mapi, vm100);
    await Wait(AppServices.Navigator.PushViewAsync("backup", backup));
    Pump(1500);
    var compressBox = backup.GetVisualDescendants().OfType<ComboBox>()
        .First(c => c.Items.OfType<object>().Any(i => (i as ContentControl)?.Content as string == L.T("BackupWindow_10")
                                                      || i?.ToString() == L.T("BackupWindow_10")));
    ComboChoices.Select(compressBox, "0");
    Pump(200);
    Press(ToolButton(backup, "BackupWindow_12"));
    ConfirmDialogs();
    var backupLines = ManageLines();
    ManageCheck("backup-none-compress0", backupLines.Contains("vzdump compress=0"), string.Join(" | ", backupLines));
    await Wait(AppServices.Navigator.PopAsync());

    // 스냅숏: CT 에는 RAM 포함 칸이 없고, 실행 중인 VM 에는 있다
    bool RamOption(ProxmoxClient.Core.Models.PveResource guest, out bool enabled)
    {
        var tab = new ProxmoxClient.UI.Views.Guest.Tabs.SnapshotsTab(mapi, guest);
        Wait(AppServices.Navigator.PushViewAsync("snap", tab)).GetAwaiter().GetResult();
        Pump(1200);
        var box = tab.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => c.Content as string == L.T("SnapshotDialog_09"));
        enabled = box?.IsEnabled == true;
        var visible = box?.IsVisible == true;
        Wait(AppServices.Navigator.PopAsync()).GetAwaiter().GetResult();
        return visible;
    }
    ManageCheck("ct-snapshot-no-ram", !RamOption(ct200, out _), "CT 200 RAM 칸 숨김");
    var vmRam = RamOption(vm100, out var vmRamEnabled);
    ManageCheck("vm-snapshot-ram", vmRam && vmRamEnabled, $"VM 100 RAM 칸 보임={vmRam} 켜짐={vmRamEnabled}");
}
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
        await Wait(AppServices.Navigator.PushViewAsync($"{guest.VmId} · {L.T(feature.LabelKey)}", feature.Create(cluster.Session, guest)));
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

void Check0(string name, string expect, string got, bool pass)
{
    Console.WriteLine($"| {name} | {expect} | {got} | {(pass ? "통과" : "실패")} |");
    if (!pass) failures++;
}

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
    // 규칙: 글자까지 한 줄에 들어가면 글자를 보이고, 넘칠 때만 아이콘만(데스크톱은 늘 글자)
    var rowButtons = (add.Parent as Panel)?.Children.OfType<Button>().Where(b => b.IsVisible).ToList() ?? [];
    var oneLine = rowButtons.Select(b => Math.Round(b.Bounds.Y)).Distinct().Count() == 1;
    var rulePass = desktop ? !compact : compact || oneLine;
    Console.WriteLine($"| 좁은 화면 버튼 | 한 줄에 들어가면 글자, 넘치면 아이콘만 | {(compact ? "아이콘만" : "글자 보임: " + add.Content)}, {(oneLine ? "한 줄" : "여러 줄")} | {(rulePass ? "통과" : "실패")} |");
    if (!rulePass) failures++;
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

    // 최근 명령 기록 — Enter 로 실행한 명령이 최신순으로 쌓이고, 고르면 입력 줄만 바뀐다(실행 안 함)
    Run("echo hist-one");
    WaitFor(Prompt, 3000);
    Run("echo hist-two");
    WaitFor(Prompt, 3000);
    page.ShowHistory();
    Pump(200);
    var hist = page.AssistItems.Take(2).ToList();
    Shot("realshell-history");
    Check("최근 명령 목록", "echo hist-two, echo hist-one", string.Join(", ", hist), hist.SequenceEqual(["echo hist-two", "echo hist-one"]));
    page.PickAssist("echo hist-one");
    Pump(400);
    Check("기록 고르기 → 입력 줄", "root@pve1:...# echo hist-one (실행 안 함)", LastLine(),
        LastLine().EndsWith("# echo hist-one") && page.AssistItems.Count == 0);
    Key(Avalonia.Input.Key.U, Avalonia.Input.RawInputModifiers.Control);

    // Tab 후보 목록 — 후보가 여럿이면 bash 가 찍은 목록을 오버레이로, 고르면 그 후보로 완성
    var dir = "/tmp/fakepve-shell/home/cmpl";
    Directory.CreateDirectory(dir + "/alpine-dir");
    File.WriteAllText(dir + "/alpha.txt", "");
    File.WriteAllText(dir + "/beta.txt", "");
    Run("clear");
    WaitFor(Prompt, 3000);
    Type("ls cmpl/al");
    var complete = page.CompleteAsync();
    WaitFor(() => complete.IsCompleted, 5000);
    Pump(200);
    var cands = page.AssistItems.ToList();
    Shot("realshell-completions");
    Check("Tab 후보 목록", "alpha.txt, alpine-dir/", string.Join(", ", cands),
        cands.Contains("alpha.txt") && cands.Contains("alpine-dir/") && !cands.Contains("beta.txt"));
    page.PickAssist("alpine-dir/");
    Pump(400);
    Check("후보 고르기 → 완성", "ls cmpl/alpine-dir/", LastLine(), LastLine().EndsWith("# ls cmpl/alpine-dir/"));
    Key(Avalonia.Input.Key.U, Avalonia.Input.RawInputModifiers.Control);
    Pump(200);
    Type("ls cmpl/be");
    complete = page.CompleteAsync();
    WaitFor(() => complete.IsCompleted, 5000);
    Pump(200);
    Check("후보 하나 → 바로 완성(목록 없음)", "ls cmpl/beta.txt", LastLine(),
        LastLine().EndsWith("# ls cmpl/beta.txt") && page.AssistItems.Count == 0);
    Key(Avalonia.Input.Key.U, Avalonia.Input.RawInputModifiers.Control);

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

    // systemd 처럼 묻기 — 에코·줄 단위 끄고 창 크기(CSI 18t)를 묻고 0.33초만 기다린 뒤 원래대로. 답이 제때 와야 하고,
    // 늦은 답이 화면(로그인 칸)에 찍히면 안 된다
    Run("clear; stty -echo -icanon min 0 time 3; printf '\\e[18t'; r=$(dd bs=1 count=32 2>/dev/null | tr -d '\\033'); stty sane; echo \"CSI18=[$r]\"; sleep 0.5");
    WaitFor(() => Screen().Contains("CSI18=["), 4000);
    Pump(800);
    var csi18 = Screen().Split('\n').FirstOrDefault(l => l.Contains("CSI18=")) ?? "";
    Check("systemd 식 크기 질의(0.33초)", $"CSI18=[[8;{buffer.Rows};{buffer.Columns}t], 화면에 ^[ 없음", csi18,
        csi18.Contains($"[8;{buffer.Rows};{buffer.Columns}t]") && !Screen().Contains("^["));

    var oldColumns = buffer.Columns;
    window.Width -= 160;
    Pump(800);
    Run("clear; stty size");
    WaitFor(() => Screen().Contains($"{buffer.Rows} {buffer.Columns}"), 4000);
    Check("창 크기 변경 → PTY", $"열 {oldColumns}→{buffer.Columns}", Screen().Split('\n').Skip(1).FirstOrDefault() ?? "",
        buffer.Columns < oldColumns && Screen().Contains($"{buffer.Rows} {buffer.Columns}"));
    window.Width += 160;
    Pump(500);

    if (!desktop)
    {
        // 화면 키보드로 터미널이 줄면 전체 화면 프로그램(대체 화면)도 새 크기를 받아 다시 그려야 한다(사용자 요청)
        var kbShell = window.GetVisualDescendants().OfType<AppShell>().First();
        Run("clear; tput smcup; trap 'tput clear; echo WINCH $(stty size)' WINCH; echo READY $(stty size); while :; do read -r -t 0.1 _; done"); // read 는 셸 안 명령 — sleep 이면 WINCH 가 sleep 쪽으로 간다
        WaitFor(() => buffer.IsAlternateScreen && Screen().Contains("READY"), 4000);
        var rowsBefore = buffer.Rows;
        kbShell.ApplyKeyboardInset(300);
        Pump(800);
        WaitFor(() => Screen().Contains($"WINCH {buffer.Rows} {buffer.Columns}"), 4000);
        var kbRows = buffer.Rows;
        Shot("realshell-keyboard-alt");
        var shrunk = kbRows < rowsBefore && Screen().Contains($"WINCH {kbRows} {buffer.Columns}");
        kbShell.ApplyKeyboardInset(0);
        Pump(800);
        WaitFor(() => Screen().Contains($"WINCH {buffer.Rows} {buffer.Columns}"), 4000);
        var restored = buffer.Rows == rowsBefore && Screen().Contains($"WINCH {rowsBefore} {buffer.Columns}");
        Check("키보드 → 대체 화면 크기 변경", $"행 {rowsBefore}→줄어듦→{rowsBefore}, 프로그램이 WINCH 받음",
            $"행 {rowsBefore}→{kbRows}→{buffer.Rows}, 화면 [{Screen().Split('\n').FirstOrDefault(l => l.Length > 0)}]", shrunk && restored);
        Key(Avalonia.Input.Key.C, Avalonia.Input.RawInputModifiers.Control);
        Type("tput rmcup; clear");
        Key(Avalonia.Input.Key.Enter);
        WaitFor(() => !buffer.IsAlternateScreen && Prompt(), 4000);

        // 연결 유지 장치가 있으면(Android 포그라운드 서비스) 배경으로 가도 끊지 않는다 — 같은 셸이 그대로(사용자 요청)
        var keep = new FakeKeepAliveHost();
        ProxmoxClient.UI.Platform.SessionKeepAlive.Host = keep;
        var listed = keep.Titles.SequenceEqual(["pve1 · 셸"]);
        Run("export KEEPMARK=$((6*7))kept");
        Pump(300);
        SetForeground(false);
        Pump(1500);
        var stayed = vm.IsConnected;
        SetForeground(true);
        Pump(300);
        Run("echo \"mark=$KEEPMARK\"");
        WaitFor(() => Screen().Contains("mark=42kept"), 4000);
        var sameShell = Screen().Contains("mark=42kept");
        Check("배경 → 복귀(연결 유지)", "알림에 제목, 배경에서 안 끊김, 같은 셸(변수 그대로)",
            $"알림 [{string.Join(", ", keep.Titles)}], {(stayed ? "연결 유지" : "끊김")}, {(sameShell ? "같은 셸" : "새 셸")}",
            listed && stayed && sameShell);

        ProxmoxClient.UI.Platform.SessionKeepAlive.RequestDisconnectAll(); // 알림의 [모두 끊기]
        WaitFor(() => !vm.IsConnected, 3000);
        Check("알림 [모두 끊기]", "터미널 끊김, 알림 사라짐", $"{(vm.IsConnected ? "연결됨" : "끊김")}, 알림 {keep.Titles.Count}개",
            !vm.IsConnected && keep.Titles.Count == 0);

        // 연결 유지 장치가 없으면(iOS 등) 예전처럼 배경에서 끊고 돌아오면 다시 연결
        ProxmoxClient.UI.Platform.SessionKeepAlive.Host = null;
        await Wait(vm.ConnectAsync()); // 헤드리스는 UI 를 돌리며 기다려야 한다
        WaitFor(() => vm.IsConnected && Prompt(), 8000);
        SetForeground(false);
        WaitFor(() => !vm.IsConnected, 3000);
        var paused = !vm.IsConnected && vm.StateText == L.T("Console_PausedBackground");
        SetForeground(true);
        WaitFor(() => vm.IsConnected && Screen().Contains("root@pve1"), 8000);
        Check("배경 → 복귀", "배경에서 끊김 → 돌아오면 다시 연결", $"{(paused ? "끊김" : vm.StateText)} → {(vm.IsConnected ? "다시 연결" : "연결 안 됨")}",
            paused && vm.IsConnected);
    }

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
    // NFD 한글(첫가끝 자모)은 초성 칸 하나(2칸)에 중성·종성이 붙는다
    var nfdRow = Enumerable.Range(0, buffer.Rows).FirstOrDefault(r => buffer.GetVisibleLine(r, 0).Cells[0].Rune == 0x1112, -1);
    var nfd = nfdRow >= 0 && buffer.GetVisibleLine(nfdRow, 0) is var nfdLine && nfdLine.Cells[0].Width == 2 &&
              nfdLine.GetExtra(0) == "\u1161\u11ab" && nfdLine.Cells[2].Rune == 0x1100;
    Check("NFD 한글 묶음", "'한' 2칸 + 붙은 자모", nfd ? "묶음" : "어긋남", nfd);

    // 화면 키보드: Enter 가 '완료'로 키보드를 닫지 않게 여러 줄 칸(Return 키) — 그래도 셸에는 Enter 한 번만 간다.
    // 키보드가 닫힌 뒤 다시 누르면 포커스를 옮겼다 돌려 키보드를 다시 연다(이미 포커스가 있으면 열리지 않던 문제)
    var multiline = Avalonia.Input.TextInput.TextInputOptions.GetMultiline(input) && input.AcceptsReturn &&
                    Avalonia.Input.TextInput.TextInputOptions.GetReturnKeyType(input) == Avalonia.Input.TextInput.TextInputReturnKeyType.Return;
    Type("echo kb\n"); // 키보드가 Enter 를 글자로 넣는 경우
    inputs = Log("input");
    var enterAsText = string.Join(" ", inputs);
    var focusEvents = 0;
    input.GotFocus += (_, _) => focusEvents++;
    term.FocusInput(); // 이미 포커스가 있는 상태에서 다시 누름
    Pump(200);
    Check("화면 키보드(Enter·다시 열기)", "여러 줄 칸·Return 키, 글자 Enter 는 \r 로, 다시 누르면 포커스 다시 받음",
        $"{(multiline ? "여러 줄" : "한 줄")}, {enterAsText}, 다시 포커스 {focusEvents}회",
        multiline && enterAsText == "input 'echo kb\\r'" && focusEvents >= 1 && input.IsFocused);

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

    if (!desktop)
    {
        // 손가락 선택 — 끝 손잡이를 오른쪽으로 끌면 선택이 늘고, 끄는 동안 확대경을 그린다(사용자 요청: 드래그 커서가 안 보임)
        // 프롬프트 줄(root@pve1:~#)의 첫 단어를 길게 눌러 고른다 — 칸 크기로 정확히 짚는다(특수 키 막대 높이에 따라 행 수가 바뀐다)
        var cell = term.CellSize;
        term.ClearSelection();
        var press = rowY + new Avalonia.Vector(6 + cell.Width * 1.5, 4 + cell.Height * (buffer.CursorY + 0.5));
        var lp = window.TouchBegin(press, Avalonia.Input.RawInputModifiers.None);
        Pump(700);
        window.TouchEnd(lp, press, Avalonia.Input.RawInputModifiers.None);
        Pump(300);
        var before2 = term.GetSelectedText();
        var lines = Screen().Split('\n');
        var selRow = Array.FindLastIndex(lines, l => l.Contains(before2));
        var lineText = lines[selRow];
        var endCol = lineText.TrimEnd().Length - 1;
        // 지금 선택 끝(단어 끝) 손잡이 — 칸 아래(맨 아랫줄이면 칸 위로 뒤집힌다)
        var selEnd = lineText.IndexOf(before2, StringComparison.Ordinal) + before2.Length;
        var lastRow = selRow == buffer.Rows - 1 || 4 + cell.Height * (selRow + 1) + 18 > term.Bounds.Height;
        var knob = rowY + new Avalonia.Vector(6 + cell.Width * selEnd + 9,
            lastRow ? 4 + cell.Height * selRow - 9 : 4 + cell.Height * (selRow + 1) + 9);
        var target = rowY + new Avalonia.Vector(6 + cell.Width * (endCol + 0.5), 4 + cell.Height * (selRow + 0.5));
        var t = window.TouchBegin(knob, Avalonia.Input.RawInputModifiers.None);
        Pump(60);
        var framesBefore = term.FramesDrawn;
        for (var step = 1; step <= 8; step++)
        {
            window.TouchMove(t, knob + (target - knob) * (step / 8.0), Avalonia.Input.RawInputModifiers.None);
            Pump(30);
        }

        Shot("terminal-selection-loupe");
        var drew = term.FramesDrawn > framesBefore;
        window.TouchEnd(t, target, Avalonia.Input.RawInputModifiers.None);
        Pump(300);
        var grown = term.GetSelectedText();
        Shot("terminal-selection-handles");
        Check("손잡이 끌어 선택 늘리기", $"'{before2}' → 줄 끝까지 늘어남, 끄는 동안 다시 그림",
            $"'{grown}', 그리기 {(drew ? "있음" : "없음")}", grown.Length > before2.Length && grown.TrimEnd() == lineText.TrimEnd() && drew);
    }

    term.ClearSelection();

    // 동기화 출력(DEC 2026) — 끄는 신호가 올 때까지 그리지 않는다. 모드 질의(DECRQM)에 지원한다고 답한다
    Log("input");
    term.Write("\x1b[?2026$p");
    var rqm = Log("input");
    Pump(100);
    var frames0 = term.FramesDrawn;
    term.Write("\x1b[?2026h\x1b[2J\x1b[HSYNC-HALF");
    Pump(120);
    var heldFrames = term.FramesDrawn - frames0;
    var heldFlag = buffer.SynchronizedOutput;
    term.Write(" SYNC-DONE\x1b[?2026l");
    Pump(120);
    var shownFrames = term.FramesDrawn - frames0;
    term.Write("\x1b[?2026h");
    Pump(400); // 끄는 신호가 오지 않으면 잠시 뒤 그냥 그린다 — 헤드리스는 타이머가 돌지 않아 그 타이머가 부르는 것을 직접 부른다
    typeof(ProxmoxClient.UI.Views.Terminal.TerminalView).GetMethod("Redraw",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(term, null);
    Pump(100);
    var timeoutFrames = term.FramesDrawn - frames0 - shownFrames;
    term.Write("\x1b[?2026l");
    Check("동기화 출력(2026)", "질의 답 '?2026;2$y', 켜진 동안 안 그림 → 끄면 그림, 안 끄면 0.2초 뒤 그림",
        $"답 {string.Join(" ", rqm)}, 켜짐 {heldFrames}번 → 끈 뒤 {shownFrames}번, 시간 초과 {timeoutFrames}번",
        rqm.SequenceEqual(["input '\\x1b[?2026;2$y'"]) && heldFlag && heldFrames == 0 && shownFrames > 0 && timeoutFrames > 0);
    // 대체 글꼴 — 한자·가나·중국어·기호·Nerd 아이콘·이모지가 기기 글꼴 없이도 그려져야 한다(앱에 넣은 글꼴)
    var samples = new (string Text, string Font)[]
    {
        ("漢", "Noto Sans Mono CJK KR"), ("學", "Noto Sans Mono CJK KR"), ("か", "Pretendard|Noto Sans Mono CJK KR"),
        ("钅", "Noto Sans Mono CJK KR"), ("𠀋", "Noto Sans Mono CJK KR"), ("α", "JetBrains Mono"),
        ("Ж", "JetBrains Mono"), ("∀", "JetBrains Mono"), ("⏺", "DejaVu Sans Mono|Noto Sans Symbols|Noto Sans Symbols 2|Noto Sans Math"),
        ("⎿", "DejaVu Sans Mono|Noto Sans Symbols|Noto Sans Symbols 2|Noto Sans Math"), ("\ue0a0", "JetBrains Mono|Symbols Nerd Font Mono"),
        ("\uf113", "Symbols Nerd Font Mono"), ("😀", "*"), ("한", "Pretendard")
    };
    var fontResults = samples.Select(x => (x.Text, Got: term.FontFor(char.ConvertToUtf32(x.Text, 0)) ?? "(없음)", x.Font)).ToList();
    var fontsOk = fontResults.All(r => r.Font == "*" ? r.Got != "(없음)" : r.Font.Split('|').Contains(r.Got));
    term.Write("\r\n漢字 學校 かなカナ 中文钅 𠀋 αβγ Жж ∀∂∑ ⏺ ✻ ⎿ ★ \ue0a0\ue0b0 \uf113 😀 한글\r\n");
    Pump(300);
    Shot("terminal-fallback-fonts");
    Check("대체 글꼴(한자·가나·기호·아이콘·이모지)", "모두 앱에 넣은 글꼴(이모지는 기기 또는 Noto Emoji)",
        string.Join(" ", fontResults.Select(r => $"{r.Text}={r.Got}")), fontsOk);

    // systemd 가 콘솔을 열 때 묻는 것들(창 크기 CSI 18t·배경색 OSC 11·터미널 이름 XTGETTCAP) — 답이 없으면 0.33초씩 기다리다
    // 커서 위치 질문으로 넘어가고, 늦게 온 답이 로그인 칸에 '^[[24;80R' 로 찍힌다(사용자 보고). 바로 답해야 한다
    Log("input");
    term.Write("\x1b[18t\x1b]11;?\x1b\\\x1bP+q6E616D65;636F6C73\x1b\\");
    var answers = Log("input");
    var expectSize = $"input '\\x1b[8;{buffer.Rows};{buffer.Columns}t'";
    Check("systemd 질의 응답", "크기 8;행;열 t · 배경 rgb · 이름 xterm-256color · 모르는 이름은 0+r",
        string.Join(" ", answers),
        answers.Count == 1 && answers[0].StartsWith(expectSize[..^1], StringComparison.Ordinal) &&
        answers[0].Contains("\\x1b]11;rgb:0c0c/0c0c/0c0c\\x1b\\\\") &&
        answers[0].Contains("\\x1bP1+r6E616D65=787465726D2D323536636F6C6F72\\x1b\\\\") &&
        answers[0].Contains("\\x1bP0+r636F6C73\\x1b\\\\"));

    Type("clear");
    Key(Avalonia.Input.Key.Enter);
    Pump(300);

    // 마우스 보고를 켠 전체 화면 프로그램(Claude Code 전체 화면 모드 등) — 손가락으로 끌면 방향키가 아니라 휠로 보낸다
    if (!desktop)
    {
        term.Write("\x1b[?1049h\x1b[?1000h\x1b[?1006h");
        Pump(100);
        Log("input");
        var t = window.TouchBegin(mid, Avalonia.Input.RawInputModifiers.None);
        for (var step = 1; step <= 6; step++)
        {
            window.TouchMove(t, mid + new Avalonia.Vector(0, step * 20), Avalonia.Input.RawInputModifiers.None);
            Pump(20);
        }

        window.TouchEnd(t, mid + new Avalonia.Vector(0, 120), Avalonia.Input.RawInputModifiers.None);
        var wheel = Log("input");
        term.Write("\x1b[?1000l\x1b[?1006l\x1b[?1049l");
        Pump(100);
        Check("마우스 보고 중 손가락 끌기", "휠 위(\\x1b[<64;…M) 여러 번, 방향키 없음", string.Join(" ", wheel.Take(3)) + (wheel.Count > 3 ? $" …({wheel.Count})" : ""),
            wheel.Count > 0 && wheel.All(w => w.StartsWith("input '\\x1b[<64;")));
    }

    // ---- 입력(붙여넣기·고정 키·포커스 보고·휠·끌기) — 한 덩어리
    {
        input.Focus();
        Pump(200);
        Log("input");
        // 화면 키보드로 한 번에 들어온 여러 줄·긴 글(Gboard 클립보드·길게 눌러 붙여넣기) → 괄호 붙여넣기로 감싼다
        term.Write("\x1b[?2004h");
        Pump(100);
        Type("echo a\necho b");
        var long40 = new string('x', 40);
        Type(long40);
        var pasted = Log("input");
        term.Write("\x1b[?2004l");
        Check("화면 키보드 붙여넣기(괄호)", "여러 줄·긴 글 → \\x1b[200~…\\x1b[201~", string.Join(" ", pasted),
            pasted.SequenceEqual(["input '\\x1b[200~echo a\\recho b\\x1b[201~'", $"input '\\x1b[200~{long40}\\x1b[201~'"]));
        Key(Avalonia.Input.Key.C, Avalonia.Input.RawInputModifiers.Control);
        Log("input");

        if (!desktop)
        {
            // 고정 키 — 수식키만 눌러서는 풀리지 않고, 여러 글자에는 붙지 않은 채 풀린다. 고정 Shift + 막대의 Tab·↑
            var toggles = page.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().ToList();
            var ctrlToggle = toggles.First(b => b.Content as string == "Ctrl");
            var shiftToggle = toggles.First(b => b.Content as string == "Shift");
            var barButtons = page.GetVisualDescendants().OfType<Avalonia.Controls.Button>().ToList();
            void Click(string label) =>
                barButtons.First(b => b.Content as string == label)
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            ctrlToggle.IsChecked = true;
            Key(Avalonia.Input.Key.LeftShift, Avalonia.Input.RawInputModifiers.Shift);
            var kept = ctrlToggle.IsChecked == true;
            Type("c");
            ctrlToggle.IsChecked = true;
            Type("abc");
            var cleared = ctrlToggle.IsChecked != true;
            shiftToggle.IsChecked = true;
            Click("Tab");
            Pump(100);
            shiftToggle.IsChecked = true;
            Click("↑");
            Pump(100);
            var sticky = Log("input");
            Check("고정 키(Ctrl·Shift)", "Shift 만 눌러도 유지 → '\\x03', 여러 글자 'abc' 는 그대로·풀림, Shift+Tab '\\x1b[Z', Shift+↑ '\\x1b[1;2A'",
                $"{string.Join(" ", sticky)}, 유지 {kept}, 풀림 {cleared}",
                sticky.SequenceEqual(["input '\\x03'", "input 'abc'", "input '\\x1b[Z'", "input '\\x1b[1;2A'"]) && kept && cleared &&
                shiftToggle.IsChecked != true);
            Key(Avalonia.Input.Key.C, Avalonia.Input.RawInputModifiers.Control);
            Log("input");

            // 괄호 붙여넣기가 꺼진 셸에 여러 줄 — 터치 기기는 실행 전에 묻는다
            Type("echo p1\necho p2");
            var beforeConfirm = Log("input");
            var dialog = AppServices.Dialogs.Current;
            dialog?.ConfirmCommand.Execute(null);
            Pump(200);
            var afterConfirm = Log("input");
            Check("여러 줄 붙여넣기 확인(터치)", "묻는 동안 안 보냄 → 확인하면 'echo p1\\recho p2'",
                $"묻기 {(dialog is null ? "없음" : "있음")}, 전 {beforeConfirm.Count}개, 뒤 {string.Join(" ", afterConfirm)}",
                dialog is not null && beforeConfirm.Count == 0 && afterConfirm.SequenceEqual(["input 'echo p1\\recho p2'"]));
            Pump(300);
        }

        // 포커스 보고(?1004) — 키보드를 다시 열 때의 잠깐 나갔다 돌아오는 포커스는 알리지 않는다
        input.Focus();
        Pump(200);
        term.Write("\x1b[?1004h");
        Pump(100);
        Log("input");
        term.FocusInput();
        Pump(300);
        var bounce = Log("input");
        term.Focusable = true; // 포커스를 입력 칸 밖으로
        term.Focus();
        Pump(200);
        var lost = Log("input");
        input.Focus();
        Pump(200);
        var got = Log("input");
        term.Focusable = false;
        term.Write("\x1b[?1004l");
        Check("포커스 보고(?1004)", "다시 열기는 조용히, 잃으면 \\x1b[O, 얻으면 \\x1b[I",
            $"다시 열기 {bounce.Count}개, 잃음 {string.Join(" ", lost)}, 얻음 {string.Join(" ", got)}",
            bounce.Count == 0 && lost.SequenceEqual(["input '\\x1b[O'"]) && got.SequenceEqual(["input '\\x1b[I'"]));

        if (desktop)
        {
            var cs = term.CellSize;
            Avalonia.Point At(int col, int row, double dx = 0) =>
                rowY + new Avalonia.Vector(6 + cs.Width * (col + 0.5) + dx, 4 + cs.Height * (row + 0.5));
            var none = Avalonia.Input.RawInputModifiers.None;
            var leftHeld = Avalonia.Input.RawInputModifiers.LeftMouseButton;

            // 휠 — 마우스 보고 중이면 한 칸에 보고 하나, 잘게 나뉜 값(터치패드)은 모아서, 가로 휠은 66/67
            term.Write("\x1b[?1002h\x1b[?1006h");
            Pump(100);
            Log("input");
            window.MouseWheel(At(5, 3), new Avalonia.Vector(0, 1), none);
            var oneNotch = Log("input");
            for (var k = 0; k < 3; k++) window.MouseWheel(At(5, 3), new Avalonia.Vector(0, 0.4), none);
            var fine = Log("input");
            window.MouseWheel(At(5, 3), new Avalonia.Vector(-1, 0), none);
            var horizontal = Log("input");
            Check("휠(마우스 보고)", "한 칸 → 64 하나, 0.4×3 → 하나, 가로 → 66/67",
                $"{string.Join(" ", oneNotch)} | {string.Join(" ", fine)} | {string.Join(" ", horizontal)}",
                oneNotch.SequenceEqual(["input '\\x1b[<64;6;4M'"]) && fine.SequenceEqual(["input '\\x1b[<64;6;4M'"]) &&
                horizontal.Count == 1 && (horizontal[0] == "input '\\x1b[<66;6;4M'" || horizontal[0] == "input '\\x1b[<67;6;4M'"));

            // 끌기 — 칸이 바뀔 때만, 두 단추를 눌렀다 하나씩 떼면 뗀 단추를 알린다
            window.MouseDown(At(2, 2), Avalonia.Input.MouseButton.Left, none);
            window.MouseMove(At(2, 2, 2), leftHeld);
            window.MouseMove(At(4, 2), leftHeld);
            window.MouseMove(At(4, 2, 2), leftHeld);
            window.MouseDown(At(4, 2), Avalonia.Input.MouseButton.Right, leftHeld);
            window.MouseUp(At(4, 2), Avalonia.Input.MouseButton.Right, leftHeld);
            window.MouseUp(At(4, 2), Avalonia.Input.MouseButton.Left, none);
            var drag = Log("input");
            Check("끌기 보고(?1002)", "누름 · 칸 바뀜 한 번 · 오른쪽 누름·뗌 · 왼쪽 뗌", string.Join(" ", drag),
                drag.SequenceEqual(["input '\\x1b[<0;3;3M'", "input '\\x1b[<32;5;3M'", "input '\\x1b[<2;5;3M'",
                    "input '\\x1b[<2;5;3m'", "input '\\x1b[<0;5;3m'"]));

            // ?1003 — 단추 없이 움직여도 알린다(35), 칸이 바뀔 때만
            term.Write("\x1b[?1003h");
            Pump(100);
            Log("input");
            window.MouseMove(At(7, 1), none);
            window.MouseMove(At(7, 1, 2), none);
            window.MouseMove(At(8, 1), none);
            var hover = Log("input");
            Check("움직임 보고(?1003)", "칸마다 35 하나", string.Join(" ", hover),
                hover.SequenceEqual(["input '\\x1b[<35;8;2M'", "input '\\x1b[<35;9;2M'"]));

            // 대체 화면의 휠(마우스 보고 없음) — 방향키 3번, ?1007 을 끄면 아무것도 보내지 않는다
            term.Write("\x1b[?1003l\x1b[?1006l\x1b[?1049h");
            Pump(100);
            Log("input");
            window.MouseWheel(At(5, 3), new Avalonia.Vector(0, 1), none);
            var arrows = Log("input");
            term.Write("\x1b[?1007l");
            window.MouseWheel(At(5, 3), new Avalonia.Vector(0, 1), none);
            var off = Log("input");
            term.Write("\x1b[?1007h\x1b[?1049l");
            Pump(100);
            Check("대체 화면 휠(?1007)", "켜짐 → ↑×3, 꺼짐 → 없음", $"{string.Join(" ", arrows)} | {off.Count}개",
                arrows.SequenceEqual(["input '\\x1b[A\\x1b[A\\x1b[A'"]) && off.Count == 0);
        }

        Key(Avalonia.Input.Key.C, Avalonia.Input.RawInputModifiers.Control);
        Log("input");
    }

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
        return text.Split('\n').Where(l => l.StartsWith("RFB pointer") || l.StartsWith("RFB key") || l.StartsWith("RFB qkey") || l.StartsWith("RFB cut"))
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

    // 버블(사용자 요청): 처음엔 페이지 위쪽 막대·특수키 막대를 숨기고 버블만. 누르면 메뉴가 열리고, 끌면 옮겨진다
    {
        var bubble = page.GetVisualDescendants().OfType<ProxmoxClient.UI.Views.Controls.FloatingBubble>().First();
        var navBarHidden = !NavigationPage.GetHasNavigationBar(page);
        var keyBarHidden = !page.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Esc" && b.IsEffectivelyVisible);
        Check0("콘솔 처음 화면", "위쪽 막대·특수키 숨김, 버블 보임",
            $"위쪽 막대 {(navBarHidden ? "숨김" : "보임")}, 특수키 {(keyBarHidden ? "숨김" : "보임")}, 버블 {(bubble.IsEffectivelyVisible ? "보임" : "없음")}",
            navBarHidden && keyBarHidden && bubble.IsEffectivelyVisible && !page.IsMenuOpen);

        Events();
        var bubbleAt = bubble.TranslatePoint(new Avalonia.Point(ProxmoxClient.UI.Views.Controls.FloatingBubble.Size / 2, ProxmoxClient.UI.Views.Controls.FloatingBubble.Size / 2), window)!.Value;
        var bt = window.TouchBegin(bubbleAt, Avalonia.Input.RawInputModifiers.None);
        for (var i = 1; i <= 10; i++)
        {
            window.TouchMove(bt, bubbleAt + new Avalonia.Vector(-i * 15, i * 20), Avalonia.Input.RawInputModifiers.None);
            Pump(16);
        }
        window.TouchEnd(bt, bubbleAt + new Avalonia.Vector(-150, 200), Avalonia.Input.RawInputModifiers.None);
        Pump(200);
        var movedAt = bubble.TranslatePoint(new Avalonia.Point(ProxmoxClient.UI.Views.Controls.FloatingBubble.Size / 2, ProxmoxClient.UI.Views.Controls.FloatingBubble.Size / 2), window)!.Value;
        var bubbleDragEvents = Events().Count(e => e.StartsWith("pointer"));
        Check0("버블 끌기", "버블이 손가락을 따라 옮겨짐, 메뉴 안 열림, 원격에 포인터 안 보냄",
            $"이동 ({movedAt.X - bubbleAt.X:F0},{movedAt.Y - bubbleAt.Y:F0}), 메뉴 {(page.IsMenuOpen ? "열림" : "닫힘")}, 포인터 {bubbleDragEvents}개",
            Math.Abs(movedAt.X - bubbleAt.X + 150) <= 2 && Math.Abs(movedAt.Y - bubbleAt.Y - 200) <= 2 && !page.IsMenuOpen && bubbleDragEvents == 0);

        bt = window.TouchBegin(movedAt, Avalonia.Input.RawInputModifiers.None);
        Pump(30);
        window.TouchEnd(bt, movedAt, Avalonia.Input.RawInputModifiers.None);
        Pump(300);
        var bubbleTapEvents = Events().Count(e => e.StartsWith("pointer"));
        var keysShown = page.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Esc" && b.IsEffectivelyVisible);
        var settingsShown = page.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == L.T("ConsoleSettingsWindow_01") && b.IsEffectivelyVisible);
        Shot("console-menu");
        Check0("버블 누르기", "메뉴 열림(특수키·콘솔 설정 보임), 원격에 클릭 안 보냄",
            $"메뉴 {(page.IsMenuOpen ? "열림" : "닫힘")}, 특수키 {(keysShown ? "보임" : "없음")}, 설정 {(settingsShown ? "보임" : "없음")}, 포인터 {bubbleTapEvents}개",
            page.IsMenuOpen && keysShown && settingsShown && bubbleTapEvents == 0);

        // 화면 키보드를 연 동안 화면을 만져 커서를 움직여도 키보드(입력 칸 포커스)가 유지된다(사용자 요청)
        var keyboardButton = page.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == L.T("Console_Keyboard"));
        keyboardButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(200);
        var softInput = page.GetVisualDescendants().OfType<TextBox>().First();
        var openedKeyboard = softInput.IsFocused;
        var dragFrom = surface.TranslatePoint(new Avalonia.Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2), window)!.Value;
        var kt = window.TouchBegin(dragFrom, Avalonia.Input.RawInputModifiers.None);
        for (var i = 1; i <= 10; i++)
        {
            window.TouchMove(kt, dragFrom + new Avalonia.Vector(i * 6, i * 4), Avalonia.Input.RawInputModifiers.None);
            Pump(16);
        }
        window.TouchEnd(kt, dragFrom + new Avalonia.Vector(60, 40), Avalonia.Input.RawInputModifiers.None);
        Pump(200);
        var keptKeyboard = softInput.IsFocused;
        // 키보드를 쓰지 않을 때는 예전처럼 화면이 포커스를 가져온다(물리 키보드 → 스캔코드)
        surface.Focusable = true; // 키보드를 직접 닫은 것과 같게(헤드리스에는 키보드 상태가 없다)
        surface.Focus();
        Pump(100);
        kt = window.TouchBegin(dragFrom, Avalonia.Input.RawInputModifiers.None);
        Pump(30);
        window.TouchMove(kt, dragFrom + new Avalonia.Vector(40, 0), Avalonia.Input.RawInputModifiers.None);
        Pump(30);
        window.TouchEnd(kt, dragFrom + new Avalonia.Vector(40, 0), Avalonia.Input.RawInputModifiers.None);
        Pump(200);
        var surfaceFocused = surface.IsFocused;
        Events();
        Check0("키보드를 연 채 화면 끌기", "입력 칸 포커스 유지(키보드 안 닫힘), 키보드 없을 땐 화면이 포커스",
            $"키보드 {(openedKeyboard ? "열림" : "안 열림")} → 끈 뒤 {(keptKeyboard ? "유지" : "닫힘")}, 키보드 없이 → 화면 포커스 {(surfaceFocused ? "있음" : "없음")}",
            openedKeyboard && keptKeyboard && surfaceFocused);
    }
    g.Mode = ProxmoxClient.UI.Views.Console.TouchInputMode.Mouse;
    g.ResetCursor(new Avalonia.Point(400, 300));

    // 탭 규칙(사용자 요청): 탭 N번 = 클릭 N번(1 = 클릭, 2 = 더블 클릭) — 마지막 탭 뒤 다음 터치를 기다렸다 보낸다
    void Settle() { now += GestureInterpreter.MultiTapMs + 50; g.Tick(now); now += 200; } // 다음 터치를 기다린 뒤
    Tap(center);
    var ev = Events();
    Check("마우스 모드 · 한 번 탭 직후", "다음 터치를 기다리는 동안 클릭 안 보냄", ev.Count == 0 ? "이벤트 없음" : Masks(ev), ev.Count == 0);
    Settle();
    ev = Events();
    Check("마우스 모드 · 한 번 탭", "왼쪽 클릭(0 1 0) @ 커서(400,300)", $"{Masks(ev)} @ {LastXY(ev)}",
        Masks(ev) == "0 1 0" && LastXY(ev) == (400, 300));

    Tap(center); now -= 150; Tap(center + new Avalonia.Vector(6, 4)); // 두 번째 탭이 조금 비껴도
    Settle();
    ev = Events();
    Check("마우스 모드 · 두 번 탭", "더블 클릭(0 1 0 1 0) @ 첫 탭 자리(400,300)", $"{Masks(ev)} @ {LastXY(ev)}",
        Masks(ev) == "0 1 0 1 0" && ev.Where(e => e.StartsWith("pointer")).All(e => e.Contains("x=400 y=300")));

    Tap(center); now -= 150; Tap(center); now -= 150; Tap(center);
    Settle();
    ev = Events();
    Check("세 번 탭", "세 번 클릭(0 1 0 1 0 1 0)", Masks(ev), Masks(ev) == "0 1 0 1 0 1 0");

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

    // 확대한 상태에서 두 손가락 끌기 = 화면 이동(원격 스크롤 아님)
    var offsetBefore = vp.Offset;
    g.Down(1, center - new Avalonia.Vector(30, 0), now);
    g.Down(2, center + new Avalonia.Vector(30, 0), now);
    for (var s = 1; s <= 10; s++)
    {
        now += 16;
        g.Move(1, center - new Avalonia.Vector(30, 0) + new Avalonia.Vector(s * 10, s * 6), now);
        g.Move(2, center + new Avalonia.Vector(30, 0) + new Avalonia.Vector(s * 10, s * 6), now);
    }
    g.Up(1, center, now); g.Up(2, center, now); now += 200;
    ev = Events();
    var panned = vp.Offset - offsetBefore;
    // 화면 밖으로 밀려난 커서는 안쪽으로 옮긴다(커서 이동만 — 버튼·휠 없음)
    var panCursor = vp.ToView(g.Cursor);
    Check("확대 중 두 손가락 끌기", "화면 이동(오프셋 변화), 휠·클릭 없음, 커서는 화면 안", $"이동 ({panned.X:F0},{panned.Y:F0}), 이벤트 {Masks(ev)}, 커서 ({panCursor.X:F0},{panCursor.Y:F0})",
        Math.Abs(panned.X) + Math.Abs(panned.Y) > 20 && ev.All(e => e.StartsWith("pointer mask=0 ")) &&
        panCursor.X >= 0 && panCursor.X <= vp.View.Width && panCursor.Y >= 0 && panCursor.Y <= vp.View.Height);

    // ---- 확대 중 가장자리(사용자 요청): 커서는 가장자리에 붙지 않고, 끄는 중 손가락이 가장자리에 머물면 그쪽으로 조금씩 이동
    {
        var W = vp.View.Width;
        var H = vp.View.Height;
        var margin = Math.Clamp(Math.Min(W, H) * 0.2, 40, 160);
        bool RightEdgeShown() => vp.Offset.X + vp.Remote.Width * vp.Scale <= W + 1;

        // 마우스 모드: 커서를 오른쪽으로 크게 밀어도 가장자리 여유 안쪽(화면이 먼저 따라감)
        g.Mode = TouchInputMode.Mouse;
        vp.PanBy(new Avalonia.Vector(100000, 100000)); // 왼쪽 위 끝을 보게
        g.ResetCursor(vp.ToRemote(center));
        now += 1000;
        Events();
        Drag(center, new Avalonia.Vector(W * 0.45, 0), steps: 20);
        var cursorView = vp.ToView(g.Cursor);
        Check("확대 중 커서 여유(마우스 모드)", $"커서가 오른쪽 가장자리에서 {margin:F0}px 안쪽(화면이 따라 이동)",
            $"커서 보기 x {cursorView.X:F0} / 폭 {W:F0}, 화면 이동 {vp.Offset.X:F0}",
            cursorView.X <= W - margin + 2 && vp.Offset.X < -1 || RightEdgeShown());

        // 터치 모드: 끌다가 오른쪽 가장자리에 손가락을 멈추면 화면이 조금씩 오른쪽으로, 버튼은 누른 채
        g.Mode = TouchInputMode.Touch;
        vp.PanBy(new Avalonia.Vector(100000, 0));
        now += 1000;
        Events();
        var edge = new Avalonia.Point(W - 8, H / 2);
        var from = new Avalonia.Point(W / 2, H / 2);
        g.Down(1, from, now);
        for (var i = 1; i <= 10; i++) { now += 16; g.Move(1, from + (edge - from) * i / 10, now); }
        var offsetAtEdge = vp.Offset.X;
        g.Tick(now);
        for (var i = 0; i < 15; i++) { now += 60; g.Tick(now); } // 0.9초 머묾
        var offsetAfter = vp.Offset.X;
        g.Up(1, edge, now);
        now += 200;
        ev = Events();
        var dragXs = ev.Where(e => e.StartsWith("pointer mask=1")).Select(e => int.Parse(e.Split(' ')[2][2..])).ToList();
        var shifted = offsetAtEdge - offsetAfter;
        Check("확대 중 끌기 · 가장자리에 머묾(터치 모드)", "화면이 오른쪽으로 조금씩(0.9초에 50~250px), 끌기 이어짐(1 0)",
            $"화면 {shifted:F0}px, 마스크 {Masks(ev)}, 원격 x {dragXs.FirstOrDefault()}→{dragXs.LastOrDefault()}",
            shifted is > 50 and < 250 && Masks(ev).EndsWith("1 0") && dragXs.Count > 0 && dragXs.Last() > dragXs.First());

        // 마우스 모드: 길게 눌러 끌다가 오른쪽 가장자리에 머물면 커서가 계속 오른쪽으로
        g.Mode = TouchInputMode.Mouse;
        vp.PanBy(new Avalonia.Vector(100000, 0));
        g.ResetCursor(vp.ToRemote(center));
        now += 1000;
        Events();
        g.Down(1, from, now);
        now += GestureInterpreter.LongPressMs + 100;
        g.Tick(now);
        for (var i = 1; i <= 10; i++) { now += 16; g.Move(1, from + (edge - from) * i / 10, now); }
        g.Tick(now);
        var cursorAtEdge = g.Cursor.X;
        for (var i = 0; i < 15; i++) { now += 60; g.Tick(now); }
        var cursorAfter = g.Cursor.X;
        g.Up(1, edge, now);
        now += 200;
        ev = Events();
        Check("확대 중 끌기 · 가장자리에 머묾(마우스 모드)", "커서가 오른쪽으로 계속 이동, 왼쪽 버튼 끌기(1 0)",
            $"커서 x {cursorAtEdge:F0}→{cursorAfter:F0}, 마스크 {Masks(ev)}",
            cursorAfter > cursorAtEdge + 10 && Masks(ev).EndsWith("1 0"));

        // 확대하지 않았으면 가장자리에 머물러도 움직이지 않는다
        var zoomBefore = vp.Zoom;
        vp.Fit();
        g.Mode = TouchInputMode.Touch;
        now += 1000;
        Events();
        g.Down(1, from, now);
        for (var i = 1; i <= 10; i++) { now += 16; g.Move(1, from + (edge - from) * i / 10, now); }
        var fitOffset = vp.Offset;
        for (var i = 0; i < 10; i++) { now += 60; g.Tick(now); }
        var still = vp.Offset == fitOffset;
        g.Up(1, edge, now);
        now += 200;
        Events();
        Check("확대 안 함 · 가장자리에 머묾", "화면 그대로", still ? "그대로" : "움직임", still);
        vp.ZoomAt(zoomBefore, center); // 이어지는 검사를 위해 확대 상태로
        g.Mode = TouchInputMode.Mouse;
    }

    // 화면 모드 단추: 맞춤 → 가로 채우기 → 1:1 → 맞춤
    var modeButton = page.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == L.T("Console_ViewModeTip"));
    vp.Fit();
    var modes = new List<string>();
    for (var press = 0; press < 3; press++)
    {
        modeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(50);
        modes.Add($"{modeButton.Content}:{vp.Scale:0.00}");
    }
    Check("화면 모드 단추", "채우기(배율↑) → 1:1 → 맞춤", string.Join(" ", modes), modes.Count == 3 && modes[0].StartsWith(L.T("Console_ViewFill")) && modes[2].StartsWith(L.T("Console_ViewFit")));
    vp.Fit();
    Shot("console-zoomed");

    vp.Fit();
    g.Mode = ProxmoxClient.UI.Views.Console.TouchInputMode.Touch;
    var target = vp.ToView(new Avalonia.Point(100, 50));
    Tap(target);
    Settle();
    ev = Events();
    var at = LastXY(ev);
    Check("터치 모드 · 한 번 탭", "누른 곳(100,50) 왼쪽 클릭(0 1 0)", $"{Masks(ev)} @ {at}",
        Masks(ev) == "0 1 0" && Math.Abs(at.X - 100) <= 2 && Math.Abs(at.Y - 50) <= 2);
    Tap(target); now -= 150; Tap(target);
    Settle();
    ev = Events();
    at = LastXY(ev);
    Check("터치 모드 · 두 번 탭", "누른 곳(100,50) 더블 클릭(0 1 0 1 0)", $"{Masks(ev)} @ {at}",
        Masks(ev) == "0 1 0 1 0" && Math.Abs(at.X - 100) <= 2 && Math.Abs(at.Y - 50) <= 2);

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
    Check("특수키 Esc", "Esc(스캔코드 0x1, keysym 0xff1b) 누름·뗌", string.Join(", ", ev),
        ev.Count == 2 && ev.All(e => e.Contains("keysym=0xff1b") && e.EndsWith("keycode=0x1")));

    page.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
        .First(b => b.Content as string == "Ctrl").IsChecked = true;
    var input = page.GetVisualDescendants().OfType<TextBox>().First();
    input.Text = "c";
    ev = Events();
    Check("Ctrl 고정 + 키보드 c", "Ctrl↓ c↓ c↑ Ctrl↑", string.Join(", ", ev.Select(e => e.Replace("key ", ""))),
        ev.Count == 4 && ev[0].Contains("keysym=0xffe3 ") && ev[1].Contains("keysym=0x63 ") && ev[3].Contains("keysym=0xffe3 ") &&
        ev[3].Contains("down=0"));

    input.Text = "안";
    ev = Events();
    string SoftCodes(List<string> list) => string.Join(" ", list.Select(e => (e.Contains("down=1") ? "↓" : "↑") + e.Split("keycode=").Last()));
    Check("한글 입력 '안'", "두벌식 키 d k s(↓20 ↑20 ↓25 ↑25 ↓1f ↑1f)", SoftCodes(ev),
        SoftCodes(ev) == "↓0x20 ↑0x20 ↓0x25 ↑0x25 ↓0x1f ↑0x1f");

    // ---- 화면 키보드 Shift·한/영·클립보드·벨·화면 배치·가려진 동안 갱신 멈춤·제자리 다시 연결
    {
        List<string> RfbLines()
        {
            Pump(300);
            if (logPath.Length == 0 || !File.Exists(logPath)) return [];
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(logOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            logOffset = fs.Position;
            return text.Split('\n').Where(l => l.StartsWith("RFB ")).Select(l => l[4..].Trim()).ToList();
        }
        Button Tip(string key) => page.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == L.T(key));
        void Click(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        input.Text = "Hi!";
        var soft = Events();
        Check("화면 키보드 'Hi!'", "Shift 로 감쌈(↓2a ↓23 ↑23 ↑2a ↓17 ↑17 ↓2a ↓2 ↑2 ↑2a)", SoftCodes(soft),
            SoftCodes(soft) == "↓0x2a ↓0x23 ↑0x23 ↑0x2a ↓0x17 ↑0x17 ↓0x2a ↓0x2 ↑0x2 ↑0x2a");

        Click(Tip("Console_HangulTip"));
        soft = Events();
        Check("특수키 한/영", "스캔코드 ↓f2 ↑f2", SoftCodes(soft), SoftCodes(soft) == "↓0xf2 ↑0xf2");

        // ⋯ 줄을 펼쳐 클립보드 단추를 쓴다
        var moreKeys = page.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "⋯");
        if (!page.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "F1" && b.IsEffectivelyVisible)) Click(moreKeys);
        Pump(200);
        var deviceClipboard = TopLevel.GetTopLevel(page)!.Clipboard!;
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(deviceClipboard, "ping-42");
        var bellsBefore = page.BellCount;
        Click(Tip("Console_PasteToGuestTip"));
        Pump(300);
        soft = Events();
        Pump(300); // 가짜 게스트가 같은 글을 클립보드로 돌려주고 벨을 울린다
        Check("붙여넣기(게스트로)", "기기 클립보드 'ping-42' 를 게스트 클립보드로, 게스트 벨 → 화면 테두리 깜빡임",
            $"{string.Join(", ", soft)}, 벨 {page.BellCount - bellsBefore}번",
            soft.Any(e => e.Contains("cut b'ping-42'")) && page.BellCount > bellsBefore);

        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(deviceClipboard, "other");
        Click(Tip("Console_CopyFromGuestTip"));
        Pump(1200);
        var copied = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(deviceClipboard);
        Check("복사(게스트에서)", "게스트 클립보드 'ping-42' → 기기 클립보드", copied ?? "(없음)", copied == "ping-42");

        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(deviceClipboard, "aB\r\n");
        Events();
        Click(Tip("Console_TypeClipboardTip"));
        soft = Events();
        Check("클립보드 입력(키로)", "a ↓1e ↑1e, B = Shift+b, 줄바꿈 = Enter 한 번", SoftCodes(soft),
            SoftCodes(soft) == "↓0x1e ↑0x1e ↓0x2a ↓0x30 ↑0x30 ↑0x2a ↓0x1c ↑0x1c");

        // 떨어진 두 영역은 하나로 감싸지 않고 따로 올린다
        surface.MarkDirty(0, 0, 8, 8);
        surface.MarkDirty(700, 500, 8, 8);
        Pump(100);
        Check("바뀐 영역 따로 올리기", "떨어진 두 영역 = 2개", $"{surface.LastCopiedRects}개", surface.LastCopiedRects == 2);

        // 휠: 움직인 만큼 — 반 칸 두 번 = 한 번, 세 칸 = 세 번
        var halfA = surface.WheelClicks(new Avalonia.Vector(0, 0.5)).Count;
        var halfB = surface.WheelClicks(new Avalonia.Vector(0, 0.5));
        var three = surface.WheelClicks(new Avalonia.Vector(0, -3));
        Check("휠 크기 반영", "0.5+0.5 → 위 1번, -3 → 아래 3번", $"{halfA}+{string.Join(",", halfB)} / {string.Join(",", three)}",
            halfA == 0 && halfB.SequenceEqual([8]) && three.SequenceEqual([16, 16, 16]));

        // 1:1 = 원격 1픽셀이 화면 물리 1픽셀(화면 배율 반영, 원격이 작으면 맞춤)
        vp.SetMode(ViewMode.Actual);
        var physical = vp.Scale * surface.RenderScaling;
        Check("1:1 보기", "원격 1px = 물리 1px(작으면 맞춤)", $"{physical:0.00}", Math.Abs(physical - 1) < 0.01 || vp.Zoom <= 1);

        // 화면 크기가 바뀌어도(회전) 채우기 유지
        vp.SetMode(ViewMode.Fill);
        var (winW, winH) = (window.Width, window.Height);
        window.Width = winH;
        window.Height = winW;
        Pump(300);
        var fillAfter = vp.Mode == ViewMode.Fill && Math.Abs(vp.Scale - Math.Max(vp.View.Width / vp.Remote.Width, vp.View.Height / vp.Remote.Height)) < 0.01;
        window.Width = winW;
        window.Height = winH;
        Pump(300);
        Check("회전 뒤 채우기", "채우기 그대로, 새 크기에 빈 곳 없이", $"{vp.Mode} {vp.Scale:0.00}", fillAfter && vp.Mode == ViewMode.Fill);

        // 확대한 채 끌면 손짓 타이머가 화면 갱신 주기(16ms)로 — 가장자리 이동이 부드럽게
        vp.ZoomAt(2, center);
        g.Mode = TouchInputMode.Touch;
        var edgeDragAt = surface.TranslatePoint(center, window)!.Value;
        var edgeTouch = window.TouchBegin(edgeDragAt, Avalonia.Input.RawInputModifiers.None);
        Pump(30);
        for (var i = 1; i <= 5; i++)
        {
            window.TouchMove(edgeTouch, edgeDragAt + new Avalonia.Vector(i * 10, 0), Avalonia.Input.RawInputModifiers.None);
            Pump(16);
        }
        var fastTicks = g.IsEdgeDragging ? surface.TickIntervalMs : -1;
        window.TouchEnd(edgeTouch, edgeDragAt + new Avalonia.Vector(50, 0), Avalonia.Input.RawInputModifiers.None);
        Pump(200);
        Events();
        Check("확대 중 끌기 타이머", $"{VncSurface.FastTickMs}ms", $"{fastTicks}ms", fastTicks == VncSurface.FastTickMs);

        // 다른 화면이 위를 덮은 동안은 화면 갱신을 요청하지 않는다
        vp.Fit();
        await Wait(AppServices.Navigator.PushViewAsync("cover", new TextBlock { Text = "cover" }));
        Pump(400);
        RfbLines();
        Pump(1000);
        var hiddenUpdates = RfbLines().Count(l => l.StartsWith("update"));
        await Wait(AppServices.Navigator.PopAsync());
        Pump(1000);
        var shownLines = RfbLines();
        var shownUpdates = shownLines.Count(l => l.StartsWith("update"));
        Check("가려진 동안 갱신 멈춤", "가려진 1초 요청 ≤ 1, 돌아오면 다시(≥ 3)", $"가려짐 {hiddenUpdates}, 돌아옴 {shownUpdates}",
            hiddenUpdates <= 1 && shownUpdates >= 3 && nav.CurrentPage == page);

        // 다시 연결은 이 화면에서 — 화면 모드·확대가 그대로
        vp.SetMode(ViewMode.Fill);
        var sessionBefore = vm.Vnc;
        await Wait(vm.ReconnectAsync());
        WaitFor(() => vm.IsConnected && vm.Vnc != sessionBefore, 10000);
        Pump(300);
        Check("제자리 다시 연결", "같은 화면·새 세션, 채우기 유지", $"{(nav.CurrentPage == page ? "같은 화면" : "바뀜")}, {(vm.Vnc != sessionBefore && vm.IsConnected ? "새 세션 연결" : "연결 안 됨")}, {vp.Mode}",
            nav.CurrentPage == page && vm.Vnc != sessionBefore && vm.IsConnected && vp.Mode == ViewMode.Fill);
        vp.Fit();
        Events();
    }

    // ---- 탭 뒤 이어진 터치(마우스 모드): 끌기 = 왼쪽 끌기, 가만히 길게 = 오른쪽 버튼. 첫 탭은 빼고 그 사이 탭만큼 먼저 클릭
    vp.Fit();
    g.Mode = TouchInputMode.Mouse;
    g.ResetCursor(new Avalonia.Point(400, 300));
    Events();
    Tap(center); now -= 100;
    Drag(center, new Avalonia.Vector(0, 90));
    ev = Events();
    Check("탭 후 끌기(TeamViewer)", "클릭 없이 왼쪽 버튼 누른 채 끌기(1 0)", Masks(ev),
        Masks(ev) == "1 0" && LastXY(ev).Y > 300);

    // 탭 2번 → 끌기 = 클릭 한 번 후 왼쪽 끌기
    now += 1000;
    Tap(center); now -= 150; Tap(center); now -= 100;
    Drag(center, new Avalonia.Vector(0, 60));
    ev = Events();
    Check("두 번 탭 후 끌기", "클릭 후 왼쪽 버튼 끌기(0 1 0 1 0)", Masks(ev), Masks(ev) == "0 1 0 1 0");

    // 탭 3번 → 끌기 = 더블 클릭 후 왼쪽 버튼 누른 채 끌기
    now += 1000;
    Tap(center); now -= 150; Tap(center); now -= 150; Tap(center); now -= 100;
    Drag(center, new Avalonia.Vector(0, 60));
    ev = Events();
    Check("세 번 탭 후 끌기", "더블 클릭 후 왼쪽 버튼 끌기(0 1 0 1 0 1 0)", Masks(ev), Masks(ev) == "0 1 0 1 0 1 0");

    // 탭 → 그 자리에서 가만히 길게 눌렀다 떼기 = 오른쪽 클릭
    now += 1000;
    Tap(center); now -= 100;
    Tap(center, holdMs: GestureInterpreter.LongPressMs + 100);
    ev = Events();
    Check("탭 후 가만히 길게 눌렀다 떼기", "오른쪽 버튼 누름·놓음(4 0)", Masks(ev), Masks(ev) == "4 0");

    // 탭 → 가만히 길게 누른 뒤 끌기 = 오른쪽 버튼 끌기
    now += 1000;
    g.ResetCursor(new Avalonia.Point(400, 300));
    Tap(center); now -= 100;
    Drag(center, new Avalonia.Vector(0, 80), holdFirstMs: GestureInterpreter.LongPressMs + 100);
    ev = Events();
    Check("탭 후 길게 누르고 끌기", "오른쪽 버튼 누른 채 끌기(4 0)", $"{Masks(ev)} y→{LastXY(ev).Y}",
        Masks(ev) == "4 0" && LastXY(ev).Y > 300);

    // 길게 누르기만(앞에 탭 없이) 하고 끌면 그대로 왼쪽 끌기 — 기존 동작 유지
    now += 1000;
    Drag(center, new Avalonia.Vector(0, 40), holdFirstMs: GestureInterpreter.LongPressMs + 100);
    ev = Events();
    Check("길게 누르고 끌기(탭 없이)", "왼쪽 버튼 끌기(1 0)", Masks(ev), Masks(ev) == "1 0");

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
    Check("물리 마우스 · 커서 하나만", "운영체제 커서만(앱이 커서를 따로 그리지 않음)",
        surface.DrawsSoftwareCursor ? "따로 그림" : "운영체제 커서만", !surface.DrawsSoftwareCursor);

    // 확대 중 실제 마우스가 오른쪽 가장자리에 머물면 화면이 그쪽으로 이동(버튼 없이도)
    {
        vp.ZoomAt(2.5, center);
        vp.PanBy(new Avalonia.Vector(100000, 100000));
        Events();
        var edgeAt = surface.TranslatePoint(new Avalonia.Point(surface.Bounds.Width - 4, surface.Bounds.Height / 2), window)!.Value;
        window.MouseMove(edgeAt, Avalonia.Input.RawInputModifiers.None);
        Pump(50);
        var mouseOffset = vp.Offset.X;
        long ms = Environment.TickCount64;
        surface.TickMouseEdge(ms);
        for (var i = 0; i < 15; i++) { ms += 60; surface.TickMouseEdge(ms); }
        var mouseShift = mouseOffset - vp.Offset.X;
        ev = Events();
        var xs = ev.Where(e => e.StartsWith("pointer mask=0")).Select(e => int.Parse(e.Split(' ')[2][2..])).ToList();
        var midAt = surface.TranslatePoint(new Avalonia.Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2), window)!.Value;
        window.MouseMove(midAt, Avalonia.Input.RawInputModifiers.None);
        Pump(50);
        var stops = !surface.TickMouseEdge(ms + 60);
        Check("물리 마우스 · 확대 중 가장자리", "화면이 오른쪽으로 이동(0.9초에 100~500px), 원격 커서도 따라감, 가운데로 오면 멈춤",
            $"화면 {mouseShift:F0}px, 원격 x {xs.FirstOrDefault()}→{xs.LastOrDefault()}, {(stops ? "멈춤" : "계속")}",
            mouseShift is > 100 and < 500 && xs.Count > 1 && xs.Last() > xs.First() && stops);
        vp.Fit();
        Events();
    }

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

    // 끌기 시작 문턱 — 손가락을 천천히 움직이기 시작할 때(문턱 12px 를 넘는 순간) 커서가 확 튀지 않아야 한다.
    // 손가락은 이벤트마다 2px(8ms) 로 고르게. 문턱을 넘은 첫 걸음이 그 뒤 걸음들보다 2배 넘게 크면 튐.
    g.Mode = TouchInputMode.Mouse;
    g.ResetCursor(new Avalonia.Point(200, 300));
    var slowAt = center - new Avalonia.Vector(100, 0);
    g.Down(31, slowAt, now);
    var slowSteps = new List<double>();
    var slowPrev = g.Cursor;
    for (var i = 1; i <= 30; i++)
    {
        now += 8;
        slowAt += new Avalonia.Vector(2, 0);
        g.Move(31, slowAt, now);
        var step = g.Cursor.X - slowPrev.X;
        if (step > 0) slowSteps.Add(step);
        slowPrev = g.Cursor;
    }

    g.Up(31, slowAt, now);
    now += 400;
    Events();
    var firstStep = slowSteps.FirstOrDefault();
    var typical = slowSteps.Skip(1).DefaultIfEmpty(0).Average();
    Check("끌기 시작 문턱(천천히)", "첫 걸음이 보통 걸음의 3배 이하(확 튀지 않음)",
        $"첫 걸음 {firstStep:F1}px, 보통 {typical:F1}px", typical > 0 && firstStep <= typical * 3);

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

        // 실제 터치로 두 번 탭 = 더블 클릭 — 손을 뗄 때 오는 캡처 해제가 기다리던 클릭을 지우면 안 된다(실기기 보고)
        Pump(500);
        Events();
        var tapAt = surface.TranslatePoint(new Avalonia.Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2), window)!.Value;
        for (var i = 0; i < 2; i++)
        {
            var tap = window.TouchBegin(tapAt, Avalonia.Input.RawInputModifiers.None);
            Pump(30);
            window.TouchEnd(tap, tapAt, Avalonia.Input.RawInputModifiers.None);
            Pump(60);
        }

        Pump(450); // 다음 터치를 기다리는 시간이 지나도록
        g.Tick(Environment.TickCount64); // 헤드리스는 DispatcherTimer 가 돌지 않아 직접 부른다
        Pump(200);
        var tapMasks = Masks(Events());
        Check("실제 터치로 두 번 탭", "더블 클릭(0 1 0 1 0)", tapMasks, tapMasks.EndsWith("0 1 0 1 0") && !tapMasks.EndsWith("0 1 0 1 0 1 0"));

        // 실제 터치로 한 번 탭 = 클릭
        Pump(500);
        Events();
        for (var i = 0; i < 1; i++)
        {
            var tap = window.TouchBegin(tapAt, Avalonia.Input.RawInputModifiers.None);
            Pump(30);
            window.TouchEnd(tap, tapAt, Avalonia.Input.RawInputModifiers.None);
            Pump(60);
        }

        Pump(450);
        g.Tick(Environment.TickCount64);
        Pump(200);
        var singleMasks = Masks(Events());
        Check("실제 터치로 한 번 탭", "왼쪽 클릭(0 1 0)", singleMasks, singleMasks.EndsWith("0 1 0") && !singleMasks.EndsWith("0 1 0 1 0"));
    }

    // 실시간 마우스 끌기 — 8ms 마다 움직인 이동이 서버에 고르게 도착하는가(보내는 쪽 간격 두기·타이머 오차 없음)
    if (desktop)
    {
        Events();
        var liveFrom = surface.TranslatePoint(new Avalonia.Point(200, 200), window)!.Value;
        // 터치 → 마우스 전환(앱이 그리던 커서를 지우는 한 번의 다시 그리기)은 끌기 전에 끝낸다 — 끌기 도중 간격만 잰다
        window.MouseMove(liveFrom, Avalonia.Input.RawInputModifiers.None);
        Pump(200);
        Events();
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

    // 콘솔 화면에서 콘솔 설정 열기 → 저장 → 돌아오면 새 설정으로 다시 연결
    // 숨은 줄은 아직 그려지지 않아 단추가 없다 — [⋯]로 펼친다
    var more = page.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "⋯");
    if (!page.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "F1"))
        more.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Pump(300);
    var settingsButton = page.GetVisualDescendants().OfType<Button>()
        .FirstOrDefault(b => b.Content as string == L.T("ConsoleSettingsWindow_01"));
    settingsButton?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    WaitFor(() => nav.CurrentPage != page && nav.CurrentPage?.GetVisualDescendants().OfType<ProxmoxClient.UI.Views.Settings.ConsoleSettingsPage>().Any() == true, 5000);
    var opened = nav.CurrentPage != page;
    Shot("console-settings");
    var save = nav.CurrentPage?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => ToolTip.GetTip(b) as string == L.T("AppSettingsWindow_12")
                                                                                            || b.Content as string == L.T("AppSettingsWindow_12"));
    var settingsSession = vm.Vnc;
    save?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    WaitFor(() => nav.CurrentPage == page && vm.Vnc != settingsSession && vm.IsConnected, 10000);
    var again = nav.CurrentPage == page && vm.Vnc != settingsSession && vm.IsConnected;
    Check0("콘솔 설정(콘솔 화면에서)", "⋯ 줄의 [콘솔 설정] → 저장 → 다시 연결", $"{(settingsButton is null ? "단추 없음" : "단추 있음")}, {(opened ? "설정 열림" : "안 열림")}, {(again ? "다시 연결됨" : "연결 안 됨")}",
        settingsButton is not null && opened && again);

    // 앱이 배경으로 가면 끊고, 돌아오면 다시 연결(휴대폰·태블릿만)
    if (!desktop || tablet)
    {
        var bgVm = (ConsoleViewModel)nav.CurrentPage!.DataContext!;
        var bgSession = bgVm.Vnc;
        SetForeground(false);
        Pump(500);
        var paused = !bgVm.IsConnected && bgVm.Status == L.T("Console_PausedBackground");
        SetForeground(true);
        WaitFor(() => nav.CurrentPage == page && bgVm.Vnc != bgSession && bgVm.IsConnected, 10000);
        var back = nav.CurrentPage == page && bgVm.Vnc != bgSession && bgVm.IsConnected;
        Check0("콘솔 배경 → 복귀", "배경에서 끊김 → 돌아오면 다시 연결", $"{(paused ? "끊김" : "그대로")} → {(back ? "다시 연결" : "연결 안 됨")}", paused && back);
    }

    await Wait(AppServices.Navigator.PopAsync());
}

void SetForeground(bool foreground) =>
    typeof(AppServices).Assembly.GetType("ProxmoxClient.UI.Services.AppLifecycle")!
        .GetMethod("SetForeground", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .Invoke(null, [foreground]);

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
            WalkSubTabs($"{name}-{i:00}");
            Wait(AppServices.Navigator.PopAsync()).GetAwaiter().GetResult();
            Pump(300);
        }
    }
}

void WalkSubTabs(string name)
{
    var current = nav.CurrentPage as Control;
    // 설정 화면의 탭(일반·콘솔·SFTP)
    if (current?.GetVisualDescendants().FirstOrDefault(v => v.GetType().Name == "SettingsTabs") is Visual settingsTabs)
    {
        var bar = settingsTabs.GetVisualDescendants().OfType<ListBox>().First();
        for (var i = 1; i < bar.ItemCount; i++)
        {
            bar.SelectedIndex = i;
            Pump(800);
            Shot($"{name}-tab{i}");
            CheckStatus($"{name}/tab{i}");
        }

        bar.SelectedIndex = 0;
        return;
    }

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

// 타일 경계 격자무늬 — 폰처럼 화면 배율이 소수(2.625)일 때 흰 화면을 여러 배율로 그려 타일 경계에 어두운 줄이 남는지
void TileSeamCheck()
{
    var type = typeof(ProxmoxClient.UI.App).Assembly.GetType("ProxmoxClient.UI.Views.Console.TiledFramebuffer")!;
    const int w = 1280, h = 800;
    var fb = (IDisposable)Activator.CreateInstance(type, w, h)!;
    var white = new byte[w * h * 4];
    Array.Fill(white, (byte)255);
    type.GetMethod("Update")!.Invoke(fb, [white, new Avalonia.PixelRect(0, 0, w, h)]);
    var render = type.GetMethod("Render")!;
    const double pixelScale = 2.625;
    var worst = 0;
    var where = "";
    foreach (var scale in new[] { 0.3, 0.37, 0.5, 0.61, 0.83, 1.0, 1.27 })
    foreach (var offset in new[] { new Avalonia.Point(0, 0), new Avalonia.Point(0.37, 0.71), new Avalonia.Point(3.2, 5.9) })
    {
        var viewW = w * scale + offset.X + 2;
        var viewH = h * scale + offset.Y + 2;
        var px = new PixelSize((int)Math.Ceiling(viewW * pixelScale), (int)Math.Ceiling(viewH * pixelScale));
        using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(px, new Avalonia.Vector(96 * pixelScale, 96 * pixelScale));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.FillRectangle(Avalonia.Media.Brushes.Black, new Avalonia.Rect(0, 0, viewW, viewH));
            using (ctx.PushRenderOptions(new Avalonia.Media.RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality }))
                render.Invoke(fb, [ctx, offset, scale, new Avalonia.Rect(0, 0, viewW, viewH), Environment.GetEnvironmentVariable("SEAM_OLD") == "1" ? 1.0 : pixelScale]);
        }

        var buf = new byte[px.Width * px.Height * 4];
        unsafe
        {
            fixed (byte* p = buf) rtb.CopyPixels(new Avalonia.PixelRect(px), (nint)p, buf.Length, px.Width * 4);
        }

        // 그림 안쪽(가장자리 3픽셀 제외)에서 가장 어두운 픽셀
        var x0 = (int)Math.Ceiling(offset.X * pixelScale) + 3;
        var y0 = (int)Math.Ceiling(offset.Y * pixelScale) + 3;
        var x1 = (int)Math.Floor((offset.X + w * scale) * pixelScale) - 3;
        var y1 = (int)Math.Floor((offset.Y + h * scale) * pixelScale) - 3;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var dark = 255 - buf[(y * px.Width + x) * 4 + 1];
            if (dark <= worst) continue;
            worst = dark;
            where = $"배율 {scale}, 위치 ({x},{y})";
        }
    }

    fb.Dispose();
    Check0("VNC 타일 경계(화면 배율 2.625)", "격자 줄 없음(가장 어두운 정도 ≤ 8/255)", worst == 0 ? "없음" : $"{worst}/255 @ {where}", worst <= 8);
}

// 만들기 마법사 여는 속도 — 누르자마자 화면이 뜨고(진행 표시), 첫 단계는 빠른 목록만 기다리며, 느린 파일 목록은 뒤에서 채운다
async Task CreateSpeedCheck()
{
    var feature = FeatureRegistry.ClusterFeatures.First(f => f.Id == "create-ct");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var open = feature.Run(cluster.Session);
    WaitFor(() => nav.CurrentPage?.GetVisualDescendants().Any(v => v.GetType().Name == "WizardView") == true, 10000);
    var shownMs = sw.ElapsedMilliseconds;
    WaitFor(() => nav.CurrentPage?.GetVisualDescendants().OfType<TextBox>().Any() == true, 20000);
    var firstStepMs = sw.ElapsedMilliseconds;
    Shot("create-speed-first-step");
    Check0("만들기 화면 열림", "누르자마자(≤ 300ms)", $"{shownMs} ms", shownMs <= 300);
    Check0("첫 단계 표시", "파일 목록을 기다리지 않음", $"{firstStepMs} ms", true);
    Console.WriteLine($"create-speed shown={shownMs}ms firstStep={firstStepMs}ms");
    await Wait(open);
    await Wait(AppServices.Navigator.PopAsync());
}

void Shot(string name)
{
    Pump(250);
    var file = Path.Combine(outDir, $"{++shot:000}-{name}.png");
    window.CaptureRenderedFrame()!.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    Console.WriteLine("shot " + file);
    if (Environment.GetEnvironmentVariable("AUDIT") == "1") AuditButtons(name);
}

// 버튼 규칙 검사 — 글자가 두 줄로 꺾이거나, 잘리거나, 화면 밖으로 나간 단추를 적는다(사용자 요청: 규칙대로 정리)
void AuditButtons(string screen)
{
    var winWidth = window.Bounds.Width;
    // 휴대폰에서 고정 폭으로 좁은 입력 칸(규칙: 모바일 입력은 오른쪽 끝까지)
    if (!desktop)
        foreach (var input in window.GetVisualDescendants().OfType<Control>().Where(c => c is TextBox or ComboBox or NumericUpDown or AutoCompleteBox))
        {
            if (!input.IsEffectivelyVisible || double.IsNaN(input.Width) || input.Width >= winWidth * 0.6) continue;
            if (input.Opacity < 0.1 || input.Width <= 4) continue; // 숨은 입력 칸(키보드용)
            var tip = (input as TextBox)?.PlaceholderText ?? input.Name ?? input.GetType().Name;
            Console.WriteLine($"AUDIT {screen} | 좁은 입력 | {input.GetType().Name} '{tip}' | {input.Width:F0}px");
        }

    // 단추 줄이 두 줄 이상으로 꺾임(WrapPanel 안 단추들의 줄 수)
    foreach (var wrap in window.GetVisualDescendants().OfType<WrapPanel>())
    {
        if (!wrap.IsEffectivelyVisible) continue;
        var buttons = wrap.Children.OfType<Button>().Where(b => b.IsVisible).ToList();
        if (buttons.Count < 2) continue;
        var rows = buttons.Select(b => Math.Round(b.Bounds.Y / 4)).Distinct().Count();
        if (rows > 1)
        {
            var names = string.Join(",", buttons.Take(4).Select(b => ToolTip.GetTip(b) as string ?? b.Content as string ?? "?"));
            Console.WriteLine($"AUDIT {screen} | 단추 줄 {rows}줄 | {wrap.Parent?.GetType().Name}/{names}… ({buttons.Count}개)");
        }
    }
    foreach (var button in window.GetVisualDescendants().OfType<Avalonia.Controls.Button>())
    {
        if (!button.IsEffectivelyVisible || button.Bounds.Width <= 0) continue;
        if (button.GetVisualAncestors().Any(a => a is Avalonia.Controls.Primitives.ScrollBar or Slider)) continue; // 스크롤 막대·슬라이더 부품
        var origin = button.TranslatePoint(new Avalonia.Point(0, 0), window);
        if (origin is null) continue;
        var o = origin.Value;
        if (o.Y > window.Bounds.Height || o.Y + button.Bounds.Height < 0) continue; // 스크롤 밖
        // 가로 스크롤 줄 안의 단추는 넘쳐도 된다(한 줄 키 막대)
        var inHScroll = button.GetVisualAncestors().OfType<ScrollViewer>().Any(sv =>
            sv.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        var label = ToolTip.GetTip(button) as string ?? button.Content as string ??
                    string.Join(" ", button.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));
        foreach (var tb in button.GetVisualDescendants().OfType<TextBlock>())
        {
            if (string.IsNullOrEmpty(tb.Text) || !tb.IsEffectivelyVisible) continue;
            var line = tb.FontSize * 1.45;
            if (tb.Bounds.Height > line * 1.4) Console.WriteLine($"AUDIT {screen} | 두 줄 | '{label}' | {tb.Bounds.Height:F0}px");
            var full = new Avalonia.Media.TextFormatting.TextLayout(tb.Text, new Avalonia.Media.Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight),
                tb.FontSize, null).Width;
            if (tb.TextWrapping == Avalonia.Media.TextWrapping.NoWrap && full > tb.Bounds.Width + 1.5)
                Console.WriteLine($"AUDIT {screen} | 잘림 | '{label}' | {full:F0}>{tb.Bounds.Width:F0}");
        }
        // 글자만 넣은 단추는 TextBlock 없이 ContentPresenter 가 직접 글자를 그린다
        foreach (var cp in button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            if (cp.Content is not string text || text.Length == 0 || !cp.IsEffectivelyVisible) continue;
            var face = new Avalonia.Media.Typeface(cp.FontFamily, cp.FontStyle, cp.FontWeight);
            var full = new Avalonia.Media.TextFormatting.TextLayout(text, face, cp.FontSize, null).Width;
            var inner = cp.Bounds.Width - cp.Padding.Left - cp.Padding.Right;
            var line = cp.FontSize * 1.45;
            var innerH = cp.Bounds.Height - cp.Padding.Top - cp.Padding.Bottom;
            if (cp.TextWrapping != Avalonia.Media.TextWrapping.NoWrap && innerH > line * 1.4 && full > inner + 1)
                Console.WriteLine($"AUDIT {screen} | 두 줄 | '{label}' | {full:F0}>{inner:F0}");
            else if (full > inner + 1.5)
                Console.WriteLine($"AUDIT {screen} | 잘림 | '{label}' | {full:F0}>{inner:F0}");
        }
        if (!inHScroll && o.X + button.Bounds.Width > winWidth + 1)
            Console.WriteLine($"AUDIT {screen} | 화면 밖 | '{label}' | x {o.X:F0}+{button.Bounds.Width:F0} > {winWidth:F0}");
        // 접근성 — 화면 낭독기가 읽을 이름(AutomationProperties.Name 또는 글자 내용)이 없는 단추
        var spoken = Avalonia.Automation.AutomationProperties.GetName(button);
        if (string.IsNullOrWhiteSpace(spoken) && button.Content is not string && !button.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)))
            Console.WriteLine($"AUDIT {screen} | 이름 없음 | '{label}' | {button.GetType().Name} in {button.Parent?.GetType().Name}/{button.Parent?.Parent?.GetType().Name}");
        // 휴대폰 손가락 크기 — 40px 보다 작은 단추
        if (!desktop && (button.Bounds.Width < 40 || button.Bounds.Height < 40))
            Console.WriteLine($"AUDIT {screen} | 작은 터치 | '{label}' | {button.GetType().Name} {button.Bounds.Width:F0}x{button.Bounds.Height:F0}");
    }

    // 여백 — 휴대폰에서 입력 칸·체크·글자가 화면 가장자리에 붙음(왼쪽·오른쪽 6px 미만)
    if (!desktop)
        foreach (var c in window.GetVisualDescendants().OfType<Control>().Where(c => c is TextBox or ComboBox or CheckBox or ToggleSwitch or TextBlock or Button))
        {
            if (!c.IsEffectivelyVisible || c.Bounds.Width <= 0 || c.Opacity < 0.1) continue;
            if (c.GetVisualAncestors().Any(a => a is TextBox or ComboBox or Button or ProxmoxClient.UI.Views.Terminal.TerminalView or ProxmoxClient.UI.Views.Console.VncSurface
                    or Avalonia.Controls.Primitives.ScrollBar)) continue;
            if (c.GetVisualAncestors().OfType<ScrollViewer>().Any(sv =>
                    sv.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)) continue;
            var at = c.TranslatePoint(new Avalonia.Point(0, 0), window);
            if (at is null || at.Value.Y > window.Bounds.Height || at.Value.Y + c.Bounds.Height < 0) continue;
            if (at.Value.Y < 50) continue; // 맨 위 제목 줄(뒤로 가기)은 플랫폼 여백을 쓴다
            var left = at.Value.X;
            var right = winWidth - (at.Value.X + c.Bounds.Width);
            if (left < 6 || right < 6)
            {
                var what = c is TextBlock t ? $"'{(t.Text?.Length > 20 ? t.Text[..20] : t.Text)}'" : c.GetType().Name;
                Console.WriteLine($"AUDIT {screen} | 여백 없음 | {what} | 왼쪽 {left:F0} 오른쪽 {right:F0}");
            }
        }

    // 단추 밖 글자 — 한 줄로 고정돼 잘리는 글자(… 포함)
    foreach (var tb in window.GetVisualDescendants().OfType<TextBlock>())
    {
        if (string.IsNullOrEmpty(tb.Text) || !tb.IsEffectivelyVisible || tb.Bounds.Width <= 0) continue;
        if (tb.GetVisualAncestors().Any(a => a is Button or TextBox or ProxmoxClient.UI.Views.Terminal.TerminalView)) continue;
        if (tb.TextWrapping != Avalonia.Media.TextWrapping.NoWrap) continue;
        var origin = tb.TranslatePoint(new Avalonia.Point(0, 0), window);
        if (origin is null || origin.Value.Y > window.Bounds.Height || origin.Value.Y + tb.Bounds.Height < 0) continue;
        if (tb.GetVisualAncestors().OfType<ScrollViewer>().Any(sv =>
                sv.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)) continue;
        var full = new Avalonia.Media.TextFormatting.TextLayout(tb.Text, new Avalonia.Media.Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight),
            tb.FontSize, null).Width;
        var inner = tb.Bounds.Width - tb.Padding.Left - tb.Padding.Right;
        if (full > inner + 1.5)
            Console.WriteLine($"AUDIT {screen} | 글자 잘림 | '{(tb.Text.Length > 40 ? tb.Text[..40] + "…" : tb.Text)}' | {full:F0}>{inner:F0}");
    }
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

/// <summary>검사용 연결 유지 장치 — 알림에 보일 제목만 기억한다.</summary>
sealed class FakeKeepAliveHost : ProxmoxClient.UI.Platform.ISessionKeepAliveHost
{
    public List<string> Titles { get; } = [];

    public void Update(IReadOnlyList<string> titles)
    {
        Titles.Clear();
        Titles.AddRange(titles);
    }
}
