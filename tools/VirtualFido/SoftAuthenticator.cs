using System.Security.Cryptography;
using System.Text;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.Tools.Fido;

/// <summary>
///     소프트웨어 보안 키(P-256) — CTAP2(getInfo·getAssertion·clientPIN v1/v2)와 U2F authenticate 에 답한다.
///     NFC APDU(ISO 7816 짧은 APDU, 이어 보내기·61xx·9100)는 <see cref="Nfc" />, USB(CTAPHID)는 VirtualFido 의 uhid 장치가 감싼다.
///     자격 증명 하나(<see cref="CredentialId" />)가 <see cref="RpId" />(U2F 시절 등록이면 <see cref="U2fAppId" />)에 묶여 있다.
///     TerminalTests 와 tools/VirtualFido 가 함께 쓴다.
/// </summary>
public sealed class SoftAuthenticator(bool ctap2)
{
    /// <summary>CTAP2 를 안다(거짓이면 U2F 전용 키).</summary>
    public bool Ctap2Supported => ctap2;

    /// <summary>자격 증명을 등록한 RP ID(U2F 시절 등록이면 <see cref="U2fAppId" /> 가 대신한다).</summary>
    public string RpId { get; init; } = "pve.example.com";

    public ECDsa Ecdsa { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public byte[] CredentialId { get; set; } = [];
    public string? U2fAppId { get; init; }
    public int ChainedCommands { get; private set; }
    public int GetResponses { get; private set; }
    private uint _counter = 4;
    private readonly List<byte> _chain = [];
    private byte[] _pending = [];
    private bool _busyOnce = true;

    /// <summary>설정한 PIN(null 이면 PIN 없음).</summary>
    public string? Pin { get; init; }

    /// <summary>PIN 을 지원하는가(설정 안 됐어도 clientPin=false 로 알린다).</summary>
    public bool PinSupported { get; init; } = true;

    /// <summary>항상 사용자 확인을 요구한다(alwaysUv) — PIN 없이 서명을 청하면 PIN_REQUIRED(0x36).</summary>
    public bool AlwaysUv { get; init; }

    /// <summary>지원하는 PIN 프로토콜.</summary>
    public int[] Protocols { get; init; } = [2, 1];

    /// <summary>CTAP 2.1 토큰 명령(0x09, 권한 지정)을 지원한다.</summary>
    public bool TokenWithPermissions { get; init; } = true;

    public int PinRetries { get; set; } = 8;
    public int LastProtocol { get; private set; }
    private readonly ECDiffieHellman _agreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _token = RandomNumberGenerator.GetBytes(32);

    /// <summary>CTAP2 명령(명령 바이트 + CBOR) → 상태 + CBOR. CTAP2 를 모르면 null.</summary>
    public byte[]? Ctap2(byte[] request)
    {
        if (!ctap2) return null;

        switch (request[0])
        {
            case 0x04:
                return GetInfo();
            case 0x06:
                return ClientPinCommand((Dictionary<object, object?>)Cbor.Decode(request.AsSpan(1))!);
            case 0x02:
                break;
            default:
                return [0x01];
        }

        var m = (Dictionary<object, object?>)Cbor.Decode(request.AsSpan(1))!;
        var rpId = (string)m[1L]!;
        var hash = (byte[])m[2L]!;
        var allow = m.TryGetValue(3L, out var l) ? ((List<object?>)l!).Cast<Dictionary<object, object?>>() : [];
        // 자격 증명은 등록한 RP(U2F 시절이면 appid)에만 쓸 수 있다
        if (rpId != (U2fAppId ?? RpId) || !allow.Any(x => ((byte[])x["id"]!).SequenceEqual(CredentialId)))
            return [0x2E];

        byte flags = 0x01;
        if (m.TryGetValue(6L, out var param))
        {
            var protocol = (int)(long)m[7L]!;
            LastProtocol = protocol;
            if (!((byte[])param!).SequenceEqual(ClientPin.Authenticate(protocol, _token, hash))) return [0x33];

            flags |= 0x04; // UV
        }
        else if (AlwaysUv)
        {
            return [0x36];
        }

        if (m.TryGetValue(5L, out var o) && o is Dictionary<object, object?> opts && opts.ContainsKey("uv"))
            return [0x2C]; // 내장 생체 인증 없음 — uv 옵션은 받지 않는다

        var authData = AuthData(rpId, flags);
        var sig = Ecdsa.SignData(authData.Concat(hash).ToArray(), HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var body = new Cbor.Writer().Map(3)
            .Int(1).Map(2).Text("id").Bytes(CredentialId).Text("type").Text("public-key")
            .Int(2).Bytes(authData).Int(3).Bytes(sig).ToArray();
        return [0x00, .. body];
    }

    private byte[] GetInfo()
    {
        var w = new Cbor.Writer().Map(4);
        w.Int(1).Array(2).Text("FIDO_2_0").Text("FIDO_2_1");
        w.Int(3).Bytes(new byte[16]);
        var options = (PinSupported ? 1 : 0) + (TokenWithPermissions ? 1 : 0);
        w.Int(4).Map(options);
        if (PinSupported) w.Text("clientPin").Bool(Pin is not null);
        if (TokenWithPermissions) w.Text("pinUvAuthToken").Bool(true);
        w.Int(6).Array(Protocols.Length);
        foreach (var p in Protocols) w.Int(p);
        return [0x00, .. w.ToArray()];
    }

    private byte[] ClientPinCommand(Dictionary<object, object?> m)
    {
        var protocol = (int)(long)m[1L]!;
        if (!Protocols.Contains(protocol)) return [0x02];

        var sub = (long)m[2L]!;
        switch (sub)
        {
            case 0x01:
                return [0x00, .. new Cbor.Writer().Map(1).Int(3).Int(PinRetries).ToArray()];
            case 0x02:
            {
                var w = new Cbor.Writer().Map(1).Int(1);
                ClientPin.WriteCoseKey(w, _agreement.ExportParameters(false));
                return [0x00, .. w.ToArray()];
            }
            case 0x05 or 0x09:
            {
                if (sub == 0x09 && (!TokenWithPermissions || (long)m[9L]! != 0x02 || m[10L] is not string))
                    return [0x02];
                if (Pin is null) return [0x35];
                if (PinRetries == 0) return [0x32];

                var cose = (Dictionary<object, object?>)m[3L]!;
                using var peer = ECDiffieHellman.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = (byte[])cose[-2L]!, Y = (byte[])cose[-3L]! }
                });
                var shared = ClientPin.Kdf(protocol, _agreement.DeriveRawSecretAgreement(peer.PublicKey));
                var pinHash = ClientPin.Decrypt(protocol, shared, (byte[])m[6L]!);
                if (!pinHash.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(Pin))[..16]))
                {
                    PinRetries--;
                    return [PinRetries == 0 ? (byte)0x32 : (byte)0x31];
                }

                PinRetries = 8;
                var enc = ClientPin.Encrypt(protocol, shared, _token);
                return [0x00, .. new Cbor.Writer().Map(1).Int(2).Bytes(enc).ToArray()];
            }
            default:
                return [0x01];
        }
    }

    /// <summary>U2F authenticate → 데이터 + SW.</summary>
    public byte[] U2f(byte ins, byte p1, byte[] data)
    {
        if (ins != 0x02) return [0x6D, 0x00];

        var clientHash = data[..32];
        var appHash = data[32..64];
        var kh = data[65..(65 + data[64])];
        var app = U2fAppId ?? RpId;
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
