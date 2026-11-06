using System.Security.Cryptography;
using System.Text;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Api;

/// <summary>보안 키 PIN 을 물을 때 넘기는 정보 — 남은 시도 횟수(모르면 null)와 바로 전 시도의 오류(처음이면 null).</summary>
public sealed record PinPromptInfo(int? RetriesLeft, string? Error);

/// <summary>보안 키 PIN 을 묻는다. 취소하면 null.</summary>
public delegate Task<string?> PinPrompt(PinPromptInfo info, CancellationToken ct);

/// <summary>authenticatorGetInfo 에서 쓰는 부분.</summary>
/// <param name="ClientPinSet">PIN 이 설정돼 있다(options.clientPin = true).</param>
/// <param name="ClientPinSupported">PIN 을 지원한다(clientPin 이 있음, 참·거짓 무관).</param>
/// <param name="BuiltInUv">지문 등 내장 사용자 확인이 설정돼 있다(options.uv = true).</param>
/// <param name="PinUvAuthToken">CTAP 2.1 토큰 명령(0x09, 권한 지정)을 지원한다.</param>
/// <param name="PinProtocols">지원하는 PIN 프로토콜(2·1). 없으면 [1].</param>
public sealed record AuthenticatorInfo(
    bool ClientPinSet,
    bool ClientPinSupported,
    bool BuiltInUv,
    bool PinUvAuthToken,
    IReadOnlyList<int> PinProtocols);

/// <summary>얻은 PIN 토큰 — getAssertion 의 pinUvAuthParam 을 만든다.</summary>
public sealed record PinToken(int Protocol, byte[] Token)
{
    /// <summary>pinUvAuthParam = HMAC-SHA-256(토큰, clientDataHash) — 프로토콜 1 은 앞 16바이트.</summary>
    public byte[] Authenticate(byte[] message)
    {
        return ClientPin.Authenticate(Protocol, Token, message);
    }
}

/// <summary>
///     CTAP2 authenticatorClientPIN(0x06) — PIN 으로 사용자 확인 토큰을 받는다(CTAP 2.1 §6.5).
///     키 합의(ECDH P-256)로 공유 비밀을 만들고(프로토콜 1: SHA-256(Z), 프로토콜 2: HKDF-SHA-256 로 HMAC 키 ‖ AES 키),
///     PIN 의 SHA-256 앞 16바이트를 AES-256-CBC 로 암호화해 보낸다. CTAP 2.1 키는 권한(ga = getAssertion)·rpId 를 지정한
///     토큰(0x09)을, 2.0 키는 getPinToken(0x05)을 쓴다.
/// </summary>
public static class ClientPin
{
    private const byte CmdGetInfo = 0x04;
    private const byte CmdClientPin = 0x06;
    private const int SubGetRetries = 0x01;
    private const int SubGetKeyAgreement = 0x02;
    private const int SubGetPinToken = 0x05;
    private const int SubGetTokenWithPermissions = 0x09;
    private const int PermissionGetAssertion = 0x02;

    /// <summary>authenticatorGetInfo. CTAP2 를 모르는 키면 null.</summary>
    public static async Task<AuthenticatorInfo?> GetInfoAsync(ICtapTransport transport, CancellationToken ct)
    {
        var r = await transport.SendCtap2Async([CmdGetInfo], ct).ConfigureAwait(false);
        if (r is not { Length: > 1 } || r[0] != 0) return null;
        if (Cbor.Decode(r.AsSpan(1)) is not Dictionary<object, object?> map) return null;

        var options = map.TryGetValue(4L, out var o) && o is Dictionary<object, object?> d ? d : [];
        var protocols = map.TryGetValue(6L, out var p) && p is List<object?> list
            ? list.OfType<long>().Select(v => (int)v).Where(v => v is 1 or 2).ToList()
            : [];
        return new AuthenticatorInfo(
            options.TryGetValue("clientPin", out var cp) && cp is true,
            options.ContainsKey("clientPin"),
            options.TryGetValue("uv", out var uv) && uv is true,
            options.TryGetValue("pinUvAuthToken", out var tok) && tok is true,
            protocols.Count > 0 ? protocols : [1]);
    }

    /// <summary>PIN 을 물어 토큰을 받는다. 틀리면 남은 횟수와 함께 다시 묻는다. 취소하면 <see cref="OperationCanceledException" />.</summary>
    public static async Task<PinToken> ObtainTokenAsync(ICtapTransport transport, AuthenticatorInfo info, string rpId,
        PinPrompt prompt, CancellationToken ct)
    {
        var protocol = info.PinProtocols.Contains(2) ? 2 : 1;
        string? error = null;
        while (true)
        {
            var retries = await GetRetriesAsync(transport, protocol, ct).ConfigureAwait(false);
            if (retries == 0) throw new WebAuthnException(Res.T("WebAuthn_PinBlocked"));

            var pin = await prompt(new PinPromptInfo(retries, error), ct).ConfigureAwait(false);
            if (pin is null) throw new OperationCanceledException();

            pin = pin.Normalize(NormalizationForm.FormC);
            if (Encoding.UTF8.GetByteCount(pin) < 4)
            {
                error = Res.T("WebAuthn_PinTooShort");
                continue;
            }

            var (platformKey, shared) = await KeyAgreementAsync(transport, protocol, ct).ConfigureAwait(false);
            var pinHashEnc = Encrypt(protocol, shared, SHA256.HashData(Encoding.UTF8.GetBytes(pin))[..16]);
            var w = new Cbor.Writer();
            if (info.PinUvAuthToken)
            {
                w.Map(6).Int(1).Int(protocol).Int(2).Int(SubGetTokenWithPermissions);
                w.Int(3);
                WriteCoseKey(w, platformKey);
                w.Int(6).Bytes(pinHashEnc).Int(9).Int(PermissionGetAssertion).Int(10).Text(rpId);
            }
            else
            {
                w.Map(4).Int(1).Int(protocol).Int(2).Int(SubGetPinToken);
                w.Int(3);
                WriteCoseKey(w, platformKey);
                w.Int(6).Bytes(pinHashEnc);
            }

            var r = await SendAsync(transport, w.ToArray(), ct).ConfigureAwait(false);
            switch (r[0])
            {
                case 0x00:
                    if (Cbor.Decode(r.AsSpan(1)) is not Dictionary<object, object?> m || m.GetValueOrDefault(2L) is not byte[] enc)
                        throw new WebAuthnException(Res.T("WebAuthn_DeviceError", "pinUvAuthToken"));

                    return new PinToken(protocol, Decrypt(protocol, shared, enc));
                case 0x31: // PIN_INVALID
                case 0x37: // PIN_POLICY_VIOLATION
                    error = Res.T("WebAuthn_PinInvalid");
                    continue;
                case 0x32:
                    throw new WebAuthnException(Res.T("WebAuthn_PinBlocked"));
                case 0x34:
                    throw new WebAuthnException(Res.T("WebAuthn_PinAuthBlocked"));
                default:
                    throw new WebAuthnException(Res.T("WebAuthn_DeviceError", $"clientPIN 0x{r[0]:X2}"));
            }
        }
    }

    /// <summary>남은 PIN 시도 횟수(getPINRetries). 키가 알려 주지 않으면 null.</summary>
    public static async Task<int?> GetRetriesAsync(ICtapTransport transport, int protocol, CancellationToken ct)
    {
        var w = new Cbor.Writer().Map(2).Int(1).Int(protocol).Int(2).Int(SubGetRetries);
        var r = await SendAsync(transport, w.ToArray(), ct).ConfigureAwait(false);
        if (r[0] != 0) return null;

        return Cbor.Decode(r.AsSpan(1)) is Dictionary<object, object?> m && m.GetValueOrDefault(3L) is long n ? (int)n : null;
    }

    /// <summary>키의 공개 키를 받아 이쪽 임시 키와 ECDH — (이쪽 공개 키, 공유 비밀).</summary>
    private static async Task<(ECParameters PlatformKey, byte[] Shared)> KeyAgreementAsync(ICtapTransport transport,
        int protocol, CancellationToken ct)
    {
        var w = new Cbor.Writer().Map(2).Int(1).Int(protocol).Int(2).Int(SubGetKeyAgreement);
        var r = await SendAsync(transport, w.ToArray(), ct).ConfigureAwait(false);
        if (r[0] != 0 || Cbor.Decode(r.AsSpan(1)) is not Dictionary<object, object?> m ||
            m.GetValueOrDefault(1L) is not Dictionary<object, object?> cose ||
            cose.GetValueOrDefault(-2L) is not byte[] x || cose.GetValueOrDefault(-3L) is not byte[] y)
            throw new WebAuthnException(Res.T("WebAuthn_DeviceError", $"keyAgreement 0x{r[0]:X2}"));

        return Encapsulate(protocol, x, y);
    }

    /// <summary>
    ///     임시 키를 만들어 상대 공개 키(x, y)와 ECDH. 공유 비밀: 프로토콜 1 = SHA-256(Z), 프로토콜 2 = HKDF(Z, "CTAP2 HMAC key")
    ///     ‖ HKDF(Z, "CTAP2 AES key")(소금은 32바이트 0). 테스트용 인증기도 같은 식을 쓴다.
    /// </summary>
    public static (ECParameters PlatformKey, byte[] Shared) Encapsulate(int protocol, byte[] peerX, byte[] peerY)
    {
        using var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = peerX, Y = peerY }
        });
        var z = mine.DeriveRawSecretAgreement(peer.PublicKey);
        return (mine.ExportParameters(false), Kdf(protocol, z));
    }

    /// <summary>Z(ECDH x 좌표) → 공유 비밀.</summary>
    public static byte[] Kdf(int protocol, byte[] z)
    {
        if (protocol == 1) return SHA256.HashData(z);

        var salt = new byte[32];
        return
        [
            .. HKDF.DeriveKey(HashAlgorithmName.SHA256, z, 32, salt, "CTAP2 HMAC key"u8.ToArray()),
            .. HKDF.DeriveKey(HashAlgorithmName.SHA256, z, 32, salt, "CTAP2 AES key"u8.ToArray())
        ];
    }

    /// <summary>AES-256-CBC(패딩 없음) — 프로토콜 1: IV 0, 프로토콜 2: 임의 IV 를 앞에 붙인다(AES 키는 뒤 32바이트).</summary>
    public static byte[] Encrypt(int protocol, byte[] shared, byte[] plain)
    {
        using var aes = Aes.Create();
        if (protocol == 1)
        {
            aes.Key = shared;
            return aes.EncryptCbc(plain, new byte[16], PaddingMode.None);
        }

        aes.Key = shared[32..];
        var iv = RandomNumberGenerator.GetBytes(16);
        return [.. iv, .. aes.EncryptCbc(plain, iv, PaddingMode.None)];
    }

    public static byte[] Decrypt(int protocol, byte[] shared, byte[] data)
    {
        using var aes = Aes.Create();
        if (protocol == 1)
        {
            aes.Key = shared;
            return aes.DecryptCbc(data, new byte[16], PaddingMode.None);
        }

        if (data.Length < 32) throw new WebAuthnException(Res.T("WebAuthn_DeviceError", "decrypt"));

        aes.Key = shared[32..];
        return aes.DecryptCbc(data[16..], data[..16], PaddingMode.None);
    }

    /// <summary>HMAC-SHA-256 — 프로토콜 1 은 앞 16바이트, 프로토콜 2 는 32바이트(키가 공유 비밀이면 HMAC 키 부분만).</summary>
    public static byte[] Authenticate(int protocol, byte[] key, byte[] message)
    {
        if (protocol == 1) return HMACSHA256.HashData(key, message)[..16];

        return HMACSHA256.HashData(key.Length > 32 ? key[..32] : key, message);
    }

    /// <summary>COSE_Key(EC2, ECDH-ES+HKDF-256 = -25, P-256) — 키 순서 1, 3, -1, -2, -3.</summary>
    public static void WriteCoseKey(Cbor.Writer w, ECParameters key)
    {
        w.Map(5).Int(1).Int(2).Int(3).Int(-25).Int(-1).Int(1).Int(-2).Bytes(key.Q.X).Int(-3).Bytes(key.Q.Y);
    }

    private static async Task<byte[]> SendAsync(ICtapTransport transport, byte[] cbor, CancellationToken ct)
    {
        var r = await transport.SendCtap2Async([CmdClientPin, .. cbor], ct).ConfigureAwait(false);
        return r is { Length: > 0 } ? r : throw new WebAuthnException(Res.T("WebAuthn_DeviceError", "clientPIN"));
    }
}
