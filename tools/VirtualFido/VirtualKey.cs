namespace ProxmoxClient.Tools.Fido;

/// <summary>
///     가상 USB 보안 키 — <see cref="UhidDevice" /> 위에서 CTAPHID(CTAP 명세 §11.2)를 말하고 CTAP2·U2F 는
///     <see cref="SoftAuthenticator" /> 에 넘긴다. 명령: INIT(채널 배정)·PING(되돌림)·CBOR·MSG(U2F APDU)·CANCEL·WINK,
///     모르는 명령·바쁜 채널·순서 틀림은 ERROR. getAssertion 은 <see cref="TouchDelay" /> 동안 터치를 기다리며
///     KEEPALIVE(UPNEEDED)를 100ms 마다 보내고, 그사이 CANCEL 이 오면 CTAP2_ERR_KEEPALIVE_CANCEL(0x2D)로 답한다.
/// </summary>
internal sealed class VirtualKey : IDisposable
{
    private const int Report = 64;
    private const byte CmdPing = 0x01;
    private const byte CmdMsg = 0x03;
    private const byte CmdInit = 0x06;
    private const byte CmdWink = 0x08;
    private const byte CmdCbor = 0x10;
    private const byte CmdCancel = 0x11;
    private const byte CmdKeepAlive = 0x3B;
    private const byte CmdError = 0x3F;
    private const byte ErrInvalidCmd = 0x01;
    private const byte ErrInvalidSeq = 0x04;
    private const byte ErrChannelBusy = 0x06;
    private const uint Broadcast = 0xFFFFFFFF;

    private readonly UhidDevice _uhid;
    private readonly Lock _lock = new();
    private uint _nextCid = 0x0A000001;

    // 받는 중인 메시지(채널 하나)
    private uint _rxCid;
    private byte _rxCmd;
    private int _rxNeed;
    private byte _rxSeq;
    private List<byte>? _rx;

    // 처리 중인 명령(CBOR·MSG)
    private uint _busyCid;
    private volatile bool _cancel;
    private volatile bool _gone;

    public SoftAuthenticator Auth { get; }

    /// <summary>getAssertion 에서 터치를 기다리는 시간.</summary>
    public TimeSpan TouchDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public int Inits { get; private set; }
    public int Pings { get; private set; }
    public int Cancels { get; private set; }
    public int KeepAlives { get; private set; }
    public List<string> Commands { get; } = [];

    public VirtualKey(SoftAuthenticator auth, string uniq)
    {
        Auth = auth;
        _uhid = new UhidDevice("Virtual FIDO2 Key", uniq, OnOutput);
    }

    /// <summary>/dev/hidrawN(아직 안 생겼으면 null).</summary>
    public string? Hidraw => _uhid.FindHidraw();

    /// <summary>키를 뽑는다.</summary>
    public void Dispose()
    {
        _gone = true;
        _uhid.Dispose();
    }

    private void OnOutput(byte[] f)
    {
        if (f.Length < 5) return;

        var cid = (uint)((f[0] << 24) | (f[1] << 16) | (f[2] << 8) | f[3]);
        lock (_lock)
        {
            if ((f[4] & 0x80) != 0)
            {
                var cmd = (byte)(f[4] & 0x7F);
                var len = f.Length >= 7 ? (f[5] << 8) | f[6] : 0;
                if (cmd == CmdCancel)
                {
                    Cancels++;
                    Log($"CANCEL cid={cid:X8}");
                    if (cid == _busyCid) _cancel = true;
                    return; // CANCEL 에는 답하지 않는다(처리 중인 명령이 0x2D 로 끝난다)
                }

                if (cmd == CmdInit)
                {
                    _rx = null;
                    HandleInit(cid, f.AsSpan(7, Math.Min(len, 8)).ToArray());
                    return;
                }

                if (cid == Broadcast || cid == 0)
                {
                    SendFrames(cid, CmdError, [0x0B]); // ERR_INVALID_CHANNEL
                    return;
                }

                if (_busyCid != 0)
                {
                    SendFrames(cid, CmdError, [ErrChannelBusy]);
                    return;
                }

                _rxCid = cid;
                _rxCmd = cmd;
                _rxNeed = len;
                _rxSeq = 0;
                _rx = [.. f.AsSpan(7, Math.Min(len, Report - 7))];
            }
            else
            {
                if (_rx is null || cid != _rxCid) return;
                if (f[4] != _rxSeq)
                {
                    _rx = null;
                    SendFrames(cid, CmdError, [ErrInvalidSeq]);
                    return;
                }

                _rxSeq++;
                _rx.AddRange(f.AsSpan(5, Math.Min(_rxNeed - _rx.Count, Report - 5)));
            }

            if (_rx.Count < _rxNeed) return;

            var data = _rx.ToArray();
            _rx = null;
            Dispatch(_rxCid, _rxCmd, data);
        }
    }

    private void HandleInit(uint cid, byte[] nonce)
    {
        Inits++;
        var assigned = cid == Broadcast ? _nextCid++ : cid;
        Log($"INIT cid={cid:X8} -> {assigned:X8}");
        // nonce(8) ‖ 채널(4) ‖ CTAPHID 판(2) ‖ 장치 판(3) ‖ 능력(WINK 0x01, CBOR 0x04, NMSG 0x08)
        byte caps = 0x01;
        if (Auth.Ctap2Supported) caps |= 0x04;
        SendFrames(cid, CmdInit,
        [
            .. nonce, (byte)(assigned >> 24), (byte)(assigned >> 16), (byte)(assigned >> 8), (byte)assigned,
            2, 1, 0, 0, caps
        ]);
    }

    private void Dispatch(uint cid, byte cmd, byte[] data)
    {
        switch (cmd)
        {
            case CmdPing:
                Pings++;
                Log($"PING {data.Length} bytes");
                SendFrames(cid, CmdPing, data);
                return;
            case CmdWink:
                SendFrames(cid, CmdWink, []);
                return;
            case CmdCbor when Auth.Ctap2Supported && data.Length > 0:
            case CmdMsg when data.Length >= 4:
                _busyCid = cid;
                _cancel = false;
                Commands.Add(cmd == CmdCbor ? $"CBOR {data[0]:X2}" : $"MSG {data[1]:X2}/{data[2]:X2}");
                Log(Commands[^1]);
                _ = Task.Run(() => Process(cid, cmd, data));
                return;
            default:
                SendFrames(cid, CmdError, [ErrInvalidCmd]);
                return;
        }
    }

    private async Task Process(uint cid, byte cmd, byte[] data)
    {
        byte[] reply;
        try
        {
            if (cmd == CmdCbor)
            {
                if (data[0] == 0x02 && !await WaitTouchAsync(cid))
                {
                    if (_gone) return;

                    reply = [0x2D]; // CTAP2_ERR_KEEPALIVE_CANCEL
                    Log("getAssertion cancelled");
                }
                else
                {
                    lock (Auth) reply = Auth.Ctap2(data) ?? [0x01];
                }
            }
            else
            {
                reply = Apdu(data);
            }
        }
        catch (Exception ex)
        {
            Log("process failed: " + ex);
            reply = cmd == CmdCbor ? [0x7F] : [0x6F, 0x00];
        }

        lock (_lock)
        {
            _busyCid = 0;
            if (!_gone) SendFrames(cid, cmd, reply);
        }
    }

    /// <summary>터치를 기다린다(KEEPALIVE UPNEEDED). CANCEL·뽑힘이면 거짓.</summary>
    private async Task<bool> WaitTouchAsync(uint cid)
    {
        var until = DateTime.UtcNow + TouchDelay;
        while (DateTime.UtcNow < until)
        {
            if (_cancel || _gone) return false;

            lock (_lock)
            {
                KeepAlives++;
                SendFrames(cid, CmdKeepAlive, [0x02]);
            }

            await Task.Delay(100);
        }

        return !_cancel && !_gone;
    }

    /// <summary>U2F APDU(확장 길이 00 INS P1 P2 00 Lc Lc 데이터 [Le Le] 또는 짧은 길이) → 데이터 + SW.</summary>
    private byte[] Apdu(byte[] a)
    {
        var (ins, p1) = (a[1], a[2]);
        byte[] body;
        if (a.Length >= 7 && a[4] == 0)
            body = a.AsSpan(7, Math.Min((a[5] << 8) | a[6], a.Length - 7)).ToArray();
        else if (a.Length > 5)
            body = a.AsSpan(5, Math.Min(a[4], a.Length - 5)).ToArray();
        else
            body = [];

        if (ins == 0x03) return [.. "U2F_V2"u8, 0x90, 0x00]; // VERSION
        if (ins == 0x02 && body.Length < 65) return [0x67, 0x00]; // 길이 틀림

        lock (Auth) return Auth.U2f(ins, p1, body);
    }

    private void SendFrames(uint cid, byte cmd, byte[] payload)
    {
        if (_gone) return;

        var f = new byte[Report];
        Cid(f, cid);
        f[4] = (byte)(0x80 | cmd);
        f[5] = (byte)(payload.Length >> 8);
        f[6] = (byte)payload.Length;
        var sent = Math.Min(payload.Length, Report - 7);
        Array.Copy(payload, 0, f, 7, sent);
        _uhid.SendInput(f);
        byte seq = 0;
        while (sent < payload.Length)
        {
            f = new byte[Report];
            Cid(f, cid);
            f[4] = seq++;
            var n = Math.Min(payload.Length - sent, Report - 5);
            Array.Copy(payload, sent, f, 5, n);
            sent += n;
            _uhid.SendInput(f);
        }
    }

    private static void Cid(byte[] f, uint cid)
    {
        f[0] = (byte)(cid >> 24);
        f[1] = (byte)(cid >> 16);
        f[2] = (byte)(cid >> 8);
        f[3] = (byte)cid;
    }

    private void Log(string message)
    {
        Console.Error.WriteLine($"  [vkey {_uhid.Uniq}] {message}");
    }
}
