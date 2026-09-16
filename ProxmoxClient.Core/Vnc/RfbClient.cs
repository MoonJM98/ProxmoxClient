using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Vnc;

/// <summary>프레임버퍼 업데이트 사각형 하나.</summary>
public sealed class RfbRect
{
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }

    /// <summary>0=Raw, 1=CopyRect, -223=DesktopSize(크기 변경).</summary>
    public int Encoding { get; init; }

    /// <summary>Raw 인코딩 픽셀(BGRA, 4바이트/px). CopyRect/Resize면 null.</summary>
    public byte[]? Pixels { get; init; }

    /// <summary>CopyRect 원본 위치.</summary>
    public int SrcX { get; init; }

    public int SrcY { get; init; }

    public bool IsResize => Encoding == -223;
}

/// <summary>
///     RFB(Remote Framebuffer, VNC) 3.7/3.8 클라이언트.
///     픽셀 형식은 BGRA 32bpp로 고정 협상하고 설정(<see cref="ConsoleSettings" />)에 따라 Tight/Zlib/Raw 인코딩을 요청한다.
///     서버가 QEMU 확장 키 이벤트(의사 인코딩 -258)를 확인하면 메시지 255/0 으로 XT 스캔코드를 직접 전송한다.
///     핸드셰이크 이후 송신은 단일 전송 채널(순서 보장, 포인터 이동 병합, ArrayPool 버퍼)을 거친다.
/// </summary>
public sealed class RfbClient
{
    private const int EncRaw = 0;
    private const int EncCopyRect = 1;
    private const int EncTight = 7;
    private const int EncDesktopSize = -223;
    private const int EncZlib = 6;
    private const int EncQemuExtendedKeyEvent = RfbEncodings.QemuExtendedKeyEvent;
    private const byte MsgQemuClient = 255;
    private const byte QemuSubExtendedKeyEvent = 0;
    private const int EncPointerPos = -232;
    private const int EncRichCursor = -239; // -240 은 XCursor(형식이 다름)

    /// <summary>ServerInit·DesktopSize 해상도 상한 — 비정상 값으로 거대 할당·int 오버플로가 나지 않도록.</summary>
    private const int MaxFramebufferDimension = 16384;

    /// <summary>연결 종료 후 읽기 루프가 끝나기를 기다렸다 해제기를 정리하는 최대 시간.</summary>
    private static readonly TimeSpan InflaterReleaseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>현재 FramebufferUpdate 의 변경 영역(읽기 루프 전용).</summary>
    private readonly DirtyRegion _dirty = new();

    private readonly object _frameLock = new();

    /// <summary>
    ///     핸드셰이크 이후 모든 클라이언트→서버 메시지는 이 채널을 거쳐 전송 루프 하나가 순서대로 보낸다.
    ///     (여러 호출자가 락을 경쟁하며 순서가 뒤바뀌던 문제 제거, 연속 포인터 이동은 루프에서 병합)
    /// </summary>
    private readonly Channel<OutgoingMessage> _outgoing = Channel.CreateUnbounded<OutgoingMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly byte[] _scratch = new byte[8];

    private readonly Stream _stream;
    private readonly ZlibContinuousInflate[] _tightInflates = [new(), new(), new(), new()];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ZlibContinuousInflate _zlibInflate = new();

    private long _bytesReceived;
    private int _closedRaised;

    private int _lastPointerMask = -1;
    private long _rawRects, _tightRects, _tightImageRects, _copyRects, _zlibRects, _frames;
    private long _statsSince = Environment.TickCount64;

    public RfbClient(Stream stream)
    {
        _stream = stream;
    }

    public int FramebufferWidth { get; private set; }
    public int FramebufferHeight { get; private set; }
    public string ServerName { get; private set; } = string.Empty;
    public bool QemuExtendedKeySupported { get; private set; }

    /// <summary>
    ///     Tight JPEG/PNG 디코더 — 압축 이미지를 프레임버퍼 위치에 직접 기록한다(<see cref="TightImageDecoder" />).
    ///     Core는 WPF 의존 없이 유지하기 위해 디코딩을 호스트에 위임한다.
    /// </summary>
    public TightImageDecoder? ImageDecoder { get; set; }

    /// <summary>
    ///     서버 프레임버퍼 원본(BGRA/Bgr32 4바이트/px). 읽기 스레드만 쓰고 UI 는 잠금 없이 변경 영역을 복사한다
    ///     (찢어진 프레임은 다음 갱신에서 복구). 해상도 변경 시 배열이 교체되므로 매번 이 속성에서 다시 읽는다.
    /// </summary>
    public byte[] Framebuffer { get; private set; } = [];

    /// <summary>콘솔 설정 — 인코딩/품질/파이프라인 (호스트에서 설정, 연결 전 적용).</summary>
    public ConsoleSettings? Settings { get; set; }

    /// <summary>초 단위 통계 스냅숏 — 호출 시점부터 재측정한다.</summary>
    public (double MegabitsPerSecond, double FramesPerSecond, long RawRects, long TightRects, long ImageRects, long
        CopyRects)
        TakeStats()
    {
        var elapsed = (Environment.TickCount64 - _statsSince) / 1000.0;
        var bytes = Interlocked.Exchange(ref _bytesReceived, 0);
        var frames = Interlocked.Exchange(ref _frames, 0);
        var raw = Interlocked.Exchange(ref _rawRects, 0);
        var tight = Interlocked.Exchange(ref _tightRects, 0);
        var image = Interlocked.Exchange(ref _tightImageRects, 0);
        var copy = Interlocked.Exchange(ref _copyRects, 0);
        _statsSince = Environment.TickCount64;
        var seconds = Math.Max(elapsed, 0.1);
        return (bytes * 8 / seconds / 1_000_000, frames / seconds, raw, tight, image, copy);
    }

    /// <summary>ServerInit 수신(연결 확립, 프레임버퍼 크기 확정).</summary>
    public event Action<int, int>? ServerInitReceived;

    /// <summary>
    ///     프레임버퍼 갱신 신호(변경 영역 x,y,w,h) — 서버 업데이트 하나당 병합된 사각형(최대 <see cref="DirtyRegion.Capacity" />개)마다 발생.
    ///     빈 업데이트에는 발생하지 않는다. 데이터는 <see cref="Framebuffer" />에서 직접 읽는다.
    /// </summary>
    public event Action<int, int, int, int>? FrameUpdated;

    /// <summary>커서 위치 갱신(PointerPos 의사 rect).</summary>
    public event Action<int, int>? CursorPosition;

    /// <summary>커서 모양 갱신(RichCursor 의사 rect) — BGRA 픽셀(투명 적용)과 크기.</summary>
    public event Action<byte[], int, int>? CursorShape;

    /// <summary>벨(사운드) 알림.</summary>
    public event Action? Bell;

    /// <summary>서버 클립보드 텍스트.</summary>
    public event Action<string>? ServerCutText;

    /// <summary>연결 종료. null이면 정상 종료, 아니면 오류.</summary>
    public event Action<Exception?>? ConnectionClosed;

    /// <summary>
    ///     버전/보안 협상 + ClientInit/ServerInit + 픽셀 형식·인코딩 설정.
    ///     vncPassword는 RFB VNC 인증(유형 2) 시 사용(Proxmox은 VNC 티켓).
    /// </summary>
    public async Task HandshakeAsync(string? vncPassword, CancellationToken ct = default)
    {
        var version = await ReadStringAsciiAsync(12, ct).ConfigureAwait(false);
        if (!version.StartsWith("RFB ", StringComparison.Ordinal))
            throw new IOException(Res.T("RfbClient_01", version.Trim()));

        var serverMajor = ParseVersionPart(version, 4);
        var serverMinor = ParseVersionPart(version, 9);
        if (serverMajor != 3 || serverMinor < 7) throw new IOException(Res.T("RfbClient_02", version.Trim()));

        // 서버가 3.7이면 3.7로 응답(3.8 이상은 3.8) — 버전별 보안 결과 처리 흐름과 일치시킨다
        serverMinor = Math.Min(serverMinor, 8);
        await WriteAsync(Encoding.ASCII.GetBytes($"RFB 003.{serverMinor:D3}\n"), ct).ConfigureAwait(false);

        var count = await ReadByteAsync(ct).ConfigureAwait(false);
        if (count == 0)
        {
            var reasonLength = (int)await ReadU32Async(ct).ConfigureAwait(false);
            var reason = await ReadStringAsciiAsync(reasonLength, ct).ConfigureAwait(false);
            throw new IOException(Res.T("RfbClient_03", reason));
        }

        var types = new int[count];
        for (var i = 0; i < count; i++) types[i] = await ReadByteAsync(ct).ConfigureAwait(false);

        int chosen;
        if (Array.IndexOf(types, 1) >= 0)
            chosen = 1; // None
        else if (Array.IndexOf(types, 2) >= 0)
            chosen = 2; // VNC Authentication
        else
            throw new IOException(Res.T("RfbClient_20", string.Join(", ", types)));

        await WriteAsync([(byte)chosen], ct).ConfigureAwait(false);

        if (chosen == 2)
        {
            var challenge = new byte[16];
            await ReadExactlyAsync(challenge, 0, 16, ct).ConfigureAwait(false);
            var response = VncDesAuth.CreateResponse(challenge, vncPassword ?? string.Empty);
            await WriteAsync(response, ct).ConfigureAwait(false);
        }

        // 3.8은 None/VNC-auth 모두 SecurityResult 전송 (3.7은 VNC-auth만)
        if (serverMinor >= 8 || chosen == 2)
        {
            var result = await ReadU32Async(ct).ConfigureAwait(false);
            if (result != 0) throw new IOException(Res.T("RfbClient_04"));
        }

        await WriteAsync([1], ct).ConfigureAwait(false);

        FramebufferWidth = await ReadU16Async(ct).ConfigureAwait(false);
        FramebufferHeight = await ReadU16Async(ct).ConfigureAwait(false);
        ValidateFramebufferSize(FramebufferWidth, FramebufferHeight);
        _ = await ReadPixelFormatAsync(ct).ConfigureAwait(false); // 서버 기본 형식 (무시)
        var nameLen = (int)await ReadU32Async(ct).ConfigureAwait(false);
        ServerName = nameLen > 0 ? await ReadStringAsciiAsync(nameLen, ct).ConfigureAwait(false) : string.Empty;

        // RFB 6143 §7.4: 타입(1) + 패딩(3) + 16바이트 포맷 — 포맷은 오프셋 4부터 시작
        Span<byte> setPixelFormat = stackalloc byte[20];
        setPixelFormat[0] = 0; // SetPixelFormat
        setPixelFormat[4] = 32; // bits-per-pixel
        setPixelFormat[5] = 24; // depth
        setPixelFormat[6] = 0; // big-endian = false
        setPixelFormat[7] = 1; // true-colour
        BinaryPrimitives.WriteUInt16BigEndian(setPixelFormat.Slice(8, 2), 255); // red-max
        BinaryPrimitives.WriteUInt16BigEndian(setPixelFormat.Slice(10, 2), 255); // green-max
        BinaryPrimitives.WriteUInt16BigEndian(setPixelFormat.Slice(12, 2), 255); // blue-max
        setPixelFormat[14] = 16; // red-shift
        setPixelFormat[15] = 8; // green-shift
        setPixelFormat[16] = 0; // blue-shift
        await WriteAsync(setPixelFormat.ToArray(), ct).ConfigureAwait(false);

        // 확장 키 지원 여부는 서버가 -258 의사 rect 로 확인해 줄 때 켠다(추정으로 켜지 않음)
        QemuExtendedKeySupported = false;
        await ApplyEncodingsAsync(ct).ConfigureAwait(false);

        ServerInitReceived?.Invoke(FramebufferWidth, FramebufferHeight);
    }

    /// <summary>전체 화면 갱신 요청 후 읽기 루프 시작(백그라운드).</summary>
    public Task StartAsync(CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Exception? error = null;
            Task? sendLoop = null;
            Task? readLoop = null;
            try
            {
                sendLoop = RunSendLoopAsync(loopCts.Token);
                await RequestFramebufferUpdateAsync(false, loopCts.Token).ConfigureAwait(false);
                readLoop = RunLoopAsync(loopCts.Token);

                // 읽기·전송 중 먼저 끝난(또는 실패한) 쪽이 연결 종료를 결정한다
                var finished = await Task.WhenAny(readLoop, sendLoop).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 정상 종료
            }
            catch (EndOfStreamException)
            {
                // 서버가 연결을 닫음(게스트 종료 등) — 오류가 아닌 정상 종료로 알린다
            }
            catch (Exception ex)
            {
                // WebSocketException 등 모든 전송 오류를 종료로 알린다 — 누락 시 UI가 "연결됨"에 머문다
                error = ex;
            }
            finally
            {
                loopCts.Cancel();
                _outgoing.Writer.TryComplete();
                ObserveRemaining(readLoop, sendLoop);
            }

            RaiseConnectionClosed(error);
            await ReleaseInflatersAsync(readLoop).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    /// <summary>
    ///     zlib 해제기의 풀 청크·네이티브 핸들 정리. 읽기 루프가 아직 해제기를 쓰는 중일 수 있으므로 끝난 뒤에만 정리하고,
    ///     제한 시간 안에 끝나지 않으면(취소가 전달되지 않는 극단적 경우) GC 에 맡긴다.
    /// </summary>
    private async Task ReleaseInflatersAsync(Task? readLoop)
    {
        if (readLoop is not null
            && await Task.WhenAny(readLoop, Task.Delay(InflaterReleaseTimeout)).ConfigureAwait(false) != readLoop)
            return;

        _zlibInflate.Dispose();
        foreach (var inflate in _tightInflates) inflate.Dispose();
    }

    /// <summary>
    ///     키 이벤트. 서버가 QEMU 확장 키 이벤트를 확인했고 스캔코드가 있으면 스캔코드(레이아웃 독립)로,
    ///     아니면 표준 KeyEvent(keysym) 로 보낸다.
    /// </summary>
    public Task SendKeyEventAsync(int xtScanCode, bool down, int keysym = 0, CancellationToken ct = default)
    {
        if (QemuExtendedKeySupported && xtScanCode != 0)
        {
            // QEMU 확장 키 이벤트: U8 255, U8 0, U16 down, U32 keysym, U32 keycode
            const int ExtKeyMessageLength = 12;
            var ext = RentMessage(ExtKeyMessageLength);
            ext[0] = MsgQemuClient;
            ext[1] = QemuSubExtendedKeyEvent;
            BinaryPrimitives.WriteUInt16BigEndian(ext.AsSpan(2, 2), down ? (ushort)1 : (ushort)0);
            BinaryPrimitives.WriteInt32BigEndian(ext.AsSpan(4, 4), keysym);
            BinaryPrimitives.WriteInt32BigEndian(ext.AsSpan(8, 4), RfbEncodings.ToQemuKeycode(xtScanCode));
            return EnqueueAsync(ext, ExtKeyMessageLength);
        }

        if (keysym == 0) return Task.CompletedTask;

        const int KeyMessageLength = 8;
        var msg = RentMessage(KeyMessageLength);
        msg[0] = 4;
        msg[1] = down ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(4, 4), keysym);
        return EnqueueAsync(msg, KeyMessageLength);
    }

    /// <summary>포인터 이벤트. mask: 1=좌 2=중 4=우 8=휠업 16=휠다운. 버튼 상태가 같은 연속 이동은 병합 대상.</summary>
    public Task SendPointerEventAsync(int buttonMask, int x, int y, CancellationToken ct = default)
    {
        const int PointerMessageLength = 6;
        var msg = RentMessage(PointerMessageLength);
        msg[0] = 5;
        msg[1] = (byte)buttonMask;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2, 2), (ushort)x);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4, 2), (ushort)y);

        var isMove = buttonMask == Interlocked.Exchange(ref _lastPointerMask, buttonMask);
        return EnqueueAsync(msg, PointerMessageLength, isMove);
    }

    /// <summary>클라이언트→서버 클립보드 텍스트 전송.</summary>
    public Task SendClientCutTextAsync(string text, CancellationToken ct = default)
    {
        const int HeaderLength = 8;
        text ??= string.Empty;
        var payloadLength = Encoding.UTF8.GetByteCount(text);
        var length = HeaderLength + payloadLength;
        var msg = RentMessage(length);
        msg[0] = 6; // ClientCutText
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(4, 4), payloadLength);
        Encoding.UTF8.GetBytes(text, msg.AsSpan(HeaderLength, payloadLength));
        return EnqueueAsync(msg, length);
    }

    /// <summary>풀에서 메시지 버퍼를 빌린다 — 이전 사용 흔적이 패딩 바이트로 새지 않도록 사용 구간을 0 으로 지운다.</summary>
    private static byte[] RentMessage(int length)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        Array.Clear(buffer, 0, length);
        return buffer;
    }

    private Task EnqueueAsync(byte[] buffer, int length, bool isPointerMove = false)
    {
        if (!_outgoing.Writer.TryWrite(new OutgoingMessage(buffer, length, isPointerMove)))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw new InvalidOperationException(Res.T("RfbClient_06"));
        }

        return Task.CompletedTask;
    }

    /// <summary>단일 전송 루프 — 큐 순서대로 보내되, 큐에 연달아 쌓인 포인터 이동은 마지막 것만 보낸다.</summary>
    private async Task RunSendLoopAsync(CancellationToken ct)
    {
        var reader = _outgoing.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        while (reader.TryRead(out var message))
        {
            while (message.IsPointerMove && reader.TryPeek(out var next) && next.IsPointerMove)
            {
                ArrayPool<byte>.Shared.Return(message.Buffer); // 병합으로 버려지는 이동 메시지도 반환
                reader.TryRead(out message);
            }

            try
            {
                await _stream.WriteAsync(message.Buffer.AsMemory(0, message.Length), ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(message.Buffer);
            }
        }
    }

    /// <summary>먼저 끝난 루프 외 나머지 루프의 예외를 관찰 처리 — UnobservedTaskException 로 새지 않도록.</summary>
    internal static void ObserveRemaining(params Task?[] tasks)
    {
        foreach (var task in tasks)
            task?.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void RaiseConnectionClosed(Exception? error)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0) ConnectionClosed?.Invoke(error);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var type = await ReadByteAsync(ct).ConfigureAwait(false);
            switch (type)
            {
                case 0:
                    await HandleFramebufferUpdateAsync(ct).ConfigureAwait(false);
                    break;
                case 1: // SetColourMapEntries (사용 안 함)
                    await ReadExactlyAsync(_scratch, 0, 5, ct).ConfigureAwait(false);
                    var colours = BinaryPrimitives.ReadUInt16BigEndian(_scratch.AsSpan(3, 2));
                    await SkipAsync(colours * 6, ct).ConfigureAwait(false);
                    break;
                case 2:
                    Bell?.Invoke();
                    break;
                case 3: // ServerCutText
                    await ReadExactlyAsync(_scratch, 0, 4, ct).ConfigureAwait(false);
                    var length = BinaryPrimitives.ReadInt32BigEndian(_scratch);
                    if (length > 0 && length < 1_000_000)
                        ServerCutText?.Invoke(await ReadStringAsciiAsync(length, ct).ConfigureAwait(false));
                    else if (length > 0) await SkipAsync(length, ct).ConfigureAwait(false); // 본문을 버려야 스트림 동기가 유지된다
                    break;
                default:
                    throw new IOException(Res.T("RfbClient_07", type));
            }
        }
    }

    private async Task HandleFramebufferUpdateAsync(CancellationToken ct)
    {
        _dirty.Clear();
        await ReadExactlyAsync(_scratch, 0, 3, ct).ConfigureAwait(false); // 패딩 1 + rectCount 상위 1바이트
        var rectCount = BinaryPrimitives.ReadUInt16BigEndian(_scratch.AsSpan(1, 2));

        // 파이프라인: rect 수 확인 직후 요청 → 서버는 다음 프레임 준비 시작
        // 클라이언트는 현재 프레임 rect들을 읽으면서 동시에 처리 (스레드 분리)
        await RequestFramebufferUpdateAsync(true, ct).ConfigureAwait(false);

        for (var i = 0; i < rectCount; i++)
        {
            await ReadExactlyAsync(_scratch, 0, 8, ct).ConfigureAwait(false);
            var x = BinaryPrimitives.ReadUInt16BigEndian(_scratch.AsSpan(0, 2));
            var y = BinaryPrimitives.ReadUInt16BigEndian(_scratch.AsSpan(2, 2));
            var w = BinaryPrimitives.ReadUInt16BigEndian(_scratch.AsSpan(4, 2));
            var h = BinaryPrimitives.ReadUInt16BigEndian(_scratch.AsSpan(6, 2));
            await ReadExactlyAsync(_scratch, 0, 4, ct).ConfigureAwait(false);
            var encoding = BinaryPrimitives.ReadInt32BigEndian(_scratch.AsSpan(0, 4));
            if (encoding is EncRaw or EncZlib or EncTight or EncCopyRect) ValidateRect(x, y, w, h);

            switch (encoding)
            {
                case EncRaw:
                {
                    // 네트워크 스트림 → 프레임버퍼 행 직접 수신.
                    // 프레임버퍼는 이 읽기 태스크가 유일한 작성자이므로 락 없이 await로 읽는다
                    // (락을 잡고 블록하면 렌더러와 교착해 UI가 멈춘다). 찢어진 프레임은 다음 갱신에 복구.
                    EnsureFramebuffer(FramebufferWidth, FramebufferHeight);
                    var fb = Framebuffer;
                    var rowBytes = w * 4;
                    for (var row = 0; row < h; row++)
                    {
                        var offset = ((y + row) * FramebufferWidth + x) * 4;
                        await ReadExactlyAsync(fb, offset, rowBytes, ct).ConfigureAwait(false);
                    }

                    MarkDirty(x, y, w, h);
                    Interlocked.Increment(ref _rawRects);
                    break;
                }
                case EncZlib:
                {
                    await ReadExactlyAsync(_scratch, 0, 4, ct).ConfigureAwait(false);
                    var compressedLength = BinaryPrimitives.ReadInt32BigEndian(_scratch);
                    if (compressedLength is <= 0 or > 64 * 1024 * 1024)
                        throw new IOException(Res.T("RfbClient_08", compressedLength));

                    await ReadCompressedChunkAsync(_zlibInflate, compressedLength, ct).ConfigureAwait(false);

                    // 중간 픽셀 버퍼 없이 프레임버퍼 행 위치에 바로 압축 해제
                    EnsureFramebuffer(FramebufferWidth, FramebufferHeight);
                    var rowBytes = w * 4;
                    for (var r = 0; r < h; r++)
                    {
                        var offset = ((y + r) * FramebufferWidth + x) * 4;
                        var produced = _zlibInflate.Decompress(Framebuffer, offset, rowBytes);
                        if (produced < rowBytes) throw new IOException(Res.T("RfbClient_09", r, produced, rowBytes));
                    }

                    MarkDirty(x, y, w, h);
                    Interlocked.Increment(ref _zlibRects);
                    break;
                }
                case EncTight:
                {
                    var control = await ReadByteAsync(ct).ConfigureAwait(false);
                    for (var s = 0; s < 4; s++)
                        if (((control >> s) & 1) != 0)
                            _tightInflates[s].Reset(); // 서버가 스트림 초기화를 지시 — 다음 청크는 zlib 헤더부터 시작

                    var sub = control >> 4;
                    if (sub == 0x08)
                    {
                        await HandleTightFillAsync(x, y, w, h, ct).ConfigureAwait(false);
                    }
                    else if (sub is 0x09 or 0x0A)
                    {
                        await HandleTightImageAsync(x, y, w, h, ct).ConfigureAwait(false);
                    }
                    else if ((sub & 0x08) == 0)
                    {
                        var filter = (sub & 0x04) != 0
                            ? await ReadByteAsync(ct).ConfigureAwait(false)
                            : 0;
                        var streamId = sub & 0x03;
                        try
                        {
                            await HandleTightBasicAsync(streamId, filter, x, y, w, h, ct).ConfigureAwait(false);
                        }
                        catch (InvalidDataException ex)
                        {
                            throw new IOException(Res.T("RfbClient_10", streamId), ex);
                        }
                    }
                    else
                    {
                        throw new IOException(Res.T("RfbClient_11", sub));
                    }

                    MarkDirty(x, y, w, h);
                    Interlocked.Increment(ref _tightRects);
                    break;
                }
                case EncQemuExtendedKeyEvent:
                    // 서버가 확장 키 이벤트 지원을 확인(빈 의사 rect) — 이후 키 입력은 스캔코드로 전송
                    QemuExtendedKeySupported = true;
                    break;
                case EncPointerPos:
                    // 커서 위치 의사 rect — rect x,y가 위치, 화면 갱신 아님
                    CursorPosition?.Invoke(x, y);
                    break;
                case EncRichCursor:
                    await ReadCursorShapeAsync(w, h, ct).ConfigureAwait(false);
                    break;
                case EncCopyRect:
                {
                    await ReadExactlyAsync(_scratch, 0, 4, ct).ConfigureAwait(false);
                    var srcX = BinaryPrimitives.ReadUInt16BigEndian(_scratch.AsSpan(0, 2));
                    var srcY = BinaryPrimitives.ReadUInt16BigEndian(_scratch.AsSpan(2, 2));
                    ValidateRect(srcX, srcY, w, h);
                    EnsureFramebuffer(FramebufferWidth, FramebufferHeight);
                    CopyRectInFramebuffer(srcX, srcY, x, y, w, h);
                    MarkDirty(x, y, w, h);
                    Interlocked.Increment(ref _copyRects);

                    break;
                }
                case EncDesktopSize:
                    ValidateFramebufferSize(w, h);
                    FramebufferWidth = w;
                    FramebufferHeight = h;
                    EnsureFramebuffer(w, h, true);
                    MarkDirty(0, 0, w, h);
                    break;
                case < 0 when w == 0 && h == 0:
                    // 페이로드 없는 알 수 없는 의사 rect(서버 확장 알림 등)는 무시 — 스트림 동기에 영향 없음
                    break;
                default:
                    throw new IOException(Res.T("RfbClient_12", encoding));
            }
        }

        for (var i = 0; i < _dirty.Count; i++)
        {
            var rect = _dirty[i];
            FrameUpdated?.Invoke(rect.X1, rect.Y1, rect.Width, rect.Height);
        }

        Interlocked.Increment(ref _frames);
    }

    /// <summary>
    ///     픽셀을 싣는 사각형이 프레임버퍼 안에 있는지 확인. 벗어나면 행이 다음 줄로 넘어가 화면을 덮거나
    ///     배열 끝을 넘어 예외가 나므로, 명확한 프로토콜 오류로 연결을 끊는다.
    /// </summary>
    private void ValidateRect(int x, int y, int w, int h)
    {
        if ((uint)x + (uint)w > (uint)FramebufferWidth || (uint)y + (uint)h > (uint)FramebufferHeight)
            throw new IOException(
                Res.T("RfbClient_13", x, y, w, h, FramebufferWidth, FramebufferHeight));
    }

    private static void ValidateFramebufferSize(int width, int height)
    {
        if (width is < 0 or > MaxFramebufferDimension || height is < 0 or > MaxFramebufferDimension)
            throw new IOException(Res.T("RfbClient_14", width, height));
    }

    /// <summary>
    ///     프레임버퍼 내부 영역 복사 — 행 단위 Span.CopyTo(memmove)로 임시 버퍼 없이 처리.
    ///     원본·대상이 세로로 겹친 채 아래로 옮길 때는 아래 행부터 복사해야 아직 옮기지 않은 원본 행이 덮이지 않는다.
    /// </summary>
    private void CopyRectInFramebuffer(int srcX, int srcY, int x, int y, int w, int h)
    {
        var fb = Framebuffer.AsSpan();
        var rowBytes = w * 4;
        var bottomUp = y > srcY;
        for (var i = 0; i < h; i++)
        {
            var row = bottomUp ? h - 1 - i : i;
            var src = ((srcY + row) * FramebufferWidth + srcX) * 4;
            var dst = ((y + row) * FramebufferWidth + x) * 4;
            fb.Slice(src, rowBytes).CopyTo(fb.Slice(dst, rowBytes));
        }
    }

    /// <summary>압축 조각을 풀 버퍼로 읽어 해제기에 소유권째 넘긴다(해제기가 소비 후 반환) — 중간 복사 없음.</summary>
    private async ValueTask ReadCompressedChunkAsync(ZlibContinuousInflate inflate, int length, CancellationToken ct)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactlyAsync(chunk, 0, length, ct).ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(chunk); // 소유권 이전 전 실패 — 여기서 반환 후 예외 전파
            throw;
        }

        inflate.AddCompressedChunk(chunk, length);
    }

    private void MarkDirty(int x, int y, int w, int h)
    {
        _dirty.Add(x, y, w, h);
    }

    private void EnsureFramebuffer(int width, int height, bool zero = false)
    {
        var required = checked(width * height * 4); // 해상도 상한(ValidateFramebufferSize)으로 오버플로 없음 — 방어적 확인
        if (Framebuffer.Length == required)
        {
            if (zero)
                lock (_frameLock)
                {
                    Array.Clear(Framebuffer);
                }

            return;
        }

        lock (_frameLock)
        {
            Framebuffer = new byte[required];
        }
    }

    private Task RequestFramebufferUpdateAsync(bool incremental, CancellationToken ct)
    {
        // 큐에 넣은 뒤 전송 전까지 내용이 유지돼야 하므로 요청마다 풀 버퍼를 빌리고 전송 루프가 반환
        const int UpdateRequestLength = 10;
        var msg = RentMessage(UpdateRequestLength);
        msg[0] = 3;
        msg[1] = incremental ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6, 2), (ushort)FramebufferWidth);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8, 2), (ushort)FramebufferHeight);
        return EnqueueAsync(msg, UpdateRequestLength);
    }

    private async ValueTask<uint> ReadU32Async(CancellationToken ct)
    {
        await ReadExactlyAsync(_scratch, 0, 4, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(_scratch);
    }

    private async ValueTask<ushort> ReadU16Async(CancellationToken ct)
    {
        await ReadExactlyAsync(_scratch, 0, 2, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16BigEndian(_scratch);
    }

    /// <summary>ValueTask — 수신 버퍼에 데이터가 있어 동기로 끝나면 할당이 없다(rect 헤더·Tight 길이 등 핫패스).</summary>
    private async ValueTask<byte> ReadByteAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_scratch, 0, 1, ct).ConfigureAwait(false);
        return _scratch[0];
    }

    private async Task<byte[]> ReadPixelFormatAsync(CancellationToken ct)
    {
        var format = new byte[16];
        await ReadExactlyAsync(format, 0, 16, ct).ConfigureAwait(false);
        return format;
    }

    private async Task SkipAsync(int count, CancellationToken ct)
    {
        const int SkipChunkSize = 4096;
        var sink = ArrayPool<byte>.Shared.Rent(Math.Min(count, SkipChunkSize));
        try
        {
            while (count > 0)
            {
                var take = Math.Min(count, sink.Length);
                await ReadExactlyAsync(sink, 0, take, ct).ConfigureAwait(false);
                count -= take;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sink);
        }
    }

    /// <summary>비동기·취소 가능 — 동기 대기(sync-over-async)로 핸드셰이크가 취소/타임아웃되지 않던 문제 제거.</summary>
    private async Task<string> ReadStringAsciiAsync(int length, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactlyAsync(buf, 0, length, ct).ConfigureAwait(false);
            return Encoding.Latin1.GetString(buf, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }


    private async Task ReadCursorShapeAsync(int w, int h, CancellationToken ct)
    {
        if (w <= 0 || h <= 0 || w > 256 || h > 256) throw new IOException(Res.T("RfbClient_15", w, h));

        var pixels = new byte[w * h * 4];
        await ReadExactlyAsync(pixels, 0, pixels.Length, ct).ConfigureAwait(false);
        // 픽셀 배열은 UI 로 소유권이 넘어가므로 새로 할당하고, 마스크는 여기서만 쓰므로 풀에서 빌린다
        var maskRowBytes = (w + 7) / 8;
        var maskLength = maskRowBytes * h;
        var mask = ArrayPool<byte>.Shared.Rent(maskLength);
        try
        {
            await ReadExactlyAsync(mask, 0, maskLength, ct).ConfigureAwait(false);

            for (var r = 0; r < h; r++)
            for (var px = 0; px < w; px++)
            {
                var bit = (mask[r * maskRowBytes + (px >> 3)] >> (7 - (px & 7))) & 1;
                pixels[(r * w + px) * 4 + 3] = bit == 0 ? (byte)0 : (byte)0xFF;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(mask);
        }

        CursorShape?.Invoke(pixels, w, h);
    }

    private async Task ApplyEncodingsAsync(CancellationToken ct)
    {
        var s = (Settings ?? new ConsoleSettings()).Normalize();
        var encodings = new List<int>();
        switch (s.Encoding)
        {
            case ConsoleSettings.VncEncoding.Tight:
                encodings.Add(EncTight);
                // 표준 Tight 의사 인코딩: 품질(JPEG 활성화), 압축 레벨 — 값 변환·범위 보정은 헬퍼가 담당
                encodings.Add(RfbEncodings.QualityLevel(s.QualityLevel));
                encodings.Add(RfbEncodings.CompressLevel(s.CompressionLevel));
                encodings.Add(EncZlib);
                break;
            case ConsoleSettings.VncEncoding.Zlib:
                encodings.Add(EncZlib);
                encodings.Add(RfbEncodings.CompressLevel(s.CompressionLevel));
                break;
        }

        encodings.Add(EncCopyRect);
        encodings.Add(EncDesktopSize);
        if (s.UseQemuExtendedKeys) encodings.Add(EncQemuExtendedKeyEvent);

        encodings.Add(EncRaw);

        var msg = new byte[4 + encodings.Count * 4];
        msg[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2, 2), (ushort)encodings.Count);
        for (var i = 0; i < encodings.Count; i++)
            BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(4 + i * 4, 4), encodings[i]);
        await WriteAsync(msg, ct).ConfigureAwait(false);
    }

    private async ValueTask<int> ReadTightLengthAsync(CancellationToken ct)
    {
        var b0 = await ReadByteAsync(ct).ConfigureAwait(false);
        if ((b0 & 0x80) == 0) return b0;

        var b1 = await ReadByteAsync(ct).ConfigureAwait(false);
        if ((b1 & 0x80) == 0) return ((b1 & 0x7F) << 7) | (b0 & 0x7F);

        var b2 = await ReadByteAsync(ct).ConfigureAwait(false);
        return (b2 << 14) | ((b1 & 0x7F) << 7) | (b0 & 0x7F);
    }

    private async Task HandleTightFillAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        await ReadExactlyAsync(_scratch, 0, 3, ct).ConfigureAwait(false); // RGB — 읽기 루프 전용 스크래치 재사용
        EnsureFramebuffer(FramebufferWidth, FramebufferHeight);
        FillRect(Framebuffer, FramebufferWidth, x, y, w, h, _scratch[0], _scratch[1], _scratch[2]);
    }

    /// <summary>단색 사각형 — BGRX 픽셀 하나를 uint 로 만들어 행마다 Span.Fill(임시 행 배열 없음, SIMD 가속).</summary>
    private static void FillRect(byte[] framebuffer, int framebufferWidth, int x, int y, int w, int h, byte r, byte g,
        byte b)
    {
        var pixel = ToBgrx(r, g, b);
        var pixels = MemoryMarshal.Cast<byte, uint>(framebuffer.AsSpan());
        for (var row = 0; row < h; row++) pixels.Slice((y + row) * framebufferWidth + x, w).Fill(pixel);
    }

    private async Task HandleTightImageAsync(int x, int y, int w, int h, CancellationToken ct)
    {
        var length = await ReadTightLengthAsync(ct).ConfigureAwait(false);
        var data = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactlyAsync(data, 0, length, ct).ConfigureAwait(false);
            EnsureFramebuffer(FramebufferWidth, FramebufferHeight);
            var decoder = ImageDecoder ?? throw new IOException(Res.T("RfbClient_16"));

            // 풀에서 빌린 버퍼를 복사 없이 넘기고, 디코더가 프레임버퍼 위치에 직접 기록한다
            decoder(data, length, Framebuffer, FramebufferWidth, x, y, w, h);
            Interlocked.Increment(ref _tightImageRects);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private async Task HandleTightBasicAsync(int streamId, int filter, int x, int y, int w, int h, CancellationToken ct)
    {
        var size = w * h * 3;
        if (size == 0) return;

        byte[]? palette = null;
        var numColors = 0;
        var bitsPerPixel = 0;
        try
        {
            if (filter == 1)
            {
                numColors = await ReadByteAsync(ct).ConfigureAwait(false) + 1;
                palette = ArrayPool<byte>.Shared.Rent(numColors * 3); // 최대 256색×3 — 풀 재사용
                await ReadExactlyAsync(palette, 0, numColors * 3, ct).ConfigureAwait(false);
                bitsPerPixel = numColors <= 2 ? 1 : 8;
                var rowSize = (w * bitsPerPixel + 7) / 8;
                size = rowSize * h;
            }

            var data = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                if (size < 12)
                {
                    await ReadExactlyAsync(data, 0, size, ct).ConfigureAwait(false);
                }
                else
                {
                    var length = await ReadTightLengthAsync(ct).ConfigureAwait(false);
                    await ReadCompressedChunkAsync(_tightInflates[streamId], length, ct).ConfigureAwait(false);
                    var produced = _tightInflates[streamId].Decompress(data, 0, size);
                    if (produced < size) throw new IOException(Res.T("RfbClient_17", produced, size));
                }

                ApplyTightFilter(filter, data, palette, numColors, bitsPerPixel, x, y, w, h);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }
        finally
        {
            if (palette is not null) ArrayPool<byte>.Shared.Return(palette);
        }
    }

    private void ApplyTightFilter(int filter, byte[] data, byte[]? palette, int paletteColors, int bitsPerPixel, int x,
        int y, int w, int h)
    {
        EnsureFramebuffer(FramebufferWidth, FramebufferHeight);

        switch (filter)
        {
            case 0:
                WriteRgbToFramebuffer(data, x, y, w, h);
                break;
            case 1:
                WritePaletteToFramebuffer(data, palette!, paletteColors, bitsPerPixel, x, y, w, h);
                break;
            case 2:
                ReverseGradientFilter(data.AsSpan(0, w * h * 3), w, h);
                WriteRgbToFramebuffer(data, x, y, w, h);
                break;
            default:
                throw new IOException(Res.T("RfbClient_18", filter));
        }
    }

    /// <summary>RGB 바이트 → 프레임버퍼 픽셀(uint, 메모리 순서 B,G,R,A).</summary>
    private static uint ToBgrx(byte r, byte g, byte b)
    {
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    /// <summary>
    ///     팔레트 인덱스 → 픽셀. 팔레트를 한 번만 uint 색으로 변환(스택)해 픽셀당 바이트 4회 쓰기를 uint 1회로 줄인다.
    ///     잘못된 인덱스가 와도 256 칸 표(0 초기화) 안이므로 범위를 벗어나지 않는다.
    /// </summary>
    private void WritePaletteToFramebuffer(
        byte[] data, byte[] palette, int paletteColors, int bitsPerPixel, int x, int y, int w, int h)
    {
        const int MaxPaletteColors = 256;
        Span<uint> colors = stackalloc uint[MaxPaletteColors];
        var colorCount = Math.Min(paletteColors, MaxPaletteColors);
        for (var i = 0; i < colorCount; i++) colors[i] = ToBgrx(palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2]);

        var pixels = MemoryMarshal.Cast<byte, uint>(Framebuffer.AsSpan());
        var rowSize = (w * bitsPerPixel + 7) / 8;
        for (var r = 0; r < h; r++)
        {
            var src = data.AsSpan(r * rowSize, rowSize);
            var dst = pixels.Slice((y + r) * FramebufferWidth + x, w);
            if (bitsPerPixel == 1)
                for (var px = 0; px < dst.Length; px++)
                    dst[px] = colors[(src[px >> 3] >> (7 - (px & 7))) & 1];
            else
                for (var px = 0; px < dst.Length; px++)
                    dst[px] = colors[src[px]];
        }
    }

    /// <summary>
    ///     Gradient 역필터(Tight): 예측 = clamp(left + up - upLeft, 0, 255), 경계 밖 이웃은 0. 제자리 복원.
    ///     첫 행·첫 픽셀 경계 처리를 루프 밖으로 빼 픽셀·채널마다 있던 분기 3개를 제거했다.
    /// </summary>
    private static void ReverseGradientFilter(Span<byte> data, int w, int h)
    {
        const int Channels = 3;
        var stride = w * Channels;

        // 첫 행: up = upLeft = 0 → 예측 = left
        var first = data[..stride];
        for (var i = Channels; i < first.Length; i++) first[i] = (byte)(first[i] + first[i - Channels]);

        for (var r = 1; r < h; r++)
        {
            var row = data.Slice(r * stride, stride);
            var prev = data.Slice((r - 1) * stride, stride);

            // 첫 픽셀: left = upLeft = 0 → 예측 = up
            for (var c = 0; c < Channels; c++) row[c] = (byte)(row[c] + prev[c]);

            for (var i = Channels; i < row.Length; i++)
            {
                var prediction = row[i - Channels] + prev[i] - prev[i - Channels];
                row[i] = (byte)(row[i] + (prediction < 0 ? 0 : prediction > 255 ? 255 : prediction));
            }
        }
    }

    /// <summary>RGB(3바이트/px) → 프레임버퍼 행에 uint 단위로 기록.</summary>
    private void WriteRgbToFramebuffer(byte[] rgb, int x, int y, int w, int h)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(Framebuffer.AsSpan());
        var srcStride = w * 3;
        for (var r = 0; r < h; r++)
        {
            var src = rgb.AsSpan(r * srcStride, srcStride);
            var dst = pixels.Slice((y + r) * FramebufferWidth + x, w);
            for (var px = 0; px < dst.Length; px++)
            {
                var s = px * 3;
                dst[px] = ToBgrx(src[s], src[s + 1], src[s + 2]);
            }
        }
    }

    /// <summary>ValueTask — 동기 완료(수신 버퍼에서 바로 채움) 시 Task 할당 없음.</summary>
    private async ValueTask ReadExactlyAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(offset + read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException(Res.T("RfbClient_19")); // 서버의 정상 종료와 오류를 구분하기 위한 전용 예외

            read += n;
            Interlocked.Add(ref _bytesReceived, n);
        }
    }

    private async Task WriteAsync(byte[] payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static int ParseVersionPart(string version, int offset)
    {
        return int.TryParse(version.AsSpan(offset, 3), out var v) ? v : 0;
    }

    /// <summary>ArrayPool 에서 빌린 버퍼(Length 까지만 유효) — 전송 루프가 보낸 뒤(또는 병합으로 버릴 때) 반환한다.</summary>
    private readonly record struct OutgoingMessage(byte[] Buffer, int Length, bool IsPointerMove);

    /// <summary>
    ///     RFB zlib 연속 스트림 해제기 — 청크 큐 방식. 지속 DeflateStream 이 슬라이딩 윈도우를 유지한 채 순차로 읽는다.
    ///     청크 버퍼는 ArrayPool 소유권을 넘겨받아, 모두 소비되거나 Reset/Dispose 될 때 반환한다.
    /// </summary>
    private sealed class ZlibContinuousInflate : IDisposable
    {
        private const int ZlibHeaderLength = 2;

        private readonly Queue<(byte[] Buffer, int Length)> _chunks = new();
        private int _chunkOffset;
        private bool _headerSkipped;
        private DeflateStream? _inflate;

        public void Dispose()
        {
            Reset();
        }

        public void Reset()
        {
            _inflate?.Dispose();
            _inflate = null;
            _headerSkipped = false;
            while (_chunks.TryDequeue(out var chunk)) ArrayPool<byte>.Shared.Return(chunk.Buffer);

            _chunkOffset = 0;
        }

        /// <summary>ArrayPool 에서 빌린 버퍼의 소유권을 넘겨받는다(호출자는 반환하지 않는다).</summary>
        public void AddCompressedChunk(byte[] rentedBuffer, int length)
        {
            _chunks.Enqueue((rentedBuffer, length));
        }

        public int Decompress(byte[] output, int offset, int count)
        {
            if (_inflate is null)
            {
                if (!_headerSkipped)
                {
                    _chunkOffset = ZlibHeaderLength; // 스트림 최초 2바이트 zlib 헤더 건너뜀(DeflateStream 은 raw deflate)
                    _headerSkipped = true;
                }

                _inflate = new DeflateStream(
                    new ChunkReadStream(this), CompressionMode.Decompress, true);
            }

            var read = 0;
            while (read < count)
            {
                var n = _inflate.Read(output, offset + read, count - read);
                if (n == 0) break;

                read += n;
            }

            return read;
        }

        private sealed class ChunkReadStream(ZlibContinuousInflate owner) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <summary>DeflateStream 은 Span 오버로드를 호출한다 — 재정의하지 않으면 기본 구현이 임시 배열로 한 번 더 복사한다.</summary>
            public override int Read(Span<byte> destination)
            {
                var totalRead = 0;
                while (totalRead < destination.Length && owner._chunks.TryPeek(out var chunk))
                {
                    var available = chunk.Length - owner._chunkOffset;
                    if (available <= 0)
                    {
                        ArrayPool<byte>.Shared.Return(owner._chunks.Dequeue().Buffer);
                        owner._chunkOffset = 0;
                        continue;
                    }

                    var toRead = Math.Min(destination.Length - totalRead, available);
                    chunk.Buffer.AsSpan(owner._chunkOffset, toRead).CopyTo(destination[totalRead..]);
                    owner._chunkOffset += toRead;
                    totalRead += toRead;
                }

                return totalRead;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return Read(buffer.AsSpan(offset, count));
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}