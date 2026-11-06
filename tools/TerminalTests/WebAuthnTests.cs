using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Tools.Fido;

/// <summary>
///     보안 키(WebAuthn) 2단계 인증 — 질문 읽기, clientDataJSON, PVE 응답 모양(proxmox-widget-toolkit TfaWindow.js
///     loginWebauthn 과 같다), 보안 키 직접 통신(CTAP2·U2F, NFC APDU·USB HID 틀)을 소프트웨어 인증기로 확인한다.
/// </summary>
public static class WebAuthnTests
{
    /// <summary>PVE 8 질문의 webauthn 항목(webauthn-rs RequestChallengeResponse 모양, U2F 에서 옮긴 키의 appid 포함).</summary>
    private const string Challenge =
        """{"publicKey":{"challenge":"3q2-7wABAgMEBQYHCAkKCw","timeout":60000,"rpId":"pve.example.com","allowCredentials":[{"type":"public-key","id":"AQIDBA"},{"type":"public-key","id":"BQYHCA"}],"userVerification":"discouraged","extensions":{"appid":"https://pve.example.com:8006"}}}""";

    private static readonly Uri Origin = new("https://pve.example.com:8006/");

    public static void ParsesPublicKey(Checker c)
    {
        var r = WebAuthnRequest.Parse(Challenge);
        c.Equal("3q2-7wABAgMEBQYHCAkKCw", r.Challenge, "challenge 문자열 그대로");
        c.Equal("pve.example.com", r.RpId, "rpId");
        c.Equal(60000, r.TimeoutMs, "timeout");
        c.Equal("discouraged", r.UserVerification, "userVerification");
        c.Equal("https://pve.example.com:8006", r.AppId, "appid 확장");
        c.Equal("01020304,05060708", string.Join(",", r.AllowCredentials.Select(Convert.ToHexString)),
            "자격 증명 ID(base64url 풀기)");

        var bare = WebAuthnRequest.Parse("""{"publicKey":{"challenge":"AAAA"}}""");
        c.Is(bare.RpId is null && bare.AllowCredentials.Count == 0 && bare.TimeoutMs == 60000, "빠진 값은 기본값");
        c.Equal("10.0.0.5", bare.EffectiveRpId(new Uri("https://10.0.0.5:8006")), "rpId 없으면 접속 호스트");

        var bad = false;
        try
        {
            WebAuthnRequest.Parse("""{"publicKey":{}}""");
        }
        catch (FormatException)
        {
            bad = true;
        }

        c.Is(bad, "challenge 없으면 FormatException");
    }

    public static void ChallengeExposesWebAuthnKind(Checker c)
    {
        var json = """{"totp":true,"webauthn":""" + Challenge + "}";
        var ticket = "PVE:!tfa!" + json.Replace("%", "%25").Replace(":", "%3A") + ":6720A0B1::c2ln";
        var ch = TfaChallenge.Parse(ticket, "alice@pve");
        c.Is(ch.WebAuthn is { RpId: "pve.example.com" }, "질문의 webauthn 을 읽음");
        c.Equal("WebAuthn,Totp", string.Join(",", ch.AvailableKinds(true)), "보안 키 가능 — 맨 앞(PVE 웹 UI 순서)");
        c.Equal("Totp", string.Join(",", ch.AvailableKinds(false)), "보안 키 불가 — 코드 방식만");
        c.Equal("Totp", string.Join(",", ch.SupportedKinds), "코드 방식 목록은 그대로");
    }

    public static void ClientDataJson(Checker c)
    {
        var r = WebAuthnRequest.Parse(Challenge);
        var text = Encoding.UTF8.GetString(r.BuildClientDataJson(Origin));
        c.Equal(
            """{"type":"webauthn.get","challenge":"3q2-7wABAgMEBQYHCAkKCw","origin":"https://pve.example.com:8006","crossOrigin":false}""",
            text, "브라우저와 같은 clientDataJSON");
        c.Equal("https://pve.example.com", WebAuthnRequest.OriginString(new Uri("https://pve.example.com:443/api2/json/")),
            "443 이면 포트 빠짐(Host 헤더와 같다)");
        c.Equal("https://[fd00::1]:8006", WebAuthnRequest.OriginString(new Uri("https://[fd00::1]:8006/api2/json/")),
            "IPv6 는 [..]");
    }

    /// <summary>TfaWindow.js 가 보내는 것과 같은 모양 — 키 순서·base64url(패딩 없음)·challenge 원문·rawId == id.</summary>
    public static void PveResponseFormat(Checker c)
    {
        var a = new WebAuthnAssertion([0xFB, 0xFF, 0x01], [0x01, 0x02, 0x03, 0x04, 0x05], "{}"u8.ToArray(),
            [0x30, 0x44, 0xFE]);
        var json = a.ToPveResponse("3q2-7wABAgMEBQYHCAkKCw");
        c.Equal(
            """{"id":"-_8B","type":"public-key","challenge":"3q2-7wABAgMEBQYHCAkKCw","rawId":"-_8B","response":{"authenticatorData":"AQIDBAU","clientDataJSON":"e30","signature":"MET-"}}""",
            json, "password 의 webauthn: 뒤 JSON");
        c.Equal("webauthn:" + json, $"{TfaChallenge.WireName(TfaKind.WebAuthn)}:{ProxmoxApiClient.NormalizeTfaResponse(TfaKind.WebAuthn, json)}",
            "CompleteTfaAsync 가 보내는 password 문자열");
        c.Equal("-_8B", Base64Url.Encode([0xFB, 0xFF, 0x01]), "base64url 인코딩");
        c.Equal("FBFF01", Convert.ToHexString(Base64Url.Decode("-_8B")), "base64url 풀기");
        c.Equal("FBFF01", Convert.ToHexString(Base64Url.Decode("+/8B")), "표준 base64 도 받음");
    }

    public static void CborRoundTrip(Checker c)
    {
        var bytes = new Cbor.Writer().Map(3).Int(1).Text("pve").Int(2).Bytes(new byte[300]).Int(-7).Array(2).Bool(true)
            .Int(70000).ToArray();
        var map = Cbor.Decode(bytes) as Dictionary<object, object?>;
        c.Is(map is not null && (string)map[1L]! == "pve" && ((byte[])map[2L]!).Length == 300, "맵·문자열·긴 바이트열");
        c.Is(map![-7L] is List<object?> { Count: 2 } l && (bool)l[0]! && (long)l[1]! == 70000, "음수 키·배열·참·큰 정수");
    }

    /// <summary>NFC CTAP2 — 긴 허용 목록(이어 보내기)·긴 응답(61xx)·처리 중(9100)을 거쳐 서명이 맞는지.</summary>
    public static void NfcCtap2Assertion(Checker c)
    {
        var key = new SoftAuthenticator(ctap2: true);
        var ids = Enumerable.Range(0, 6).Select(i => Enumerable.Repeat((byte)(0x40 + i), 64).ToArray()).ToList();
        key.CredentialId = ids[4];
        var r = WebAuthnRequest.Parse(Challenge) with { AllowCredentials = ids, AppId = null };
        var transport = new NfcCtapTransport((apdu, _) => Task.FromResult(key.Nfc(apdu)));
        c.Equal("FIDO_2_0", transport.SelectAsync(CancellationToken.None).GetAwaiter().GetResult(), "FIDO 애플릿 고름");
        var a = FidoClient.GetAssertionAsync(transport, r, Origin, CancellationToken.None).GetAwaiter().GetResult();
        c.Is(key.ChainedCommands > 0, "255바이트 넘는 명령은 이어 보내기", key.ChainedCommands);
        c.Is(key.GetResponses > 0, "긴 응답은 GET RESPONSE 로", key.GetResponses);
        Verify(c, key, a, "pve.example.com", r);
    }

    /// <summary>U2F 시절 등록(appid) — CTAP2 는 "자격 증명 없음"을 주고, U2F authenticate 를 appid 해시로 다시 한다.</summary>
    public static void NfcU2fFallbackWithAppId(Checker c)
    {
        var key = new SoftAuthenticator(ctap2: false) { U2fAppId = "https://pve.example.com:8006" };
        key.CredentialId = [0x05, 0x06, 0x07, 0x08];
        var r = WebAuthnRequest.Parse(Challenge);
        var transport = new NfcCtapTransport((apdu, _) => Task.FromResult(key.Nfc(apdu)));
        transport.SelectAsync(CancellationToken.None).GetAwaiter().GetResult();
        var a = FidoClient.GetAssertionAsync(transport, r, Origin, CancellationToken.None).GetAwaiter().GetResult();
        c.Equal("05060708", Convert.ToHexString(a.CredentialId), "맞는 키 핸들(확인만 P1=7 로 찾음)");
        c.Equal(5, a.AuthenticatorData[36], "U2F 서명 횟수가 인증기 데이터로");
        Verify(c, key, a, "https://pve.example.com:8006", r);
    }

    /// <summary>USB HID — INIT 로 채널을 열고, 64바이트 틀로 나눠 보내고, KEEPALIVE 를 건너뛰고 응답을 합친다.</summary>
    public static void HidCtap2Assertion(Checker c)
    {
        var key = new SoftAuthenticator(ctap2: true);
        var ids = Enumerable.Range(0, 3).Select(i => Enumerable.Repeat((byte)(0x10 + i), 48).ToArray()).ToList();
        key.CredentialId = ids[1];
        var r = WebAuthnRequest.Parse(Challenge) with { AllowCredentials = ids };
        var hid = new SoftHid(key);
        var transport = new HidCtapTransport((f, _) =>
        {
            hid.Write(f);
            return Task.CompletedTask;
        }, _ => Task.FromResult(hid.Read()));
        transport.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
        var a = FidoClient.GetAssertionAsync(transport, r, Origin, CancellationToken.None).GetAwaiter().GetResult();
        c.Is(hid.KeepAlives > 0, "KEEPALIVE 건너뜀", hid.KeepAlives);
        Verify(c, key, a, "pve.example.com", r);
    }

    /// <summary>PIN 프로토콜 2 + CTAP 2.1 토큰(권한 ga) — 서버가 사용자 확인 required, 첫 PIN 은 틀리고 다시 묻는다.</summary>
    public static void PinProtocol2WithRetry(Checker c)
    {
        var key = new SoftAuthenticator(ctap2: true) { Pin = "1234", CredentialId = [0x01, 0x02, 0x03, 0x04] };
        var r = WebAuthnRequest.Parse(Challenge) with { UserVerification = "required", AppId = null };
        var asked = new List<PinPromptInfo>();
        var answers = new Queue<string>(["0000", "1234"]);
        var transport = new NfcCtapTransport((apdu, _) => Task.FromResult(key.Nfc(apdu)));
        transport.SelectAsync(CancellationToken.None).GetAwaiter().GetResult();
        var a = FidoClient.GetAssertionAsync(transport, r, Origin, CancellationToken.None, (info, _) =>
        {
            asked.Add(info);
            return Task.FromResult<string?>(answers.Dequeue());
        }).GetAwaiter().GetResult();
        c.Equal(2, asked.Count, "PIN 을 두 번 물음");
        c.Is(asked[0] is { RetriesLeft: 8, Error: null }, "처음: 남은 횟수 8, 오류 없음", asked[0]);
        c.Is(asked[1].RetriesLeft == 7 && !string.IsNullOrEmpty(asked[1].Error), "틀린 뒤: 남은 횟수 7·PIN 오류 문구",
            asked[1]);
        c.Equal(2, key.LastProtocol, "PIN 프로토콜 2");
        c.Is((a.AuthenticatorData[32] & 0x04) != 0, "UV 플래그(PIN 확인)");
        Verify(c, key, a, "pve.example.com", r);
    }

    /// <summary>PIN 프로토콜 1 + getPinToken(CTAP 2.0 키) — 항상 사용자 확인을 요구하는 키는 PIN_REQUIRED 뒤 PIN 을 묻는다.</summary>
    public static void PinProtocol1OnPinRequired(Checker c)
    {
        var key = new SoftAuthenticator(ctap2: true)
        {
            Pin = "4321", AlwaysUv = true, Protocols = [1], TokenWithPermissions = false,
            CredentialId = [0x01, 0x02, 0x03, 0x04]
        };
        var r = WebAuthnRequest.Parse(Challenge) with { AppId = null };
        var hid = new SoftHid(key);
        var transport = new HidCtapTransport((f, _) =>
        {
            hid.Write(f);
            return Task.CompletedTask;
        }, _ => Task.FromResult(hid.Read()));
        transport.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
        var prompts = 0;
        var a = FidoClient.GetAssertionAsync(transport, r, Origin, CancellationToken.None, (_, _) =>
        {
            prompts++;
            return Task.FromResult<string?>("4321");
        }).GetAwaiter().GetResult();
        c.Equal(1, prompts, "PIN_REQUIRED 뒤 PIN 한 번");
        c.Equal(1, key.LastProtocol, "PIN 프로토콜 1(인증 값 16바이트)");
        Verify(c, key, a, "pve.example.com", r);
    }

    public static void PinCancelBlockedNotSet(Checker c)
    {
        var r = WebAuthnRequest.Parse(Challenge) with { UserVerification = "required", AppId = null };

        NfcCtapTransport Open(SoftAuthenticator key)
        {
            var t = new NfcCtapTransport((apdu, _) => Task.FromResult(key.Nfc(apdu)));
            t.SelectAsync(CancellationToken.None).GetAwaiter().GetResult();
            return t;
        }

        var cancelKey = new SoftAuthenticator(ctap2: true) { Pin = "1234", CredentialId = [0x01, 0x02, 0x03, 0x04] };
        var cancelled = FidoClient.GetPveResponseAsync(Open(cancelKey), r, Origin,
            (_, _) => Task.FromResult<string?>(null), CancellationToken.None).GetAwaiter().GetResult();
        c.Is(cancelled is null, "PIN 입력 취소 → null(취소)");

        string? Error(SoftAuthenticator key, PinPrompt? prompt)
        {
            try
            {
                FidoClient.GetAssertionAsync(Open(key), r, Origin, CancellationToken.None, prompt).GetAwaiter()
                    .GetResult();
                return null;
            }
            catch (WebAuthnException ex)
            {
                return ex.Message;
            }
        }

        PinPrompt any = (_, _) => Task.FromResult<string?>("1234");
        var blocked = Error(new SoftAuthenticator(ctap2: true) { Pin = "1234", PinRetries = 0, CredentialId = [0x01, 0x02, 0x03, 0x04] },
            any);
        var notSet = Error(new SoftAuthenticator(ctap2: true) { Pin = null, CredentialId = [0x01, 0x02, 0x03, 0x04] }, any);
        var noPrompt = Error(new SoftAuthenticator(ctap2: true) { Pin = "1234", CredentialId = [0x01, 0x02, 0x03, 0x04] }, null);
        c.Is(blocked is not null && notSet is not null && noPrompt is not null && blocked != notSet && notSet != noPrompt,
            "PIN 잠김 · PIN 미설정 · PIN 못 물음은 각각 다른 오류", $"{blocked} | {notSet} | {noPrompt}");
    }

    /// <summary>옛 U2F 질문(proxmox-tfa U2fChallenge) → WebAuthn 모양으로 바꿔 U2F 키로 서명 → u2f.sign 결과 모양으로 되돌림.</summary>
    public static void U2fChallengeRoundTrip(Checker c)
    {
        const string u2f =
            """{"challenge":{"challenge":"Yz9mXk2wQ1-abc_def0123456789ABCDEFGHIJKLMNO","appId":"https://pve.example.com:8006","version":"U2F_V2"},"keys":[{"keyHandle":"BQYHCA","version":"U2F_V2"}]}""";
        var json = """{"totp":true,"u2f":""" + u2f + ""","yubico":true}""";
        var ticket = "PVE:!tfa!" + json.Replace("%", "%25").Replace(":", "%3A") + ":6720A0B1::c2ln";
        var ch = TfaChallenge.Parse(ticket, "bob@pve");
        c.Equal("Totp,U2f,Yubico", string.Join(",", ch.AvailableKinds(true)), "U2F 는 Yubico 앞(PVE 웹 UI 순서)");
        c.Equal("Totp,Yubico", string.Join(",", ch.AvailableKinds(false)), "보안 키 불가면 빠짐");

        var r = WebAuthnRequest.Parse(ch.SecurityKeyJson(TfaKind.U2f)!);
        c.Is(r is { U2f: true, AppId: "https://pve.example.com:8006", RpId: "pve.example.com" }, "appid 확장 WebAuthn 질문",
            r);
        var key = new SoftAuthenticator(ctap2: true)
        {
            U2fAppId = "https://pve.example.com:8006", CredentialId = [0x05, 0x06, 0x07, 0x08]
        };
        var transport = new NfcCtapTransport((apdu, _) => Task.FromResult(key.Nfc(apdu)));
        transport.SelectAsync(CancellationToken.None).GetAwaiter().GetResult();
        var webauthn = FidoClient.GetPveResponseAsync(transport, r, Origin, null, CancellationToken.None).GetAwaiter()
            .GetResult()!;
        var resp = U2fCompat.FromWebAuthnResponse(webauthn);
        using var doc = JsonDocument.Parse(resp);
        var root = doc.RootElement;
        c.Equal("BQYHCA", root.GetProperty("keyHandle").GetString(), "keyHandle");
        var clientData = Base64Url.Decode(root.GetProperty("clientData").GetString()!);
        using var cd = JsonDocument.Parse(clientData);
        c.Equal("navigator.id.getAssertion", cd.RootElement.GetProperty("typ").GetString(), "U2F clientData typ");
        c.Equal("Yz9mXk2wQ1-abc_def0123456789ABCDEFGHIJKLMNO", cd.RootElement.GetProperty("challenge").GetString(),
            "U2F challenge");
        c.Equal("https://pve.example.com:8006", cd.RootElement.GetProperty("origin").GetString(), "U2F origin");
        // proxmox-tfa auth_verify 와 같은 검증: SHA-256(appId) ‖ 사용자 확인·횟수(5) ‖ SHA-256(clientData)
        var sigData = Base64Url.Decode(root.GetProperty("signatureData").GetString()!);
        var signed = WebAuthnRequest.RpIdHash("https://pve.example.com:8006").Concat(sigData[..5])
            .Concat(SHA256.HashData(clientData)).ToArray();
        c.Is(sigData[0] == 0x01 && key.Ecdsa.VerifyData(signed, sigData[5..], HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence), "U2F signatureData 서명 검증");
    }

    public static void NoMatchingCredential(Checker c)
    {
        var key = new SoftAuthenticator(ctap2: true) { CredentialId = [0x99] };
        var r = WebAuthnRequest.Parse(Challenge) with { AppId = null };
        var transport = new NfcCtapTransport((apdu, _) => Task.FromResult(key.Nfc(apdu)));
        transport.SelectAsync(CancellationToken.None).GetAwaiter().GetResult();
        string? error = null;
        try
        {
            FidoClient.GetAssertionAsync(transport, r, Origin, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (WebAuthnException ex)
        {
            error = ex.Message;
        }

        c.Is(!string.IsNullOrEmpty(error), "등록 안 된 키는 WebAuthnException", error);
    }

    /// <summary>서명이 인증기 데이터 ‖ SHA-256(clientDataJSON) 에 대해 맞고, rpIdHash·origin·challenge 가 맞는지.</summary>
    private static void Verify(Checker c, SoftAuthenticator key, WebAuthnAssertion a, string rpHashSource, WebAuthnRequest r)
    {
        c.Equal(Convert.ToHexString(key.CredentialId), Convert.ToHexString(a.CredentialId), "자격 증명 ID");
        c.Equal(Convert.ToHexString(WebAuthnRequest.RpIdHash(rpHashSource)),
            Convert.ToHexString(a.AuthenticatorData.AsSpan(0, 32)), "rpIdHash");
        c.Is((a.AuthenticatorData[32] & 0x01) != 0, "사용자 확인(UP) 플래그");
        var signed = a.AuthenticatorData.Concat(SHA256.HashData(a.ClientDataJson)).ToArray();
        c.Is(key.Ecdsa.VerifyData(signed, a.Signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence), "ECDSA 서명 검증");
        using var cd = JsonDocument.Parse(a.ClientDataJson);
        c.Equal("https://pve.example.com:8006", cd.RootElement.GetProperty("origin").GetString(), "clientData origin");
        c.Equal(r.Challenge, cd.RootElement.GetProperty("challenge").GetString(), "clientData challenge");
        using var pve = JsonDocument.Parse(a.ToPveResponse(r.Challenge));
        c.Equal(r.Challenge, pve.RootElement.GetProperty("challenge").GetString(), "PVE 응답 challenge");
    }

    /// <summary>소프트웨어 USB 키 — CTAPHID 틀을 받아 <see cref="SoftAuthenticator" /> 로 넘기고 답을 틀로 나눠 돌려준다.</summary>
    private sealed class SoftHid(SoftAuthenticator key)
    {
        private readonly Queue<byte[]> _out = new();
        private readonly List<byte> _in = [];
        private int _need;
        private byte _cmd;
        private uint _cid;
        public int KeepAlives { get; private set; }

        public void Write(byte[] f)
        {
            var cid = (uint)((f[0] << 24) | (f[1] << 16) | (f[2] << 8) | f[3]);
            if ((f[4] & 0x80) != 0)
            {
                _cid = cid;
                _cmd = (byte)(f[4] & 0x7F);
                _need = (f[5] << 8) | f[6];
                _in.Clear();
                _in.AddRange(f.AsSpan(7, Math.Min(_need, 57)).ToArray());
            }
            else
            {
                _in.AddRange(f.AsSpan(5, Math.Min(_need - _in.Count, 59)).ToArray());
            }

            if (_in.Count < _need) return;

            var data = _in.ToArray();
            switch (_cmd)
            {
                case 0x06:
                    Reply(0x06, [.. data, 0x00, 0x00, 0x00, 0x2A, 2, 5, 4, 0, 0x04 | 0x01]);
                    break;
                case 0x10:
                    Reply(0x3B, [0x02]);
                    KeepAlives++;
                    Reply(0x10, key.Ctap2(data)!);
                    break;
                default:
                    Reply(0x3F, [0x01]);
                    break;
            }
        }

        public byte[] Read()
        {
            return _out.Dequeue();
        }

        private void Reply(byte cmd, byte[] payload)
        {
            var cid = cmd == 0x06 ? 0xFFFFFFFF : 0x0000002Au;
            if (cmd != 0x06 && _cid != cid) throw new InvalidOperationException("채널 ID");

            var f = new byte[64];
            Cid(f, cid);
            f[4] = (byte)(0x80 | cmd);
            f[5] = (byte)(payload.Length >> 8);
            f[6] = (byte)payload.Length;
            var sent = Math.Min(payload.Length, 57);
            Array.Copy(payload, 0, f, 7, sent);
            _out.Enqueue(f);
            byte seq = 0;
            while (sent < payload.Length)
            {
                f = new byte[64];
                Cid(f, cid);
                f[4] = seq++;
                var n = Math.Min(payload.Length - sent, 59);
                Array.Copy(payload, sent, f, 5, n);
                sent += n;
                _out.Enqueue(f);
            }
        }

        private static void Cid(byte[] f, uint cid)
        {
            f[0] = (byte)(cid >> 24);
            f[1] = (byte)(cid >> 16);
            f[2] = (byte)(cid >> 8);
            f[3] = (byte)cid;
        }
    }
}
