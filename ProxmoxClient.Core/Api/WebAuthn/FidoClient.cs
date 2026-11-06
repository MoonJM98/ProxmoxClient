using System.Security.Cryptography;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Api;

/// <summary>보안 키 서명을 받지 못했다 — <see cref="Exception.Message" /> 는 화면에 그대로 보일 문구.</summary>
public sealed class WebAuthnException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>제한 시간 안에 키가 답하지 않았다.</summary>
    public static WebAuthnException Timeout()
    {
        return new WebAuthnException(Res.T("WebAuthn_Timeout"));
    }

    /// <summary>닿은 것이 FIDO 보안 키가 아니다.</summary>
    public static WebAuthnException NotFidoKey()
    {
        return new WebAuthnException(Res.T("WebAuthn_NotFidoKey"));
    }
}

/// <summary>
///     보안 키와 주고받는 통로(NFC·USB HID). <see cref="FidoClient" /> 가 CTAP2 를 먼저, 안 되면 U2F(CTAP1)를 쓴다.
/// </summary>
public interface ICtapTransport
{
    /// <summary>CTAP2 명령(명령 바이트 + CBOR) → 상태 바이트 + CBOR. 키가 CTAP2 를 모르면 null.</summary>
    Task<byte[]?> SendCtap2Async(byte[] request, CancellationToken ct);

    /// <summary>U2F 명령(APDU 의 INS·P1·P2·데이터) → 응답 데이터 + SW1 SW2.</summary>
    Task<byte[]> SendU2fAsync(byte ins, byte p1, byte p2, byte[] data, CancellationToken ct);
}

/// <summary>
///     보안 키에 직접 서명을 받는다(WebAuthn 클라이언트 역할) — 운영체제 WebAuthn API 를 못 쓰는 곳(Android·Linux)용.
///     clientDataJSON 을 직접 만들므로 origin 을 PVE 주소로 둘 수 있다(브라우저·WebView 의 출처 제한이 없다).
///     CTAP2 authenticatorGetAssertion(0x02)을 먼저 보내고, CTAP2 를 모르는 키이거나 U2F 시절 등록(appid)이면
///     U2F authenticate 로 서명을 받아 WebAuthn 인증기 데이터 모양으로 바꾼다.
///     PIN·지문 확인(userVerification=required)은 키에 내장 생체 인증이 있을 때만 된다(PIN 입력은 지원하지 않음).
/// </summary>
public static class FidoClient
{
    private const byte CmdGetAssertion = 0x02;
    private const byte U2fAuthenticate = 0x02;
    private const byte U2fEnforcePresence = 0x03;
    private const byte U2fCheckOnly = 0x07;

    /// <summary>서명을 받는다. 키에 맞는 자격 증명이 없거나 거부되면 <see cref="WebAuthnException" />.</summary>
    public static async Task<WebAuthnAssertion> GetAssertionAsync(ICtapTransport transport, WebAuthnRequest request,
        Uri origin, CancellationToken ct)
    {
        var clientData = request.BuildClientDataJson(origin);
        var clientDataHash = SHA256.HashData(clientData);
        var rpId = request.EffectiveRpId(origin);

        var response = await transport.SendCtap2Async(BuildGetAssertion(rpId, clientDataHash, request), ct)
            .ConfigureAwait(false);
        if (response is { Length: > 0 })
        {
            var status = response[0];
            if (status == 0x00) return ParseGetAssertion(response.AsSpan(1), clientData, request);
            // 0x2E 자격 증명 없음 — U2F 시절(appid) 등록이면 U2F 로 다시. 0x01 명령 모름 — U2F 로
            if (!(status == 0x2E && request.AppId is not null) && status != 0x01) throw StatusError(status);
        }

        return await U2fAssertionAsync(transport, request, rpId, clientData, clientDataHash, ct).ConfigureAwait(false);
    }

    /// <summary>authenticatorGetAssertion 요청 — 명령 바이트 + CBOR 맵(키는 CTAP2 정규 순서).</summary>
    public static byte[] BuildGetAssertion(string rpId, byte[] clientDataHash, WebAuthnRequest request)
    {
        var uvRequired = request.UserVerification == "required";
        var entries = 2 + (request.AllowCredentials.Count > 0 ? 1 : 0) + (uvRequired ? 1 : 0);
        var w = new Cbor.Writer().Map(entries);
        w.Int(1).Text(rpId);
        w.Int(2).Bytes(clientDataHash);
        if (request.AllowCredentials.Count > 0)
        {
            w.Int(3).Array(request.AllowCredentials.Count);
            foreach (var id in request.AllowCredentials)
                w.Map(2).Text("id").Bytes(id).Text("type").Text("public-key");
        }

        if (uvRequired) w.Int(5).Map(1).Text("uv").Bool(true);
        var cbor = w.ToArray();
        var result = new byte[cbor.Length + 1];
        result[0] = CmdGetAssertion;
        cbor.CopyTo(result, 1);
        return result;
    }

    private static WebAuthnAssertion ParseGetAssertion(ReadOnlySpan<byte> cbor, byte[] clientData,
        WebAuthnRequest request)
    {
        if (Cbor.Decode(cbor) is not Dictionary<object, object?> map ||
            !map.TryGetValue(2L, out var ad) || ad is not byte[] authData ||
            !map.TryGetValue(3L, out var sg) || sg is not byte[] signature)
            throw new WebAuthnException(Res.T("WebAuthn_DeviceError", "CBOR"));

        byte[]? credentialId = null;
        if (map.TryGetValue(1L, out var c) && c is Dictionary<object, object?> cred &&
            cred.TryGetValue("id", out var cid))
            credentialId = cid as byte[];
        // 허용 목록이 하나면 키가 자격 증명을 생략할 수 있다
        credentialId ??= request.AllowCredentials.Count == 1
            ? request.AllowCredentials[0]
            : throw new WebAuthnException(Res.T("WebAuthn_DeviceError", "credential"));

        byte[]? userHandle = null;
        if (map.TryGetValue(4L, out var u) && u is Dictionary<object, object?> user &&
            user.TryGetValue("id", out var uid))
            userHandle = uid as byte[];

        return new WebAuthnAssertion(credentialId, authData, clientData, signature, userHandle);
    }

    /// <summary>
    ///     U2F authenticate — 자격 증명마다 먼저 "확인만"(P1=7)으로 이 키의 것인지 보고, 맞으면 터치를 기다려 서명받는다.
    ///     인증기 데이터 = SHA-256(rpId 또는 appid) ‖ 플래그(사용자 확인) ‖ 서명 횟수(4바이트) — WebAuthn 명세 §6.1.2 와 같다.
    /// </summary>
    private static async Task<WebAuthnAssertion> U2fAssertionAsync(ICtapTransport transport, WebAuthnRequest request,
        string rpId, byte[] clientData, byte[] clientDataHash, CancellationToken ct)
    {
        if (request.AllowCredentials.Count == 0) throw new WebAuthnException(Res.T("WebAuthn_NoCredential"));

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(request.TimeoutMs, 10000));
        var apps = new List<string> { rpId };
        if (request.AppId is { } appId && appId != rpId) apps.Add(appId);
        foreach (var app in apps)
        {
            var appHash = WebAuthnRequest.RpIdHash(app);
            foreach (var keyHandle in request.AllowCredentials.Where(k => k.Length is > 0 and <= 255))
            {
                var data = new byte[65 + keyHandle.Length];
                clientDataHash.CopyTo(data, 0);
                appHash.CopyTo(data, 32);
                data[64] = (byte)keyHandle.Length;
                keyHandle.CopyTo(data, 65);

                var check = await transport.SendU2fAsync(U2fAuthenticate, U2fCheckOnly, 0, data, ct)
                    .ConfigureAwait(false);
                var checkSw = Sw(check);
                // 6985 = 이 키의 자격 증명(터치 필요), 9000 도 일부 키가 그렇게 답한다. 6A80 = 다른 키의 것
                if (checkSw is not (0x6985 or 0x9000)) continue;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var signed = await transport.SendU2fAsync(U2fAuthenticate, U2fEnforcePresence, 0, data, ct)
                        .ConfigureAwait(false);
                    var sw = Sw(signed);
                    if (sw == 0x9000 && signed.Length > 2 + 5)
                    {
                        var body = signed.AsSpan(0, signed.Length - 2);
                        var authData = new byte[37];
                        appHash.CopyTo(authData, 0);
                        authData[32] = body[0]; // 사용자 확인 플래그(UP)
                        body.Slice(1, 4).CopyTo(authData.AsSpan(33));
                        return new WebAuthnAssertion(keyHandle, authData, clientData, body[5..].ToArray());
                    }

                    if (sw != 0x6985) throw new WebAuthnException(Res.T("WebAuthn_DeviceError", $"U2F {sw:X4}"));
                    if (DateTime.UtcNow > deadline) throw new WebAuthnException(Res.T("WebAuthn_Timeout"));

                    await Task.Delay(200, ct).ConfigureAwait(false); // 터치를 기다린다
                }
            }
        }

        throw new WebAuthnException(Res.T("WebAuthn_NoCredential"));
    }

    private static int Sw(byte[] response)
    {
        return response.Length < 2 ? 0 : (response[^2] << 8) | response[^1];
    }

    private static WebAuthnException StatusError(byte status)
    {
        return status switch
        {
            0x2E => new WebAuthnException(Res.T("WebAuthn_NoCredential")),
            0x27 => new WebAuthnException(Res.T("WebAuthn_Denied")),
            0x2F or 0x3A => new WebAuthnException(Res.T("WebAuthn_Timeout")),
            0x2B or 0x2C or 0x31 or 0x35 or 0x36 => new WebAuthnException(Res.T("WebAuthn_PinRequired")),
            _ => new WebAuthnException(Res.T("WebAuthn_DeviceError", $"CTAP2 0x{status:X2}"))
        };
    }
}
