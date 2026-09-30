using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ProxmoxClient.Core.Vnc;

/// <summary>
///     클립보드 — 확장 클립보드(Extended Clipboard, 의사 인코딩 0xC0A1E5CE)로 UTF-8 글(한글 포함)을 주고받는다.
///     QEMU 는 VM 디스플레이에 clipboard=vnc 가 켜져 있고 게스트에 spice-vdagent 가 있을 때 이것으로 게스트 클립보드를
///     잇는다(웹 UI noVNC 와 같은 방식). 흐름: 서버 능력(caps) → 알림(notify) → 요청(request) → 제공(provide).
///     서버가 확장 클립보드를 모르면 기본 ClientCutText(라틴 문자만)로 보낸다.
/// </summary>
public sealed partial class RfbClient
{
    private const int EncExtendedClipboard = unchecked((int)0xC0A1E5CE);
    private const uint ClipText = 1u;
    private const uint ClipCaps = 1u << 24;
    private const uint ClipRequest = 1u << 25;
    private const uint ClipPeek = 1u << 26;
    private const uint ClipNotify = 1u << 27;
    private const uint ClipProvide = 1u << 28;
    private const uint ClipFormatMask = 0xFFFF;

    /// <summary>주고받을 글 최대 크기 — 이보다 크면 받지 않는다(거대 할당 방지).</summary>
    private const int MaxClipboardBytes = 10 * 1024 * 1024;

    /// <summary>서버가 요청하면 넘겨줄 글(알림을 보낸 뒤 request 를 기다린다).</summary>
    private volatile string? _pendingClipboard;

    private uint _serverClipActions;

    /// <summary>서버가 확장 클립보드(UTF-8 글)를 쓴다고 알렸다 — 게스트 클립보드가 이어져 있다.</summary>
    public bool ExtendedClipboardSupported { get; private set; }

    /// <summary>게스트 클립보드에 글을 넣는다 — 확장 클립보드면 알림→요청→제공, 아니면 기본 전송.</summary>
    public Task SendClipboardTextAsync(string text, CancellationToken ct = default)
    {
        if (!ExtendedClipboardSupported || (_serverClipActions & ClipNotify) == 0)
            return SendClientCutTextAsync(text, ct);

        _pendingClipboard = text;
        return SendExtendedClipboardAsync(ClipNotify | ClipText, [], ct);
    }

    /// <summary>
    ///     게스트 클립보드를 지금 달라고 요청한다 — 서버가 제공(provide)으로 답하면 <see cref="ServerCutText" /> 가 불린다.
    ///     확장 클립보드를 쓰지 않는 서버면 아무 일도 하지 않는다.
    /// </summary>
    public Task RequestClipboardTextAsync(CancellationToken ct = default)
    {
        if (!ExtendedClipboardSupported || (_serverClipActions & ClipRequest) == 0) return Task.CompletedTask;

        return SendExtendedClipboardAsync(ClipRequest | ClipText, [], ct);
    }

    /// <summary>ServerCutText 의 길이가 음수면 확장 클립보드 메시지(길이 = 절댓값).</summary>
    private async Task HandleExtendedClipboardAsync(int length, CancellationToken ct)
    {
        if (length < 4 || length > MaxClipboardBytes)
        {
            await SkipAsync(length, ct).ConfigureAwait(false);
            return;
        }

        var message = new byte[length];
        await ReadExactlyAsync(message, 0, length, ct).ConfigureAwait(false);
        var flags = BinaryPrimitives.ReadUInt32BigEndian(message);
        var payload = message.AsSpan(4);
        var hasText = (flags & ClipText) != 0;

        if ((flags & ClipCaps) != 0)
        {
            _serverClipActions = flags & ~ClipFormatMask;
            ExtendedClipboardSupported = hasText;
            await SendClientCapsAsync(ct).ConfigureAwait(false);
        }
        else if ((flags & ClipRequest) != 0 && hasText)
        {
            await ProvideAsync(_pendingClipboard ?? string.Empty, ct).ConfigureAwait(false);
        }
        else if ((flags & ClipPeek) != 0)
        {
            var formats = _pendingClipboard is null ? 0u : ClipText;
            await SendExtendedClipboardAsync(ClipNotify | formats, [], ct).ConfigureAwait(false);
        }
        else if ((flags & ClipNotify) != 0 && hasText)
        {
            await SendExtendedClipboardAsync(ClipRequest | ClipText, [], ct).ConfigureAwait(false);
        }
        else if ((flags & ClipProvide) != 0 && hasText && ReadProvidedText(payload) is { } text)
        {
            ServerCutText?.Invoke(text);
        }
    }

    /// <summary>우리 능력 — 글 형식, 요청·엿보기·알림·제공, 글 최대 크기.</summary>
    private Task SendClientCapsAsync(CancellationToken ct)
    {
        var sizes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sizes, MaxClipboardBytes);
        return SendExtendedClipboardAsync(ClipCaps | ClipRequest | ClipPeek | ClipNotify | ClipProvide | ClipText,
            sizes, ct);
    }

    /// <summary>제공 — zlib 로 압축한 [u32 길이][UTF-8 글 + NUL].</summary>
    private Task ProvideAsync(string text, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(text + "\0");
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)body.Length);
            zlib.Write(size);
            zlib.Write(body);
        }

        return SendExtendedClipboardAsync(ClipProvide | ClipText, compressed.ToArray(), ct);
    }

    private static string? ReadProvidedText(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(payload.ToArray()), CompressionMode.Decompress);
            Span<byte> size = stackalloc byte[4];
            zlib.ReadExactly(size);
            var length = BinaryPrimitives.ReadUInt32BigEndian(size);
            if (length > MaxClipboardBytes) return null;

            var body = new byte[length];
            zlib.ReadExactly(body);
            return Encoding.UTF8.GetString(body).TrimEnd('\0');
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null; // 깨진 제공 메시지는 버린다(연결은 유지)
        }
    }

    /// <summary>ClientCutText 에 음수 길이로 담아 보낸다(확장 클립보드 형식).</summary>
    private Task SendExtendedClipboardAsync(uint flags, byte[] data, CancellationToken ct)
    {
        const int HeaderLength = 8;
        var bodyLength = 4 + data.Length;
        var length = HeaderLength + bodyLength;
        var msg = RentMessage(length);
        msg[0] = 6; // ClientCutText
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(4, 4), -bodyLength);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(HeaderLength, 4), flags);
        data.CopyTo(msg.AsSpan(HeaderLength + 4));
        return EnqueueAsync(msg, length);
    }
}
