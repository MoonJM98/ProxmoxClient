// Windows RDP 끝까지 검사 — 사용법(러너에서):
//   WinRdpE2E --python python --fakepve tools/FakePve/fake_pve.py --port 19750 --rdp 127.0.0.2:3389,10.1.0.5:3389
//             --user rdptest --pass ... --out shots
// 가짜 PVE 를 띄우고(로그인·rdpproxy·RDCleanPath) 이 PC 의 원격 데스크톱(NLA)에 ProxmoxRdpSession 으로 붙는다.
// 확인: CredSSP(NLA) 연결, 화면, 키보드(세션 안 PowerShell 이 사용자 폴더에 쓴 파일), 해상도 변경, 클립보드 양방향.
// 줄 머리: "CHECK <이름> PASS|FAIL <내용>", 끝에 "DONE ok|fail".

using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Core.Rdp;

var options = new Dictionary<string, string>();
for (var i = 0; i + 1 < args.Length; i += 2) options[args[i].TrimStart('-')] = args[i + 1];

var exitCode = 1;
var ui = new Thread(() =>
{
    // 클립보드 백엔드는 이 스레드(메시지 루프)에서 만들어지고 돈다 — WPF 앱의 UI 스레드와 같은 조건
    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
    var context = new ApplicationContext();
    SynchronizationContext.Current!.Post(async _ =>
    {
        try
        {
            exitCode = await new E2E(options).RunAsync() ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL {ex}");
        }
        finally
        {
            context.ExitThread();
        }
    }, null);
    Application.Run(context);
});
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
// 전체 제한 시간 — 무엇이 멈춰도 작업이 끝없이 돌지 않게
if (!ui.Join(TimeSpan.FromMinutes(int.Parse(options.GetValueOrDefault("timeout-min", "12")))))
{
    Console.WriteLine("DONE fail (timeout)");
    Environment.Exit(2);
}

Environment.Exit(exitCode);

internal sealed class E2E(Dictionary<string, string> options)
{
    private readonly string _out = Path.GetFullPath(options.GetValueOrDefault("out", "shots"));
    private readonly string _nonce = Guid.NewGuid().ToString("N")[..8];
    private readonly StringBuilder _serverLog = new();
    private readonly List<string> _summary = [];
    private int _failures;
    private int _frames;
    private Process? _fake;
    private ProxmoxRdpSession? _session;
    private int _rdpSessionId = -1;
    private string _profileDir = "";

    private static void Log(string text)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {text}");
    }

    private void Check(string name, bool ok, string detail)
    {
        var line = $"CHECK {name} {(ok ? "PASS" : "FAIL")} {detail}";
        Console.WriteLine(line);
        _summary.Add(line);
        if (!ok) _failures++;
    }

    public async Task<bool> RunAsync()
    {
        Directory.CreateDirectory(_out);
        try
        {
            await RunCoreAsync();
        }
        catch (Exception ex)
        {
            Check("harness", false, ex.ToString().ReplaceLineEndings(" | "));
        }
        finally
        {
            try
            {
                if (_session is { } s) await s.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                Log($"disconnect: {ex.Message}");
            }

            try
            {
                _fake?.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            await File.WriteAllTextAsync(Path.Combine(_out, "fakepve.log"), ServerLog());
        }

        Console.WriteLine("==== 요약 ====");
        foreach (var line in _summary) Console.WriteLine(line);
        await File.WriteAllLinesAsync(Path.Combine(_out, "summary.txt"), _summary);
        Console.WriteLine(_failures == 0 ? "DONE ok" : $"DONE fail ({_failures})");
        return _failures == 0;
    }

    private string ServerLog()
    {
        lock (_serverLog) return _serverLog.ToString();
    }

    private async Task RunCoreAsync()
    {
        var port = int.Parse(options.GetValueOrDefault("port", "19750"));
        var user = options.GetValueOrDefault("user", "rdptest");
        var password = options["pass"];
        _profileDir = options.GetValueOrDefault("profile", $@"C:\Users\{user}");

        StartFakePve(port);
        var api = await LoginAsync(port);

        // RDP 대상 고르기 — 루프백(127.0.0.1)은 Windows 가 막을 수 있어 여러 주소를 차례로 시도한다
        var targets = options.GetValueOrDefault("rdp", "127.0.0.2:3389").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var sessionsBefore = Process.GetProcesses().Select(p => p.SessionId).ToHashSet();

        // 연결 전에 PC 클립보드에 글을 둔다 — 연결하면 형식 목록이 서버로 간다
        var localBefore = $"local-before-{_nonce}";
        Clipboard.SetText(localBefore);

        ProxmoxRdpSession? session = null;
        foreach (var target in targets)
        {
            Log($"try RDP target {target}");
            await ConfigureAsync(port, target, user, password);
            var candidate = NewSession(api);
            var logFrom = ServerLog().Length;
            try
            {
                await candidate.ConnectAsync("pve1", 101);
                session = candidate;
                Check("connect", true, $"target={target} size={candidate.Width}x{candidate.Height} clipboard={candidate.ClipboardActive}");
                break;
            }
            catch (Exception ex)
            {
                Log($"connect {target} failed: {ex.GetType().Name}: {ex.Message}");
                Log("server log: " + ServerLog()[logFrom..].ReplaceLineEndings(" | "));
                candidate.Dispose();
            }
        }

        if (session is null)
        {
            Check("connect", false, "모든 대상 실패: " + string.Join(",", targets));
            return;
        }

        _session = session;
        // NLA: 서버가 고른 보안 프로토콜(X.224 연결 확인의 selectedProtocol) — 1=TLS, 2=CredSSP, 8=CredSSP+EarlyUserAuth
        var confirm = ServerLog().Split('\n').LastOrDefault(l => l.StartsWith("RDP x224 confirm "))?.Trim();
        var selected = confirm is null ? -1 : SelectedProtocol(confirm["RDP x224 confirm ".Length..]);
        Check("nla", selected is 2 or 8, $"selectedProtocol={selected} ({confirm})");

        // 세션 안 로그온이 끝날 때까지(탐색기)
        var explorer = await WaitAsync(() => NewSessionProcess("explorer", sessionsBefore), 90_000);
        Log($"explorer in session {_rdpSessionId}: {explorer}");
        await Task.Delay(8000); // 첫 로그온 — 바탕 화면이 다 뜰 때까지
        var colors = Colors(session.Framebuffer);
        Shot("01-connected");
        Check("frames", _frames > 0 && colors > 8, $"frames={_frames} colors={colors} explorer={explorer} session={_rdpSessionId}");

        // 키보드: Win+R → powershell
        await KeyAsync(0xE05B, true);
        await KeyAsync(0x13, true);
        await KeyAsync(0x13, false);
        await KeyAsync(0xE05B, false);
        await Task.Delay(2500);
        Shot("02-run-dialog");
        await TypeScancodesAsync("powershell -NoLogo -NoProfile\n");
        var ps = await WaitAsync(() => NewSessionProcess("powershell", sessionsBefore), 30_000);
        await Task.Delay(5000);
        Shot("03-powershell");
        Log($"powershell in session: {ps}");

        // 물리 키보드 경로(스캔코드, Shift 포함)
        var typedFile = Path.Combine(_profileDir, "e2e-typed.txt");
        await TypeScancodesAsync($"Set-Content -Encoding UTF8 $env:USERPROFILE\\e2e-typed.txt 'Ab1-c_{_nonce}'\n");
        var typed = await WaitFileAsync(typedFile, 20_000);
        Check("keyboard-scancode", typed == $"Ab1-c_{_nonce}", $"{typedFile} = {typed ?? "(없음)"}");

        // 화면 키보드·IME 경로(keysym → 유니코드 키) — 글자만 유니코드로, 앞뒤 명령은 스캔코드
        var uniFile = Path.Combine(_profileDir, "e2e-unicode.txt");
        var uniText = "Uni-Xy9 한글";
        await TypeScancodesAsync("Set-Content -Encoding UTF8 $env:USERPROFILE\\e2e-unicode.txt '");
        await TypeKeysymAsync(uniText);
        await TypeScancodesAsync("'\n");
        var uni = await WaitFileAsync(uniFile, 20_000);
        Check("keyboard-unicode", uni == uniText, $"{uniFile} = {uni ?? "(없음)"}");
        Shot("04-typed");

        // 클립보드 PC → 세션: 연결 전에 둔 글
        var clip1 = await RemoteGetClipboardAsync("e2e-clip1.txt");
        Check("clipboard-local-to-remote-before-connect", clip1 == localBefore, $"remote Get-Clipboard = {clip1 ?? "(없음)"}");

        // 클립보드 PC → 세션: 연결 뒤 바꾼 글
        var localAfter = $"local-after-{_nonce}";
        Clipboard.SetText(localAfter);
        await Task.Delay(1500);
        var clip2 = await RemoteGetClipboardAsync("e2e-clip2.txt");
        Check("clipboard-local-to-remote-after-connect", clip2 == localAfter, $"remote Get-Clipboard = {clip2 ?? "(없음)"}");

        // 클립보드 세션 → PC: 다른 프로그램(PowerShell 프로세스)에서 붙여 넣기
        var remote1 = $"remote-{_nonce}";
        await TypeScancodesAsync($"Set-Clipboard -Value '{remote1}'\n");
        var got1 = await WaitAsync(async () => await LocalClipboardOtherProcessAsync() is var t && t == remote1 ? t : null, 20_000);
        Check("clipboard-remote-to-local", got1 == remote1, $"local Get-Clipboard(다른 프로세스) = {got1 ?? "(없음/다름)"}");

        // 참고: 같은 스레드(백엔드 창이 있는 UI 스레드)에서 읽기 — 지연 렌더링을 자기 스레드에서 기다린다
        var remote2 = $"remote2-{_nonce}";
        await TypeScancodesAsync($"Set-Clipboard -Value '{remote2}'\n");
        await Task.Delay(3000);
        var sw = Stopwatch.StartNew();
        string sameThread;
        try
        {
            sameThread = Clipboard.GetText();
        }
        catch (ExternalException ex)
        {
            sameThread = "(예외 " + ex.Message + ")";
        }

        Console.WriteLine($"INFO clipboard-remote-to-local-same-thread value={sameThread} ms={sw.ElapsedMilliseconds}");
        Check("clipboard-remote-to-local-same-thread", sameThread == remote2 && sw.ElapsedMilliseconds < 5000,
            $"Clipboard.GetText(UI 스레드) = {sameThread} · {sw.ElapsedMilliseconds}ms");

        // 해상도 변경(디스플레이 제어 채널)
        var before = (session.Width, session.Height);
        session.RequestDesktopSize(1024, 768);
        var resized = await WaitAsync(() => session.Width == 1024 && session.Height == 768 ? "yes" : null, 30_000);
        await Task.Delay(3000);
        Shot("05-resized");
        var resFile = Path.Combine(_profileDir, "e2e-res.txt");
        await TypeScancodesAsync(
            "Add-Type -AssemblyName System.Windows.Forms; $b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; " +
            "Set-Content -Encoding UTF8 $env:USERPROFILE\\e2e-res.txt (\"\" + $b.Width + 'x' + $b.Height)\n");
        var res = await WaitFileAsync(resFile, 20_000);
        Check("resize", resized is not null && res == "1024x768",
            $"{before.Width}x{before.Height} → client {session.Width}x{session.Height}, 세션 안 화면 {res ?? "(없음)"}");

        // 해상도 변경 뒤 입력·화면이 계속 동작
        var framesBefore = _frames;
        await TypeScancodesAsync("cls\n");
        await Task.Delay(2000);
        Shot("06-after-resize-input");
        Check("frames-after-resize", _frames > framesBefore, $"frames {framesBefore} → {_frames}");

        await TypeScancodesAsync("exit\n");
        await Task.Delay(1000);

        // 정상 종료
        var closed = new TaskCompletionSource<Exception?>();
        session.Closed += e => closed.TrySetResult(e);
        await session.DisconnectAsync();
        _session = null;
        Check("disconnect", !session.IsConnected, "DisconnectAsync 완료");
    }

    // ---- 가짜 PVE ----

    private void StartFakePve(int port)
    {
        // 인증서를 미리 만든다(fake_pve.py 는 없을 때만 openssl 을 부른다 — Windows 러너 PATH 에는 openssl 이 없을 수 있다)
        var tmp = Path.Combine(_out, "fakepve-tmp");
        var certDir = Path.Combine(tmp, "fakepve-cert");
        Directory.CreateDirectory(certDir);
        using (var key = RSA.Create(2048))
        {
            var req = new CertificateRequest("CN=pve1.local, O=PVE Cluster Manager CA", key, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
            File.WriteAllText(Path.Combine(certDir, "cert.pem"), cert.ExportCertificatePem());
            File.WriteAllText(Path.Combine(certDir, "key.pem"), key.ExportPkcs8PrivateKeyPem());
        }

        var psi = new ProcessStartInfo(options.GetValueOrDefault("python", "python"))
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
        };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(Path.GetFullPath(options.GetValueOrDefault("fakepve", "tools/FakePve/fake_pve.py")));
        psi.ArgumentList.Add(port.ToString());
        psi.Environment["TMPDIR"] = tmp;
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        if (options.TryGetValue("rdpdebug", out var dbg)) psi.Environment["FAKEPVE_RDP_DEBUG"] = dbg;
        var listening = new ManualResetEventSlim();
        _fake = Process.Start(psi)!;
        void Append(string? line)
        {
            if (line is null) return;
            lock (_serverLog) _serverLog.Append(line).Append('\n');
            if (line.StartsWith("listening")) listening.Set();
            if (line.StartsWith("RDP ")) Log("fake: " + line);
        }

        _fake.OutputDataReceived += (_, e) => Append(e.Data);
        _fake.ErrorDataReceived += (_, e) => Append(e.Data);
        _fake.BeginOutputReadLine();
        _fake.BeginErrorReadLine();
        if (!listening.Wait(20_000)) throw new InvalidOperationException("가짜 PVE 가 뜨지 않음: " + ServerLog());
        Log("fake PVE listening");
    }

    private static async Task<ProxmoxApiClient> LoginAsync(int port)
    {
        // 자체 서명 인증서 지문을 먼저 읽어 프로필에 고정한다(앱의 "처음 보는 인증서 — 신뢰"와 같다)
        string thumbprint;
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync("127.0.0.1", port);
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("127.0.0.1");
            thumbprint = ServerCertificateValidator.ComputeThumbprint(ssl.RemoteCertificate!);
        }

        var profile = new ConnectionProfile
        {
            Name = "fake", Host = "127.0.0.1", Port = port, UserName = "root@pam", CertificateThumbprint = thumbprint,
            Password = ToSecure("secret")
        };
        var api = new ProxmoxApiClient(profile);
        await api.LoginAsync();
        Log($"logged in to fake PVE as {api.AuthenticatedUser}");
        return api;
    }

    private static System.Security.SecureString ToSecure(string text)
    {
        var s = new System.Security.SecureString();
        foreach (var ch in text) s.AppendChar(ch);
        s.MakeReadOnly();
        return s;
    }

    private static async Task ConfigureAsync(int port, string target, string user, string password)
    {
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["target"] = target, ["user"] = user, ["password"] = password, ["vmid"] = "101"
        });
        (await http.PostAsync($"https://127.0.0.1:{port}/api2/json/__fake/rdp", form)).EnsureSuccessStatusCode();
    }

    private ProxmoxRdpSession NewSession(ProxmoxApiClient api)
    {
        var s = new ProxmoxRdpSession(api) { InitialSize = (1280, 800), ShareClipboard = true };
        s.StatusChanged += t => Log($"status: {t}");
        s.Connected += (w, h) => Log($"connected {w}x{h}");
        s.FrameReceived += (_, _, _, _) => Interlocked.Increment(ref _frames);
        s.Closed += e => Log($"closed: {e?.Message ?? "(정상)"}");
        return s;
    }

    private static int SelectedProtocol(string confirmHex)
    {
        // X.224 Connection Confirm + RDP_NEG_RSP(type 2, flags, length 8, selectedProtocol LE32) — 끝 4바이트
        var bytes = Convert.FromHexString(confirmHex);
        return bytes.Length >= 8 && bytes[^8] == 0x02 ? BitConverter.ToInt32(bytes, bytes.Length - 4) : -1;
    }

    // ---- 세션 안 확인 ----

    private string? NewSessionProcess(string name, HashSet<int> sessionsBefore)
    {
        foreach (var p in Process.GetProcessesByName(name))
            if (!sessionsBefore.Contains(p.SessionId))
            {
                _rdpSessionId = p.SessionId;
                return $"{name}#{p.Id}@session{p.SessionId}";
            }

        return null;
    }

    private async Task<string?> RemoteGetClipboardAsync(string file)
    {
        var path = Path.Combine(_profileDir, file);
        await TypeScancodesAsync($"Get-Clipboard | Set-Content -Encoding UTF8 $env:USERPROFILE\\{file}\n");
        return await WaitFileAsync(path, 20_000);
    }

    private static async Task<string?> WaitFileAsync(string path, int timeoutMs)
    {
        return await WaitAsync(() =>
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : null;
            }
            catch (IOException)
            {
                return null; // 아직 쓰는 중
            }
        }, timeoutMs);
    }

    private static async Task<string?> LocalClipboardOtherProcessAsync()
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "-NoProfile", "-Command", "[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-Clipboard" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return text.Trim();
    }

    private static async Task<string?> WaitAsync(Func<string?> probe, int timeoutMs)
    {
        return await WaitAsync(() => Task.FromResult(probe()), timeoutMs);
    }

    private static async Task<string?> WaitAsync(Func<Task<string?>> probe, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (await probe() is { } value) return value;
            if (sw.ElapsedMilliseconds > timeoutMs) return null;
            await Task.Delay(500);
        }
    }

    // ---- 입력 ----

    private async Task KeyAsync(int scancode, bool down)
    {
        await _session!.SendKeyAsync(scancode, down);
        await Task.Delay(15);
    }

    private const string ShiftedDigits = ")!@#$%^&*(";

    /// <summary>물리 키보드처럼 US 배치 스캔코드로 친다(필요하면 Shift).</summary>
    private async Task TypeScancodesAsync(string text)
    {
        foreach (var ch in text)
        {
            int code;
            bool shift;
            if (ch == '\n')
            {
                code = 0x1C;
                shift = false;
            }
            else if (ShiftedDigits.IndexOf(ch) is var d and >= 0)
            {
                code = RdpKeyMap.UsScancode('0' + d);
                shift = true;
            }
            else
            {
                code = RdpKeyMap.UsScancode(ch);
                shift = char.IsUpper(ch) || "_+{}:\"~|<>?".Contains(ch);
            }

            if (code == 0) throw new ArgumentException($"스캔코드 없음: {ch}");
            if (shift) await KeyAsync(0x2A, true);
            await KeyAsync(code, true);
            await KeyAsync(code, false);
            if (shift) await KeyAsync(0x2A, false);
            if (ch == '\n') await Task.Delay(300);
        }
    }

    /// <summary>화면 키보드·IME 처럼 keysym 만 보낸다(글자는 유니코드 키 입력).</summary>
    private async Task TypeKeysymAsync(string text)
    {
        foreach (var ch in text)
        {
            var keysym = ch is >= ' ' and <= '~' or >= '\u00A0' and <= '\u00FF' ? ch : 0x01000000 | ch;
            await _session!.SendKeyAsync(0, true, keysym);
            await Task.Delay(15);
            await _session.SendKeyAsync(0, false, keysym);
            await Task.Delay(15);
        }
    }

    // ---- 화면 ----

    private static int Colors(byte[]? fb)
    {
        if (fb is null) return 0;
        var set = new HashSet<int>();
        for (var i = 0; i + 3 < fb.Length; i += 4 * 97) set.Add(BitConverter.ToInt32(fb, i) & 0xFFFFFF);
        return set.Count;
    }

    private void Shot(string name)
    {
        var s = _session;
        if (s?.Framebuffer is not { } fb) return;
        int w = s.Width, h = s.Height;
        if (fb.Length < w * h * 4) return; // 크기가 막 바뀌었다
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var row = new byte[w * 4];
        for (var y = 0; y < h; y++)
        {
            Buffer.BlockCopy(fb, y * w * 4, row, 0, row.Length);
            for (var x = 3; x < row.Length; x += 4) row[x] = 255;
            Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
        }

        bmp.UnlockBits(data);
        var path = Path.Combine(_out, name + ".png");
        bmp.Save(path, ImageFormat.Png);
        Log($"shot {path} ({w}x{h})");
    }
}
