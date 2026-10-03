using Devolutions.IronRdp;
using ProxmoxClient.Core.Localization;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.Core.Rdp;

/// <summary>수신 — 서버 PDU 를 IronRDP 로 처리하고, 나온 결과(응답·화면·커서·재활성화)를 반영한다.</summary>
public sealed partial class ProxmoxRdpSession
{
    private async Task ReceiveLoopAsync(RdpChannel channel, CancellationToken token)
    {
        Exception? error = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var (action, payload) = await channel.ReadPduAsync().ConfigureAwait(false);
                Interlocked.Add(ref _statsBytes, payload.Length);

                Outputs outputs;
                lock (_gate)
                {
                    if (_stage is null || _image is null) return; // 닫는 중
                    using var iterator = _stage.Process(_image, action, payload);
                    outputs = Collect(iterator);
                }

                if (!await ApplyAsync(channel, outputs).ConfigureAwait(false)) break;
            }
        }
        catch (Exception ex) when (IsEndOfStream(ex))
        {
            // 서버가 연결을 닫았다 — 정상 종료
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            error = ex is IronRdpException rdp
                ? new InvalidOperationException(Res.T("Rdp_Failed", rdp.Inner.ToDisplay()), ex)
                : ex;
        }
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            _ = ex; // 우리가 닫았다(창 닫기·재연결) — 알릴 곳이 없다
        }
        finally
        {
            IsConnected = false;
            channel.Complete();
            if (!token.IsCancellationRequested) Closed?.Invoke(error);
            // 연결이 끝났다 — 입력·클립보드 루프도 멈춘다(클립보드 창을 두면 PC 에서 붙여 넣을 때마다 10초씩 멈춘다)
            _lifetimeCts.Cancel();
        }
    }

    /// <summary>
    ///     스트림 끝(서버가 닫음) — IronRDP 틀은 PDU 경계에서는 EndOfFile, PDU 중간에서는 일반 예외("EOF")를 던진다.
    /// </summary>
    private static bool IsEndOfStream(Exception ex)
    {
        return ex is IronRdpLibException { ErrorType: IronRdpLibExceptionType.EndOfFile }
               || (ex.GetType() == typeof(Exception) && ex.Message == "EOF");
    }

    /// <summary>
    ///     (잠금 안에서) IronRDP 결과를 모은다 — 보낼 응답은 이 자리에서 줄에 넣어 입력과 순서가 섞이지 않게 하고,
    ///     화면이 바뀌었으면 표시만 해 둔다(프레임버퍼로 옮기는 것은 창이 그릴 때).
    /// </summary>
    private Outputs Collect(ActiveStageOutputIterator iterator)
    {
        var outputs = new Outputs();
        while (!iterator.IsEmpty() && iterator.Next() is { } next)
        {
            using var output = next;
            switch (output.GetEnumType())
            {
                case ActiveStageOutputType.ResponseFrame:
                    var frame = ToBytes(output.GetResponseFrame());
                    outputs.Frames.Add(frame);
                    _channel?.Enqueue(frame);
                    break;
                case ActiveStageOutputType.GraphicsUpdate:
                    outputs.AddDirty(output.GetGraphicsUpdate());
                    break;
                case ActiveStageOutputType.PointerBitmap:
                    outputs.Cursor = ToCursor(output.GetPointerBitmap());
                    break;
                case ActiveStageOutputType.PointerHidden:
                    outputs.Cursor = new RfbCursor([], 0, 0, 0, 0);
                    break;
                case ActiveStageOutputType.PointerDefault:
                    outputs.Cursor = null;
                    outputs.CursorDefault = true;
                    break;
                case ActiveStageOutputType.Terminate:
                    outputs.Terminate = true;
                    break;
                case ActiveStageOutputType.DeactivateAll:
                    outputs.Activation = output.GetDeactivateAll();
                    _reactivating = true; // 잠금을 놓기 전에 — 그 틈에 입력이 옛 채널로 나가지 않게
                    break;
                // PointerPosition: 입력이 절대 좌표라 게스트가 옮긴 커서 위치는 쓰지 않는다
            }
        }

        if (outputs.Dirty is not null) _framebufferStale = true;

        return outputs;
    }

    /// <summary>(잠금 밖에서) 화면·커서 알림과 재활성화. 서버가 세션을 끝냈으면 false.</summary>
    private async Task<bool> ApplyAsync(RdpChannel channel, Outputs outputs)
    {
        if (Interlocked.Exchange(ref _resignalFrame, 0) == 1)
            FrameReceived?.Invoke(0, 0, _width, _height); // 창이 처리 중에 옛 화면을 가져갔다 — 다시 그리게
        else if (outputs.Dirty is { } dirty)
            FrameReceived?.Invoke(dirty.Left, dirty.Top, dirty.Right - dirty.Left + 1, dirty.Bottom - dirty.Top + 1);

        if (outputs.Dirty is not null) Interlocked.Increment(ref _statsFrames);

        if (outputs.Cursor is { } cursor) CursorShape?.Invoke(cursor);
        else if (outputs.CursorDefault) CursorDefault?.Invoke();

        if (outputs.Activation is { } activation) await ReactivateAsync(channel, activation).ConfigureAwait(false);

        return !outputs.Terminate;
    }

    /// <summary>
    ///     서버가 해상도를 바꿨다(Deactivate-All) — 연결 활성화 단계를 다시 밟고 새 크기로 버퍼를 만든다.
    ///     그동안 들어온 입력은 붙잡아 두었다가(옛 채널로 나가지 않게) 끝나면 보낸다.
    /// </summary>
    private async Task ReactivateAsync(RdpChannel channel, ConnectionActivationSequence activation)
    {
        using var sequence = activation;
        try
        {
            using var buf = WriteBuf.New();
            while (true)
            {
                using var state = sequence.GetState();
                if (state.Type == ConnectionActivationStateType.Finalized) break;

                await channel.StepAsync(sequence, buf).ConfigureAwait(false);
            }

            using var done = sequence.GetState();
            using var finalized = done.GetFinalized();
            using var size = finalized.GetDesktopSize();
            lock (_gate)
            {
                if (_stage is null) return; // 그사이 닫혔다
                _stage.SetFastpathProcessor(finalized.GetIoChannelId(), finalized.GetUserChannelId(),
                    finalized.GetEnableServerPointer(), finalized.GetPointerSoftwareRendering());
                ResetImage(size);
            }

            // 창은 프레임버퍼 크기가 바뀐 것을 다음 갱신에서 알아챈다 — 전체를 한 번 알린다
            FrameReceived?.Invoke(0, 0, _width, _height);
        }
        finally
        {
            lock (_gate)
            {
                _reactivating = false;
                FlushHeldClipboardFrames();
                FlushHeldInputs();
            }
        }
    }

    private static byte[] ToBytes(BytesSlice slice)
    {
        using var owned = slice;
        var bytes = new byte[(int)slice.GetSize()];
        slice.Fill(bytes);
        return bytes;
    }

    /// <summary>IronRDP 커서(RGBA, 알파 곱하지 않음) → 콘솔 창 커서(BGRA).</summary>
    private static RfbCursor ToCursor(DecodedPointer pointer)
    {
        var pixels = ToBytes(pointer.GetData());
        for (var i = 0; i + 3 < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);

        return new RfbCursor(pixels, pointer.GetWidth(), pointer.GetHeight(), pointer.GetHotspotX(),
            pointer.GetHotspotY());
    }

    /// <summary>한 번의 처리에서 나온 결과 묶음.</summary>
    private sealed class Outputs
    {
        public List<byte[]> Frames { get; } = [];
        public (int Left, int Top, int Right, int Bottom)? Dirty { get; private set; }
        public RfbCursor? Cursor { get; set; }
        public bool CursorDefault { get; set; }
        public bool Terminate { get; set; }
        public ConnectionActivationSequence? Activation { get; set; }

        /// <summary>바뀐 영역을 하나로 합친다(포함 좌표).</summary>
        public void AddDirty(InclusiveRectangle rect)
        {
            var next = (Left: (int)rect.GetLeft(), Top: (int)rect.GetTop(), Right: (int)rect.GetRight(),
                Bottom: (int)rect.GetBottom());
            Dirty = Dirty is { } d
                ? (Math.Min(d.Left, next.Left), Math.Min(d.Top, next.Top), Math.Max(d.Right, next.Right),
                    Math.Max(d.Bottom, next.Bottom))
                : next;
        }
    }
}
