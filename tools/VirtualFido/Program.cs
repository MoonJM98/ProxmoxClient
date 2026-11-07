// 가상 USB 보안 키(/dev/uhid) 끝까지 검사 — 진짜 커널 HID(hidraw)를 거쳐 실제 LinuxHidWebAuthnProvider 로 서명을 받는다.
//   dotnet run --project tools/VirtualFido [-- --fakepve <포트>] [이름 일부]
//     → 검사마다 "PASS 이름 — …" / "FAIL 이름 — …", 끝에 "DONE ok" 또는 "DONE failures=N". /dev/uhid 가 없으면 "SKIP" 하고 0.
//     --fakepve 를 주면 tools/FakePve(webauthn@pam)에서 질문을 받아 서명을 돌려보내 로그인까지 본다.
//   dotnet run --project tools/VirtualFido -- serve [--pin 1234] [--always-uv] [--cred fake-credential] [--rp pve.example]
//     → 장치만 띄워 둔다("READY /dev/hidrawN" 출력, SIGTERM·Ctrl+C 로 뺀다). UiSnapshot --webauthn-key linux 용.
// 장치를 만들려면 /dev/uhid(modprobe uhid), 쓰려면 /dev/hidrawN 읽기·쓰기 권한이 있어야 한다(CI 는 udev 규칙).

using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Tools.Fido;
using ProxmoxClient.UI.Platform;

var argList = args.ToList();
string? Opt(string name)
{
    var i = argList.IndexOf("--" + name);
    if (i < 0) return null;

    var hasValue = i + 1 < argList.Count && !argList[i + 1].StartsWith("--", StringComparison.Ordinal);
    var v = hasValue ? argList[i + 1] : "";
    argList.RemoveRange(i, hasValue ? 2 : 1);
    return v;
}

if (!File.Exists("/dev/uhid"))
{
    Console.WriteLine("SKIP /dev/uhid 없음(sudo modprobe uhid) — 가상 USB 키 검사를 건너뜀");
    return 0;
}

if (argList.FirstOrDefault() == "serve")
{
    argList.RemoveAt(0);
    var pin = Opt("pin");
    var alwaysUv = Opt("always-uv") is not null;
    var cred = Opt("cred") ?? "fake-credential";
    var rp = Opt("rp") ?? "pve.example";
    var auth = new SoftAuthenticator(ctap2: true)
    {
        Pin = string.IsNullOrEmpty(pin) ? null : pin, AlwaysUv = alwaysUv, RpId = rp,
        CredentialId = Encoding.UTF8.GetBytes(cred)
    };
    using var served = new VirtualKey(auth, "vfido-serve");
    var hidraw = await Harness.WaitHidrawAsync(served);
    Console.WriteLine($"READY {hidraw}");
    using var stop = new CancellationTokenSource();
    using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c =>
    {
        c.Cancel = true;
        stop.Cancel();
    });
    using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c =>
    {
        c.Cancel = true;
        stop.Cancel();
    });
    try
    {
        await Task.Delay(Timeout.Infinite, stop.Token);
    }
    catch (OperationCanceledException)
    {
    }

    Console.WriteLine($"STOP commands={string.Join(",", served.Commands)}");
    return 0;
}

var fakePvePort = Opt("fakepve");
var filter = argList.FirstOrDefault();
return await new Harness(fakePvePort is { Length: > 0 } ? int.Parse(fakePvePort) : null, filter).RunAsync();

internal sealed class Harness(int? fakePvePort, string? filter)
{
    /// <summary>tools/FakePve 의 webauthn@pam 질문과 같은 것(--fakepve 없이 돌 때).</summary>
    private const string PveChallenge =
        """{"publicKey":{"challenge":"q83vEjRWeJA-_w","timeout":60000,"rpId":"pve.example","allowCredentials":[{"type":"public-key","id":"ZmFrZS1jcmVkZW50aWFs"}],"userVerification":"discouraged"}}""";

    private static readonly Uri Origin = new("https://pve.example:8006/");
    private static readonly byte[] CredentialId = "fake-credential"u8.ToArray();
    private const string AppId = "https://pve.example:8006";

    private int _failures;
    private int _keyNo;

    public async Task<int> RunAsync()
    {
        var cases = new (string Name, Func<Task<string>> Run)[]
        {
            ("hidraw_ping", RawPingAsync),
            ("ctap2_no_pin", NoPinAsync),
            ("pin_v2_wrong_then_right", PinV2Async),
            ("pin_v1_always_uv", PinV1AlwaysUvAsync),
            ("pin_prompt_cancel", PinPromptCancelAsync),
            ("u2f_appid_fallback", U2fAppIdAsync),
            ("u2f_only_key", U2fOnlyAsync),
            ("cancel_while_waiting_touch", CancelAsync),
            ("unplug_mid_request", UnplugAsync),
            ("no_key_timeout", NoKeyAsync),
            ("fakepve_login", FakePveLoginAsync)
        };
        foreach (var (name, run) in cases)
        {
            if (filter is not null && !name.Contains(filter, StringComparison.Ordinal)) continue;

            Console.WriteLine($"--- {name}");
            try
            {
                var detail = await run().WaitAsync(TimeSpan.FromSeconds(60));
                if (detail.StartsWith("SKIP", StringComparison.Ordinal))
                {
                    Console.WriteLine($"SKIP {name} — {detail[4..].Trim()}");
                    continue;
                }

                Console.WriteLine($"PASS {name} — {detail}");
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine($"FAIL {name} — {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex);
            }
        }

        Console.WriteLine(_failures == 0 ? "DONE ok" : $"DONE failures={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 검사

    /// <summary>커널 HID 틀 확인 — hidraw 를 직접 열어 INIT 뒤 150바이트 PING(여러 틀)을 되돌려 받는다.</summary>
    private async Task<string> RawPingAsync()
    {
        using var key = await PlugAsync(new SoftAuthenticator(ctap2: true));
        await using var fs = new FileStream(key.Hidraw!, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0);
        var nonce = RandomNumberGenerator.GetBytes(8);
        Write(fs, 0xFFFFFFFF, 0x86, nonce);
        var init = Read(fs);
        Check(init[4] == 0x86 && init.AsSpan(7, 8).SequenceEqual(nonce), "INIT 응답·nonce");
        var cid = (uint)((init[15] << 24) | (init[16] << 16) | (init[17] << 8) | init[18]);
        var payload = RandomNumberGenerator.GetBytes(150);
        Write(fs, cid, 0x81, payload);
        var first = Read(fs);
        Check(first[4] == 0x81 && ((first[5] << 8) | first[6]) == 150, "PING 머리");
        var echo = new List<byte>(first.AsSpan(7, 57).ToArray());
        for (byte seq = 0; echo.Count < 150; seq++)
        {
            var cont = Read(fs);
            Check(cont[4] == seq, $"이어진 틀 순서 {seq}");
            echo.AddRange(cont.AsSpan(5, Math.Min(59, 150 - echo.Count)).ToArray());
        }

        Check(echo.SequenceEqual(payload), "PING 되돌림 내용");
        // sysfs report_descriptor: 알린 크기와 실제 길이(공급자가 장치를 찾는 방법)
        var sys = $"/sys/class/hidraw/{Path.GetFileName(key.Hidraw)}/device/report_descriptor";
        var descriptor = ReadSysfs(sys);
        Check(descriptor.SequenceEqual(UhidDevice.FidoReportDescriptor), "report_descriptor 내용");
        string readAll;
        try
        {
            readAll = $"File.ReadAllBytes {File.ReadAllBytes(sys).Length}B";
        }
        catch (Exception ex)
        {
            readAll = $"File.ReadAllBytes → {ex.GetType().Name}";
        }

        return $"{key.Hidraw} report_descriptor {descriptor.Length}B(stat {new FileInfo(sys).Length}B, {readAll}) 0xF1D0 · INIT cid={cid:X8} caps=0x{init[23]:X2} · PING 150B(3틀) 같음 · key pings={key.Pings}";
    }

    private async Task<string> NoPinAsync()
    {
        var auth = Auth();
        using var key = await PlugAsync(auth);
        var json = await new LinuxHidWebAuthnProvider().GetAssertionAsync(Origin, PveChallenge, null,
            CancellationToken.None);
        var v = Verify(auth, json, "pve.example", "q83vEjRWeJA-_w");
        Check((v.Flags & 0x04) == 0, "PIN 없이 UV 플래그 없음");
        Check(key.KeepAlives > 0, "터치 대기 KEEPALIVE");
        return $"{v} · keepalives={key.KeepAlives} · cmds={string.Join(",", key.Commands)}";
    }

    private async Task<string> PinV2Async()
    {
        var auth = Auth(a => a with { Pin = "1234" });
        using var key = await PlugAsync(auth);
        var asked = new List<PinPromptInfo>();
        var answers = new Queue<string>(["0000", "1234"]);
        var challenge = PveChallenge.Replace("discouraged", "required");
        var json = await new LinuxHidWebAuthnProvider().GetAssertionAsync(Origin, challenge, (info, _) =>
        {
            asked.Add(info);
            return Task.FromResult<string?>(answers.Dequeue());
        }, CancellationToken.None);
        var v = Verify(auth, json, "pve.example", "q83vEjRWeJA-_w");
        Check(asked.Count == 2, $"PIN 두 번 물음({asked.Count})");
        Check(asked[0] is { RetriesLeft: 8, Error: null }, $"처음 남은 횟수 8·오류 없음({asked[0]})");
        Check(asked[1].RetriesLeft == 7 && !string.IsNullOrEmpty(asked[1].Error), $"틀린 뒤 7·오류({asked[1]})");
        Check(auth.LastProtocol == 2, "PIN 프로토콜 2");
        Check((v.Flags & 0x04) != 0, "UV 플래그");
        Check(auth.PinRetries == 8, "맞힌 뒤 남은 횟수 8 로");
        return $"{v} · prompts=[{asked[0].RetriesLeft},{asked[1].RetriesLeft} '{asked[1].Error}'] · protocol=2 · cmds={string.Join(",", key.Commands)}";
    }

    private async Task<string> PinV1AlwaysUvAsync()
    {
        var auth = Auth(a => a with { Pin = "4321", AlwaysUv = true, Protocols = [1], TokenWithPermissions = false });
        using var key = await PlugAsync(auth);
        var prompts = 0;
        var json = await new LinuxHidWebAuthnProvider().GetAssertionAsync(Origin, PveChallenge, (_, _) =>
        {
            prompts++;
            return Task.FromResult<string?>("4321");
        }, CancellationToken.None);
        var v = Verify(auth, json, "pve.example", "q83vEjRWeJA-_w");
        Check(prompts == 1, "PIN_REQUIRED 뒤 한 번 물음");
        Check(auth.LastProtocol == 1, "PIN 프로토콜 1");
        Check(key.Commands.Contains("CBOR 06"), "clientPIN 명령");
        return $"{v} · prompts=1 · protocol=1 (getPinToken) · cmds={string.Join(",", key.Commands)}";
    }

    private async Task<string> PinPromptCancelAsync()
    {
        using var key = await PlugAsync(Auth(a => a with { Pin = "1234" }));
        var json = await new LinuxHidWebAuthnProvider().GetAssertionAsync(Origin,
            PveChallenge.Replace("discouraged", "required"), (_, _) => Task.FromResult<string?>(null),
            CancellationToken.None);
        Check(json is null, "PIN 입력 취소 → null");
        return "PIN 묻기 취소 → null(창 유지용 취소)";
    }

    private async Task<string> U2fAppIdAsync()
    {
        // U2F 시절(appid)로 등록한 자격 증명 — CTAP2 는 0x2E, U2F authenticate(MSG)로 appid 해시에 서명
        var auth = Auth(a => a with { U2fAppId = AppId });
        using var key = await PlugAsync(auth);
        var challenge = PveChallenge.Replace("\"userVerification\"",
            "\"extensions\":{\"appid\":\"" + AppId + "\"},\"userVerification\"");
        var json = await new LinuxHidWebAuthnProvider().GetAssertionAsync(Origin, challenge, null,
            CancellationToken.None);
        var v = Verify(auth, json, AppId, "q83vEjRWeJA-_w");
        Check(key.Commands.Contains("CBOR 02") && key.Commands.Any(c => c.StartsWith("MSG 02", StringComparison.Ordinal)),
            "CTAP2 다음 U2F");
        return $"{v} · rpIdHash=SHA256(appid) · cmds={string.Join(",", key.Commands)}";
    }

    private async Task<string> U2fOnlyAsync()
    {
        var auth = new SoftAuthenticator(ctap2: false) { RpId = "pve.example", CredentialId = CredentialId };
        using var key = await PlugAsync(auth);
        var json = await new LinuxHidWebAuthnProvider().GetAssertionAsync(Origin, PveChallenge, null,
            CancellationToken.None);
        var v = Verify(auth, json, "pve.example", "q83vEjRWeJA-_w");
        Check(!key.Commands.Any(c => c.StartsWith("CBOR", StringComparison.Ordinal)), "CBOR 안 보냄(능력 비트)");
        return $"{v} · INIT caps 에 CBOR 없음 → U2F 만 · cmds={string.Join(",", key.Commands)}";
    }

    private async Task<string> CancelAsync()
    {
        using var key = await PlugAsync(Auth());
        key.TouchDelay = TimeSpan.FromSeconds(40);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        var sw = Stopwatch.StartNew();
        Exception? error = null;
        try
        {
            await new LinuxHidWebAuthnProvider().GetAssertionAsync(Origin, PveChallenge, null, cts.Token);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Check(error is OperationCanceledException,
            $"OperationCanceledException 이어야 함(받음: {error?.GetType().Name}: {error?.Message})");
        Check(sw.Elapsed < TimeSpan.FromSeconds(6), $"곧 끝남({sw.Elapsed.TotalSeconds:F1}s)");
        await WaitUntilAsync(() => key.Cancels > 0, 3000);
        Check(key.Cancels > 0, "키가 CTAPHID CANCEL 을 받음");
        return $"{sw.Elapsed.TotalSeconds:F1}s 에 OperationCanceledException · key cancels={key.Cancels} keepalives={key.KeepAlives}";
    }

    private async Task<string> UnplugAsync()
    {
        var key = await PlugAsync(Auth());
        key.TouchDelay = TimeSpan.FromSeconds(40);
        var sw = Stopwatch.StartNew();
        var task = new LinuxHidWebAuthnProvider().GetAssertionAsync(Origin, PveChallenge, null, CancellationToken.None);
        await WaitUntilAsync(() => key.KeepAlives > 2, 5000);
        key.Dispose(); // 키를 뽑는다(UHID_DESTROY)
        var unplugged = sw.Elapsed;
        Exception? error = null;
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Check(error is not TimeoutException, "뽑은 뒤 멈추지 않음");
        Check(error is WebAuthnException,
            $"WebAuthnException 이어야 함(받음: {error?.GetType().Name}: {error?.Message})");
        return $"{unplugged.TotalSeconds:F1}s 에 뽑음 → {sw.Elapsed.TotalSeconds:F1}s 에 WebAuthnException '{error!.Message}'";
    }

    private async Task<string> NoKeyAsync()
    {
        var sw = Stopwatch.StartNew();
        Exception? error = null;
        try
        {
            await new LinuxHidWebAuthnProvider().GetAssertionAsync(Origin,
                PveChallenge.Replace("\"timeout\":60000", "\"timeout\":1500"), null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Check(error is WebAuthnException, $"WebAuthnException(받음: {error?.GetType().Name})");
        return $"키 없음 → {sw.Elapsed.TotalSeconds:F1}s 에 '{error!.Message}'";
    }

    /// <summary>FakePve 에 webauthn@pam/secret 로 로그인 → 질문의 webauthn 항목으로 서명 → webauthn:{JSON} 으로 2단계 완료.</summary>
    private async Task<string> FakePveLoginAsync()
    {
        if (fakePvePort is not { } port) return "SKIP --fakepve 포트 없음";

        var auth = Auth();
        using var key = await PlugAsync(auth);
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        var origin = new Uri($"https://127.0.0.1:{port}/");
        var url = new Uri(origin, "/api2/json/access/ticket");
        using var first = await http.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "webauthn@pam", ["password"] = "secret"
        }));
        using var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var ticket = firstDoc.RootElement.GetProperty("data").GetProperty("ticket").GetString()!;
        var challenge = TfaChallenge.Parse(ticket, "webauthn@pam");
        var publicKey = challenge.SecurityKeyJson(TfaKind.WebAuthn) ?? throw new InvalidOperationException("webauthn 질문 없음");
        var json = await new LinuxHidWebAuthnProvider().GetAssertionAsync(origin, publicKey, null,
            CancellationToken.None);
        var v = Verify(auth, json, "pve.example", null, origin);
        using var second = await http.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "webauthn@pam", ["tfa-challenge"] = ticket,
            ["password"] = $"{TfaChallenge.WireName(TfaKind.WebAuthn)}:{ProxmoxApiClient.NormalizeTfaResponse(TfaKind.WebAuthn, json!)}"
        }));
        Check(second.StatusCode == HttpStatusCode.OK, $"2단계 응답 {(int)second.StatusCode}");
        using var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var full = doc.RootElement.GetProperty("data").GetProperty("ticket").GetString();
        Check(full == "PVE:webauthn@pam:0000::sig", $"로그인 티켓({full})");
        return $"FakePve :{port} 질문 → 실제 키 서명 → {full} · {v}";
    }

    // ------------------------------------------------------------------ 도움

    private static SoftAuthenticator Auth(Func<KeyConfig, KeyConfig>? edit = null)
    {
        var c = (edit ?? (x => x))(new KeyConfig());
        return new SoftAuthenticator(ctap2: true)
        {
            RpId = "pve.example", CredentialId = CredentialId, Pin = c.Pin, AlwaysUv = c.AlwaysUv,
            Protocols = c.Protocols, TokenWithPermissions = c.TokenWithPermissions, U2fAppId = c.U2fAppId
        };
    }

    private sealed record KeyConfig
    {
        public string? Pin { get; init; }
        public bool AlwaysUv { get; init; }
        public int[] Protocols { get; init; } = [2, 1];
        public bool TokenWithPermissions { get; init; } = true;
        public string? U2fAppId { get; init; }
    }

    /// <summary>키를 꽂고 hidraw 가 생겨 열 수 있을 때까지 기다린다(udev 가 권한을 줄 때까지).</summary>
    private async Task<VirtualKey> PlugAsync(SoftAuthenticator auth)
    {
        await WaitNoFidoAsync();
        var key = new VirtualKey(auth, $"vfido-{Environment.ProcessId}-{++_keyNo}");
        try
        {
            var path = await WaitHidrawAsync(key);
            Console.WriteLine($"  plugged {path}");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public static async Task<string> WaitHidrawAsync(VirtualKey key)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (key.Hidraw is { } path)
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0))
                        return path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    last = $"{path}: {ex.Message}";
                }

            await Task.Delay(100);
        }

        throw new InvalidOperationException("hidraw 장치가 생기지 않음 " + last);
    }

    /// <summary>앞 검사의 키가 빠질 때까지(공급자는 첫 FIDO 장치를 고른다).</summary>
    private static async Task WaitNoFidoAsync()
    {
        await WaitUntilAsync(() => !Directory.EnumerateDirectories("/sys/class/hidraw").Any(d =>
        {
            try
            {
                return ReadSysfs(Path.Combine(d, "device", "report_descriptor")).AsSpan()
                    .IndexOf([(byte)0x06, (byte)0xD0, (byte)0xF1]) >= 0;
            }
            catch (IOException)
            {
                return false;
            }
        }), 5000);
    }

    private static byte[] ReadSysfs(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
    }

    private sealed record Verified(int Flags, uint Counter, string Origin)
    {
        public override string ToString()
        {
            return $"서명 검증 ok · flags=0x{Flags:X2} counter={Counter} origin={Origin}";
        }
    }

    /// <summary>PVE 응답 JSON 의 서명을 등록된 공개 키로 검증하고 rpIdHash·clientData·자격 증명을 본다.</summary>
    private static Verified Verify(SoftAuthenticator auth, string? json, string rpHashSource, string? challenge,
        Uri? origin = null)
    {
        Check(json is not null, "응답 JSON 있음");
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        var resp = root.GetProperty("response");
        var authData = Base64Url.Decode(resp.GetProperty("authenticatorData").GetString()!);
        var clientData = Base64Url.Decode(resp.GetProperty("clientDataJSON").GetString()!);
        var signature = Base64Url.Decode(resp.GetProperty("signature").GetString()!);
        Check(Base64Url.Decode(root.GetProperty("id").GetString()!).SequenceEqual(auth.CredentialId), "자격 증명 ID");
        Check(root.GetProperty("rawId").GetString() == root.GetProperty("id").GetString(), "rawId == id");
        Check(authData.AsSpan(0, 32).SequenceEqual(WebAuthnRequest.RpIdHash(rpHashSource)), $"rpIdHash = SHA256({rpHashSource})");
        Check((authData[32] & 0x01) != 0, "UP 플래그");
        Check(auth.Ecdsa.VerifyData((byte[])[.. authData, .. SHA256.HashData(clientData)], signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence), "ECDSA P-256 서명(등록 공개 키)");
        using var cd = JsonDocument.Parse(clientData);
        var expectedOrigin = WebAuthnRequest.OriginString(origin ?? Origin);
        Check(cd.RootElement.GetProperty("type").GetString() == "webauthn.get", "clientData type");
        Check(cd.RootElement.GetProperty("origin").GetString() == expectedOrigin, "clientData origin");
        var ch = cd.RootElement.GetProperty("challenge").GetString();
        Check(challenge is null || ch == challenge, "clientData challenge");
        Check(root.GetProperty("challenge").GetString() == ch, "응답 challenge");
        var counter = (uint)((authData[33] << 24) | (authData[34] << 16) | (authData[35] << 8) | authData[36]);
        return new Verified(authData[32], counter, expectedOrigin);
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException("확인 실패: " + what);
    }

    private static void Write(FileStream fs, uint cid, byte cmd, byte[] data)
    {
        var frame = new byte[65];
        frame[1] = (byte)(cid >> 24);
        frame[2] = (byte)(cid >> 16);
        frame[3] = (byte)(cid >> 8);
        frame[4] = (byte)cid;
        frame[5] = cmd;
        frame[6] = (byte)(data.Length >> 8);
        frame[7] = (byte)data.Length;
        var sent = Math.Min(data.Length, 57);
        Array.Copy(data, 0, frame, 8, sent);
        fs.Write(frame);
        for (byte seq = 0; sent < data.Length; seq++)
        {
            frame = new byte[65];
            frame[1] = (byte)(cid >> 24);
            frame[2] = (byte)(cid >> 16);
            frame[3] = (byte)(cid >> 8);
            frame[4] = (byte)cid;
            frame[5] = seq;
            var n = Math.Min(data.Length - sent, 59);
            Array.Copy(data, sent, frame, 6, n);
            sent += n;
            fs.Write(frame);
        }
    }

    private static byte[] Read(FileStream fs)
    {
        var buf = new byte[64];
        var task = Task.Run(() => fs.Read(buf));
        if (!task.Wait(5000)) throw new TimeoutException("hidraw 읽기 5초 넘음");
        Check(task.Result == 64, $"입력 보고서 64바이트({task.Result})");
        return buf;
    }
}
