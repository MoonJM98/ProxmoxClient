using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProxmoxClient.Core.Api;

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
        var key = new SoftKey(ctap2: true);
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
        var key = new SoftKey(ctap2: false) { U2fAppId = "https://pve.example.com:8006" };
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
        var key = new SoftKey(ctap2: true);
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

    public static void NoMatchingCredential(Checker c)
    {
        var key = new SoftKey(ctap2: true) { CredentialId = [0x99] };
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
    private static void Verify(Checker c, SoftKey key, WebAuthnAssertion a, string rpHashSource, WebAuthnRequest r)
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

    /// <summary>소프트웨어 보안 키 — NFC APDU(ISO 7816 짧은 APDU, 이어 보내기·61xx·9100)로 CTAP2·U2F 에 답한다.</summary>
    private sealed class SoftKey(bool ctap2)
    {
        public ECDsa Ecdsa { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public byte[] CredentialId { get; set; } = [];
        public string? U2fAppId { get; init; }
        public int ChainedCommands { get; private set; }
        public int GetResponses { get; private set; }
        private uint _counter = 4;
        private readonly List<byte> _chain = [];
        private byte[] _pending = [];
        private bool _busyOnce = true;

        /// <summary>CTAP2 명령(명령 바이트 + CBOR) → 상태 + CBOR. CTAP2 를 모르면 null.</summary>
        public byte[]? Ctap2(byte[] request)
        {
            if (!ctap2) return null;
            if (request[0] != 0x02) return [0x01];

            var m = (Dictionary<object, object?>)Cbor.Decode(request.AsSpan(1))!;
            var rpId = (string)m[1L]!;
            var hash = (byte[])m[2L]!;
            var allow = m.TryGetValue(3L, out var l) ? ((List<object?>)l!).Cast<Dictionary<object, object?>>() : [];
            if (!allow.Any(x => ((byte[])x["id"]!).SequenceEqual(CredentialId))) return [0x2E];

            var authData = AuthData(rpId, 0x01);
            var sig = Ecdsa.SignData(authData.Concat(hash).ToArray(), HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            var body = new Cbor.Writer().Map(3)
                .Int(1).Map(2).Text("id").Bytes(CredentialId).Text("type").Text("public-key")
                .Int(2).Bytes(authData).Int(3).Bytes(sig).ToArray();
            return [0x00, .. body];
        }

        /// <summary>U2F authenticate → 데이터 + SW.</summary>
        public byte[] U2f(byte ins, byte p1, byte[] data)
        {
            if (ins != 0x02) return [0x6D, 0x00];

            var clientHash = data[..32];
            var appHash = data[32..64];
            var kh = data[65..(65 + data[64])];
            var app = U2fAppId ?? "pve.example.com";
            if (!kh.SequenceEqual(CredentialId) || !appHash.SequenceEqual(WebAuthnRequest.RpIdHash(app)))
                return [0x6A, 0x80];
            if (p1 == 0x07) return [0x69, 0x85];

            _counter++;
            var counter = new[] { (byte)(_counter >> 24), (byte)(_counter >> 16), (byte)(_counter >> 8), (byte)_counter };
            // U2F 서명 대상: appHash ‖ UP ‖ counter ‖ clientHash — WebAuthn 의 authData ‖ clientHash 와 같다
            var sig = Ecdsa.SignData(appHash.Concat(new byte[] { 0x01 }).Concat(counter).Concat(clientHash).ToArray(),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return [0x01, .. counter, .. sig, 0x90, 0x00];
        }

        public byte[] Nfc(byte[] apdu)
        {
            var (cla, ins, p1) = (apdu[0], apdu[1], apdu[2]);
            var data = apdu.Length > 5 ? apdu[5..(5 + apdu[4])] : [];
            if (ins == 0xA4) return [.. Encoding.ASCII.GetBytes(ctap2 ? "FIDO_2_0" : "U2F_V2"), 0x90, 0x00];
            if (ins == 0xC0) return Next();

            if ((cla & 0x10) != 0)
            {
                ChainedCommands++;
                _chain.AddRange(data);
                return [0x90, 0x00];
            }

            var full = _chain.Concat(data).ToArray();
            _chain.Clear();
            if (cla == 0x80 && ins == 0x10)
            {
                if (Ctap2(full) is not { } r) return [0x6D, 0x00];

                _pending = r;
                if (_busyOnce)
                {
                    _busyOnce = false;
                    return [0x91, 0x00]; // 처리 중 — 다음 GETRESPONSE 에 답한다
                }

                return Next();
            }

            if (cla == 0x80 && ins == 0x11) return Next();

            var u = U2f(ins, p1, full);
            _pending = u[..^2];
            return u.Length > 2 && u[^2] == 0x90 ? Next() : u;
        }

        /// <summary>64바이트씩 61xx 로 나눠 보낸다.</summary>
        private byte[] Next()
        {
            if (_pending.Length > 64)
            {
                GetResponses++;
                var head = _pending[..64];
                _pending = _pending[64..];
                return [.. head, 0x61, (byte)Math.Min(_pending.Length, 255)];
            }

            var last = _pending;
            _pending = [];
            return [.. last, 0x90, 0x00];
        }

        private byte[] AuthData(string rpId, byte flags)
        {
            _counter++;
            return
            [
                .. WebAuthnRequest.RpIdHash(rpId), flags, (byte)(_counter >> 24), (byte)(_counter >> 16),
                (byte)(_counter >> 8), (byte)_counter
            ];
        }
    }

    /// <summary>소프트웨어 USB 키 — CTAPHID 틀을 받아 <see cref="SoftKey" /> 로 넘기고 답을 틀로 나눠 돌려준다.</summary>
    private sealed class SoftHid(SoftKey key)
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
