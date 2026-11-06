using System.Security.Cryptography;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     NFC 보안 키(ISO 7816-4 APDU) — CTAP 명세 §11.3. FIDO 애플릿을 고른 뒤 CTAP2 는 NFCCTAP_MSG(80 10),
///     U2F 는 일반 APDU 로 보낸다. 긴 명령은 짧은 APDU 이어 보내기(CLA|0x10), 긴 응답은 61xx → GET RESPONSE 로 받는다.
///     <paramref name="transceive" /> 는 플랫폼의 APDU 송수신(Android IsoDep.Transceive).
/// </summary>
public sealed class NfcCtapTransport(Func<byte[], CancellationToken, Task<byte[]>> transceive) : ICtapTransport
{
    private static readonly byte[] FidoAid = [0xA0, 0x00, 0x00, 0x06, 0x47, 0x2F, 0x00, 0x01];

    /// <summary>FIDO 애플릿을 고른다. 응답은 "U2F_V2" 또는 "FIDO_2_0". 보안 키가 아니면 <see cref="WebAuthnException" />.</summary>
    public async Task<string> SelectAsync(CancellationToken ct)
    {
        var r = await SendApduAsync(0x00, 0xA4, 0x04, 0x00, FidoAid, ct).ConfigureAwait(false);
        if (Sw(r) != 0x9000) throw new WebAuthnException(Res.T("WebAuthn_NotFidoKey"));

        return System.Text.Encoding.ASCII.GetString(r, 0, r.Length - 2);
    }

    public async Task<byte[]?> SendCtap2Async(byte[] request, CancellationToken ct)
    {
        var r = await SendApduAsync(0x80, 0x10, 0x00, 0x00, request, ct).ConfigureAwait(false);
        // 9100 = 처리 중(터치·지문 대기) — NFCCTAP_GETRESPONSE 로 끝날 때까지 묻는다
        while (Sw(r) == 0x9100)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            r = await SendApduAsync(0x80, 0x11, 0x00, 0x00, [], ct).ConfigureAwait(false);
        }

        var sw = Sw(r);
        if (sw is 0x6D00 or 0x6E00) return null; // CTAP2 를 모르는 U2F 전용 키
        if (sw != 0x9000) throw new WebAuthnException(Res.T("WebAuthn_DeviceError", $"NFC {sw:X4}"));

        return r[..^2];
    }

    public Task<byte[]> SendU2fAsync(byte ins, byte p1, byte p2, byte[] data, CancellationToken ct)
    {
        return SendApduAsync(0x00, ins, p1, p2, data, ct);
    }

    /// <summary>짧은 APDU 로 보낸다(255바이트가 넘으면 이어 보내기). 응답 = 데이터 + SW1 SW2.</summary>
    private async Task<byte[]> SendApduAsync(byte cla, byte ins, byte p1, byte p2, byte[] data, CancellationToken ct)
    {
        var offset = 0;
        byte[] response;
        while (true)
        {
            var chunk = Math.Min(255, data.Length - offset);
            var last = offset + chunk >= data.Length;
            var apdu = new List<byte> { last ? cla : (byte)(cla | 0x10), ins, p1, p2 };
            if (chunk > 0)
            {
                apdu.Add((byte)chunk);
                apdu.AddRange(data.AsSpan(offset, chunk).ToArray());
            }

            apdu.Add(0x00); // Le — 최대 256
            response = await transceive(apdu.ToArray(), ct).ConfigureAwait(false);
            offset += chunk;
            if (last) break;
            if (Sw(response) != 0x9000) return response;
        }

        // 61xx — 남은 응답을 GET RESPONSE 로 받는다
        var body = new List<byte>(response.AsSpan(0, Math.Max(0, response.Length - 2)).ToArray());
        while (response.Length >= 2 && response[^2] == 0x61)
        {
            ct.ThrowIfCancellationRequested();
            response = await transceive([0x00, 0xC0, 0x00, 0x00, response[^1]], ct).ConfigureAwait(false);
            body.AddRange(response.AsSpan(0, Math.Max(0, response.Length - 2)).ToArray());
        }

        if (response.Length < 2) throw new WebAuthnException(Res.T("WebAuthn_DeviceError", "APDU"));

        body.Add(response[^2]);
        body.Add(response[^1]);
        return body.ToArray();
    }

    private static int Sw(byte[] r)
    {
        return r.Length < 2 ? 0 : (r[^2] << 8) | r[^1];
    }
}

/// <summary>
///     USB 보안 키(CTAPHID, CTAP 명세 §11.2) — 64바이트 보고서로 채널을 열고(INIT) CBOR(0x10)·MSG(0x03) 명령을 보낸다.
///     기다리는 동안 오는 KEEPALIVE 는 건너뛴다. <paramref name="write" /> 는 64바이트 보고서 하나를 보내고
///     <paramref name="read" /> 는 하나를 받는다(Linux hidraw · Android USB 인터럽트 끝점).
/// </summary>
public sealed class HidCtapTransport(
    Func<byte[], CancellationToken, Task> write,
    Func<CancellationToken, Task<byte[]>> read) : ICtapTransport
{
    public const int ReportSize = 64;
    private const byte CmdMsg = 0x03;
    private const byte CmdCbor = 0x10;
    private const byte CmdInit = 0x06;
    private const byte CmdCancel = 0x11;
    private const byte CmdKeepAlive = 0x3B;
    private const byte CmdError = 0x3F;

    private uint _cid = 0xFFFFFFFF;
    private bool _cbor;

    /// <summary>채널을 연다(INIT). 키가 CBOR(CTAP2)를 지원하는지도 여기서 안다.</summary>
    public async Task OpenAsync(CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(8);
        _cid = 0xFFFFFFFF;
        while (true)
        {
            var (cmd, payload) = await TransactAsync(CmdInit, nonce, ct).ConfigureAwait(false);
            if (cmd != CmdInit || payload.Length < 17) throw new WebAuthnException(Res.T("WebAuthn_DeviceError", "HID INIT"));
            if (!payload.AsSpan(0, 8).SequenceEqual(nonce)) continue; // 다른 프로그램의 INIT 응답

            _cid = (uint)((payload[8] << 24) | (payload[9] << 16) | (payload[10] << 8) | payload[11]);
            _cbor = (payload[16] & 0x04) != 0;
            return;
        }
    }

    public async Task<byte[]?> SendCtap2Async(byte[] request, CancellationToken ct)
    {
        if (!_cbor) return null;

        var (cmd, payload) = await TransactAsync(CmdCbor, request, ct).ConfigureAwait(false);
        if (cmd == CmdError) return payload.Length > 0 && payload[0] == 0x01 ? null : throw HidError(payload);

        return payload;
    }

    public async Task<byte[]> SendU2fAsync(byte ins, byte p1, byte p2, byte[] data, CancellationToken ct)
    {
        // 확장 길이 APDU: 00 INS P1 P2 00 Lc(2) 데이터 Le(00 00)
        var apdu = new byte[7 + data.Length + 2];
        apdu[1] = ins;
        apdu[2] = p1;
        apdu[3] = p2;
        apdu[5] = (byte)(data.Length >> 8);
        apdu[6] = (byte)data.Length;
        data.CopyTo(apdu, 7);
        var (cmd, payload) = await TransactAsync(CmdMsg, apdu, ct).ConfigureAwait(false);
        if (cmd == CmdError) throw HidError(payload);

        return payload;
    }

    private static WebAuthnException HidError(byte[] payload)
    {
        return new WebAuthnException(Res.T("WebAuthn_DeviceError", $"HID 0x{(payload.Length > 0 ? payload[0] : 0):X2}"));
    }

    /// <summary>명령 하나를 보내고 응답을 받는다(이어진 패킷을 합친다). 취소되면 CANCEL 을 보낸다.</summary>
    private async Task<(byte Cmd, byte[] Payload)> TransactAsync(byte cmd, byte[] data, CancellationToken ct)
    {
        await SendFramesAsync(cmd, data, ct).ConfigureAwait(false);
        await using var cancelReg = ct.Register(() =>
        {
            try
            {
                _ = SendFramesAsync(CmdCancel, [], CancellationToken.None);
            }
            catch
            {
                // 이미 닫힌 장치
            }
        });

        while (true)
        {
            var frame = await read(ct).ConfigureAwait(false);
            if (frame.Length < 7 || ReadCid(frame) != _cid || (frame[4] & 0x80) == 0) continue;

            var rcmd = (byte)(frame[4] & 0x7F);
            if (rcmd == CmdKeepAlive) continue; // 터치 대기 중

            var total = (frame[5] << 8) | frame[6];
            var payload = new byte[total];
            var got = Math.Min(total, frame.Length - 7);
            Array.Copy(frame, 7, payload, 0, got);
            byte seq = 0;
            while (got < total)
            {
                var cont = await read(ct).ConfigureAwait(false);
                if (cont.Length < 5 || ReadCid(cont) != _cid) continue;
                if (cont[4] != seq) throw new WebAuthnException(Res.T("WebAuthn_DeviceError", "HID SEQ"));

                var n = Math.Min(total - got, cont.Length - 5);
                Array.Copy(cont, 5, payload, got, n);
                got += n;
                seq++;
            }

            return (rcmd, payload);
        }
    }

    private async Task SendFramesAsync(byte cmd, byte[] data, CancellationToken ct)
    {
        var frame = new byte[ReportSize];
        WriteCid(frame);
        frame[4] = (byte)(0x80 | cmd);
        frame[5] = (byte)(data.Length >> 8);
        frame[6] = (byte)data.Length;
        var sent = Math.Min(data.Length, ReportSize - 7);
        Array.Copy(data, 0, frame, 7, sent);
        await write(frame, ct).ConfigureAwait(false);
        byte seq = 0;
        while (sent < data.Length)
        {
            frame = new byte[ReportSize];
            WriteCid(frame);
            frame[4] = seq++;
            var n = Math.Min(data.Length - sent, ReportSize - 5);
            Array.Copy(data, sent, frame, 5, n);
            sent += n;
            await write(frame, ct).ConfigureAwait(false);
        }
    }

    private void WriteCid(byte[] frame)
    {
        frame[0] = (byte)(_cid >> 24);
        frame[1] = (byte)(_cid >> 16);
        frame[2] = (byte)(_cid >> 8);
        frame[3] = (byte)_cid;
    }

    private static uint ReadCid(byte[] frame)
    {
        return (uint)((frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3]);
    }
}
