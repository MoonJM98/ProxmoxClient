using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.Core.Tests;

/// <summary>
///     RfbClient 를 가짜 RFB 서버(같은 프로세스의 TCP 루프백)에 붙여, 핸드셰이크와 인코딩별 사각형(Raw·Zlib·Tight·CopyRect)·
///     커서 모양·의사 rect 를 작은 고정 데이터로 해석해 본다. 기대 화면은 테스트가 따로 그린 모델과 비교한다.
/// </summary>
public class RfbClientTests
{
    private const int Width = 16;
    private const int Height = 8;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task HandshakeWithoutAuthReadsServerInit()
    {
        await using var pair = await LoopbackPair.CreateAsync();
        var server = ServeHandshakeAsync(pair.Server, false, 0);
        var client = new RfbClient(pair.ClientStream);

        await client.HandshakeAsync(null).WaitAsync(Timeout);
        await server.WaitAsync(Timeout);

        Assert.Equal(Width, client.FramebufferWidth);
        Assert.Equal(Height, client.FramebufferHeight);
        Assert.Equal("fake-vm", client.ServerName);
    }

    [Fact]
    public async Task VncAuthSendsDesResponse()
    {
        await using var pair = await LoopbackPair.CreateAsync();
        var server = ServeHandshakeAsync(pair.Server, true, 0);
        var client = new RfbClient(pair.ClientStream);

        await client.HandshakeAsync("secret12").WaitAsync(Timeout);
        var response = await server.WaitAsync(Timeout);

        Assert.Equal(VncDesAuth.CreateResponse(Challenge, "secret12"), response);
    }

    [Fact]
    public async Task VncAuthFailureThrows()
    {
        await using var pair = await LoopbackPair.CreateAsync();
        _ = ServeHandshakeAsync(pair.Server, true, 1);
        var client = new RfbClient(pair.ClientStream);

        await Assert.ThrowsAsync<IOException>(() => client.HandshakeAsync("wrong").WaitAsync(Timeout));
    }

    [Fact]
    public async Task DecodesFramebufferUpdate()
    {
        await using var pair = await LoopbackPair.CreateAsync();
        var server = ServeHandshakeAsync(pair.Server, false, 0);
        var client = new RfbClient(pair.ClientStream);
        await client.HandshakeAsync(null).WaitAsync(Timeout);
        await server.WaitAsync(Timeout);

        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dirty = new List<(int X, int Y, int W, int H)>();
        RfbCursor? cursor = null;
        (int X, int Y)? pointer = null;
        KeyboardLeds? leds = null;
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameUpdated += (x, y, w, h) =>
        {
            lock (dirty) dirty.Add((x, y, w, h));
            updated.TrySetResult();
        };
        client.CursorShape += c => cursor = c;
        client.CursorPosition += (x, y) => pointer = (x, y);
        client.LedState += l => leds = l;
        client.ConnectionClosed += e => closed.TrySetResult(e);

        var expected = new byte[Width * Height * 4];
        var message = BuildUpdate(expected);

        _ = client.StartAsync(CancellationToken.None);
        _ = DrainAsync(pair.Server); // 클라이언트 요청(FramebufferUpdateRequest 등)은 읽어 버린다
        await pair.Server.GetStream().WriteAsync(message);
        await updated.Task.WaitAsync(Timeout);
        // 한 메시지에 영역 여럿·커서·포인터·LED 가 함께 온다 — 첫 화면 알림은 첫 영역 뒤일 수 있으니 다 풀릴 때까지 기다린다
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline &&
               !(client.Framebuffer.AsSpan().SequenceEqual(expected) && cursor is not null && pointer is not null && leds is not null))
            await Task.Delay(10);

        Assert.Equal(expected, client.Framebuffer);
        Assert.NotEmpty(dirty);

        Assert.NotNull(cursor);
        Assert.Equal((2, 2, 1, 1), (cursor.Width, cursor.Height, cursor.HotX, cursor.HotY));
        // 마스크 비트가 알파가 된다: [1 0 / 0 1]
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0xFF },
            new[] { cursor.Pixels[3], cursor.Pixels[7], cursor.Pixels[11], cursor.Pixels[15] });
        Assert.Equal((5, 6), pointer);
        Assert.NotNull(leds);

        // 서버가 연결을 닫으면 종료를 알린다
        pair.Server.Close();
        await closed.Task.WaitAsync(Timeout);
    }

    private static readonly byte[] Challenge = Enumerable.Range(0, 16).Select(i => (byte)(i * 7)).ToArray();

    /// <summary>RFB 3.8 서버 쪽 핸드셰이크. VNC 인증이면 클라이언트 응답(16바이트)을 돌려준다.</summary>
    private static async Task<byte[]?> ServeHandshakeAsync(TcpClient server, bool vncAuth, uint securityResult)
    {
        var s = server.GetStream();
        await s.WriteAsync("RFB 003.008\n"u8.ToArray());
        await ReadExactAsync(s, 12); // 클라이언트 버전
        await s.WriteAsync(new byte[] { 1, vncAuth ? (byte)2 : (byte)1 });
        var chosen = await ReadExactAsync(s, 1);
        Assert.Equal(vncAuth ? 2 : 1, chosen[0]);
        byte[]? response = null;
        if (vncAuth)
        {
            await s.WriteAsync(Challenge);
            response = await ReadExactAsync(s, 16);
        }

        await s.WriteAsync(U32(securityResult));
        if (securityResult != 0) return response;

        await ReadExactAsync(s, 1); // ClientInit
        var name = "fake-vm"u8.ToArray();
        var init = new List<byte>();
        init.AddRange(U16(Width));
        init.AddRange(U16(Height));
        init.AddRange(new byte[] { 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0 });
        init.AddRange(U32((uint)name.Length));
        init.AddRange(name);
        await s.WriteAsync(init.ToArray());
        await ReadExactAsync(s, 20); // SetPixelFormat
        var head = await ReadExactAsync(s, 4); // SetEncodings
        await ReadExactAsync(s, BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2)) * 4);
        return response;
    }

    /// <summary>FramebufferUpdate 한 개를 만들고, 같은 내용을 <paramref name="fb" /> 모델에 그린다.</summary>
    private static byte[] BuildUpdate(byte[] fb)
    {
        var rects = new List<byte[]>();
        var zlib = new ZlibChunks();
        var tight2 = new ZlibChunks();

        // 1) Raw (0,0) 2×1 — 픽셀 형식(BGRX)을 그대로 받는다
        byte[] raw = [10, 20, 30, 0xFF, 40, 50, 60, 0xFF];
        Put(fb, 0, 0, 2, 1, raw);
        rects.Add([.. Header(0, 0, 2, 1, 0), .. raw]);

        // 2) Zlib 두 개 — 한 스트림이 사각형 사이에 이어진다
        for (var n = 0; n < 2; n++)
        {
            var px = Pattern(4 * 2, 0x40 + n * 0x20);
            Put(fb, n * 4, 1, 4, 2, px);
            var chunk = zlib.Compress(px);
            rects.Add([.. Header(n * 4, 1, 4, 2, 6), .. U32((uint)chunk.Length), .. chunk]);
        }

        // 3) Tight 단색 (8,0) 8×4
        FillRgb(fb, 8, 0, 8, 4, 0x11, 0x22, 0x33);
        rects.Add([.. Header(8, 0, 8, 4, 7), 0x80, 0x11, 0x22, 0x33]);

        // 4) Tight 기본(필터 없음) 1×3 — 12바이트 미만이라 압축 없이 RGB
        byte[] rgb9 = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        PutRgb(fb, 0, 4, 1, 3, rgb9);
        rects.Add([.. Header(0, 4, 1, 3, 7), 0x00, .. rgb9]);

        // 5) Tight 팔레트 2색(스트림 1) 8×2 — 한 행 1바이트, 앞 비트가 왼쪽 픽셀
        byte[] palette = [0xAA, 0xBB, 0xCC, 0x01, 0x02, 0x03];
        byte[] bits = [0b1100_0011, 0b0101_0101];
        for (var r = 0; r < 2; r++)
        for (var c = 0; c < 8; c++)
        {
            var on = ((bits[r] >> (7 - c)) & 1) != 0;
            FillRgb(fb, 8 + c, 4 + r, 1, 1, palette[on ? 3 : 0], palette[on ? 4 : 1], palette[on ? 5 : 2]);
        }

        rects.Add([.. Header(8, 4, 8, 2, 7), 0x50, 0x01, 0x01, .. palette, .. bits]);

        // 6) Tight 기본(스트림 2) 4×4 — 48바이트라 압축, 길이는 Tight 가변 길이
        var rgb48 = Pattern(12, 0x90); // 48바이트(4×4×3)
        PutRgb(fb, 4, 4, 4, 4, rgb48);
        var tightChunk = tight2.Compress(rgb48);
        rects.Add([.. Header(4, 4, 4, 4, 7), 0x20, .. TightLength(tightChunk.Length), .. tightChunk]);

        // 7) CopyRect — Raw 로 그린 (0,0) 2×1 을 (12,7) 로
        var copied = fb.AsSpan(0, 8).ToArray();
        Put(fb, 12, 7, 2, 1, copied);
        rects.Add([.. Header(12, 7, 2, 1, 1), .. U16(0), .. U16(0)]);

        // 8) 의사 rect: 커서 모양(핫스팟 1,1 · 2×2), 커서 위치, 키보드 LED
        byte[] cursorPixels = Enumerable.Repeat((byte)0x7F, 16).ToArray();
        rects.Add([.. Header(1, 1, 2, 2, -239), .. cursorPixels, 0b1000_0000, 0b0100_0000]);
        rects.Add(Header(5, 6, 0, 0, -232));
        rects.Add([.. Header(0, 0, 0, 0, -261), 0x02]);

        var message = new List<byte> { 0, 0 };
        message.AddRange(U16((ushort)rects.Count));
        foreach (var r in rects) message.AddRange(r);
        return message.ToArray();
    }

    private static byte[] Pattern(int pixels, int seed)
    {
        var px = new byte[pixels * 4];
        for (var i = 0; i < px.Length; i++) px[i] = (byte)(seed + i * 13);
        for (var i = 3; i < px.Length; i += 4) px[i] = 0xFF;
        return px;
    }

    private static void Put(byte[] fb, int x, int y, int w, int h, byte[] bgrx)
    {
        for (var r = 0; r < h; r++) Array.Copy(bgrx, r * w * 4, fb, ((y + r) * Width + x) * 4, w * 4);
    }

    private static void PutRgb(byte[] fb, int x, int y, int w, int h, byte[] rgb)
    {
        for (var r = 0; r < h; r++)
        for (var c = 0; c < w; c++)
        {
            var s = (r * w + c) * 3;
            FillRgb(fb, x + c, y + r, 1, 1, rgb[s], rgb[s + 1], rgb[s + 2]);
        }
    }

    private static void FillRgb(byte[] fb, int x, int y, int w, int h, byte red, byte green, byte blue)
    {
        for (var r = 0; r < h; r++)
        for (var c = 0; c < w; c++)
        {
            var o = ((y + r) * Width + x + c) * 4;
            fb[o] = blue;
            fb[o + 1] = green;
            fb[o + 2] = red;
            fb[o + 3] = 0xFF;
        }
    }

    private static byte[] Header(int x, int y, int w, int h, int encoding)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0), (ushort)x);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)y);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), (ushort)h);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), encoding);
        return b;
    }

    private static byte[] TightLength(int length)
    {
        var bytes = new List<byte> { (byte)(length & 0x7F) };
        if (length > 0x7F)
        {
            bytes[0] |= 0x80;
            bytes.Add((byte)((length >> 7) & 0x7F));
            if (length > 0x3FFF)
            {
                bytes[1] |= 0x80;
                bytes.Add((byte)(length >> 14));
            }
        }

        return bytes.ToArray();
    }

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int count)
    {
        var buffer = new byte[count];
        await s.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static async Task DrainAsync(TcpClient server)
    {
        var buffer = new byte[4096];
        try
        {
            while (await server.GetStream().ReadAsync(buffer) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>서버처럼 연결 내내 이어지는 zlib 스트림 — 사각형마다 동기 flush 로 끊어 보낸다.</summary>
    private sealed class ZlibChunks
    {
        private readonly MemoryStream _output = new();
        private readonly ZLibStream _zlib;

        public ZlibChunks()
        {
            _zlib = new ZLibStream(_output, CompressionLevel.Optimal, true);
        }

        public byte[] Compress(byte[] data)
        {
            var start = _output.Length;
            _zlib.Write(data);
            _zlib.Flush();
            return _output.ToArray()[(int)start..];
        }
    }

    private sealed class LoopbackPair : IAsyncDisposable
    {
        private LoopbackPair(TcpClient client, TcpClient server)
        {
            Client = client;
            Server = server;
        }

        private TcpClient Client { get; }
        public TcpClient Server { get; }
        public Stream ClientStream => Client.GetStream();

        public static async Task<LoopbackPair> CreateAsync()
        {
            // 임시 포트(0) — 고정 포트를 쓰지 않아 다른 검사와 겹치지 않는다
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var client = new TcpClient();
                var accept = listener.AcceptTcpClientAsync();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                return new LoopbackPair(client, await accept);
            }
            finally
            {
                listener.Stop();
            }
        }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            Server.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
