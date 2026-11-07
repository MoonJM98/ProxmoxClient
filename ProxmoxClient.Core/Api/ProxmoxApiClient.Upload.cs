using System.Net.Http.Headers;
using System.Text.Json;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Api;

public sealed partial class ProxmoxApiClient
{
    /// <summary>업로드 버퍼 — 너무 작으면 느리고, 크게 잡아도 이득이 거의 없다.</summary>
    private const int UploadBufferSize = 1 << 20;

    /// <summary>보내는 도중 이만큼 파일을 더 읽어 가지 않으면(서버·회선이 멈춤) 끊긴 것으로 본다. 다 보낸 뒤 응답 대기에도 쓴다.</summary>
    private static readonly TimeSpan UploadIdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     로컬 파일을 저장소에 올린다(POST nodes/{node}/storage/{storage}/upload) — ISO·CT 템플릿 등.
    ///     파일은 메모리에 통째로 읽지 않고 흘려 보내며, 수 GB 파일도 올릴 수 있도록 전체 제한 시간은 두지 않는다.
    ///     대신 보내기 시작한 뒤 <see cref="UploadIdleTimeout" /> 넘게 진척이 없으면 멈춘다(쓰기 확인 대기에는 걸지 않는다).
    ///     서버는 받은 파일을 옮기는 작업(UPID)을 돌려준다.
    /// </summary>
    /// <param name="content">서버 콘텐츠 종류: iso, vztmpl, import.</param>
    /// <param name="progress">보낸 비율(0~1).</param>
    [Versioning.PveApi("POST", "/nodes/{node}/storage/{storage}/upload")]
    public async Task<string> UploadToStorageAsync(string node, string storage, string content, string filePath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void Touch()
        {
            try
            {
                idle.CancelAfter(UploadIdleTimeout); // 조각을 읽어 갈 때마다 다시 잰다
            }
            catch (ObjectDisposedException)
            {
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post,
                        $"nodes/{Escape(node)}/storage/{Escape(storage)}/upload")
                    {
                        Content = BuildUploadForm(content, filePath, progress, Touch)
                    }, true, idle.Token, Timeout.InfiniteTimeSpan)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProxmoxApiException(0, null, Res.T("ProxmoxApiClient_10"));
        }

        using var _ = response;
        using var doc = await ReadJsonDocumentAsync(response, ct).ConfigureAwait(false);
        var data = DataElement(doc);
        return data.ValueKind == JsonValueKind.String ? data.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>
    ///     열린 스트림(예: Android 파일 선택기의 content:// 스트림)을 임시 파일로 복사하지 않고 바로 올린다.
    ///     길이를 알아야 한다 — 서버는 청크 전송을 받지 못하므로 Content-Length 를 붙인다.
    ///     스트림은 끝나면 닫는다. 탐색이 안 되는 스트림은 401 뒤 다시 보낼 수 없어 실패한다
    ///     (보내기 전에 티켓을 갱신하므로 드물다).
    /// </summary>
    /// <param name="checksum">기대 체크섬과 알고리즘(md5·sha1·sha224·sha256·sha384·sha512) — 주면 서버가 받은 뒤 검사한다.</param>
    [Versioning.PveApi("POST", "/nodes/{node}/storage/{storage}/upload")]
    [Versioning.PveParam("checksum", "7.0")]
    public async Task<string> UploadToStorageAsync(string node, string storage, string content, Stream stream,
        long length, string fileName, (string Value, string Algorithm)? checksum = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // 요청은 여러 번 만들어질 수 있다(쓰기 확인용 미리 보기, 401 뒤 다시 보내기) — 요청이 버려져도 스트림이
        // 닫히지 않게 감싸고, 끝나면 여기서 닫는다. 탐색되는 스트림은 다시 보낼 때 처음으로 돌린다
        await using var owned = stream;
        var start = stream.CanSeek ? stream.Position : 0;
        // 파일 업로드와 같은 멈춤 감시 — 보내기 시작한 뒤 진척 없이 오래 있으면 끊는다
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void Touch()
        {
            try
            {
                idle.CancelAfter(UploadIdleTimeout);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        HttpResponseMessage sent;
        try
        {
            sent = await SendAsync(() =>
                    {
                        if (stream.CanSeek) stream.Position = start;
                        return new HttpRequestMessage(HttpMethod.Post,
                            $"nodes/{Escape(node)}/storage/{Escape(storage)}/upload")
                        {
                            Content = BuildUploadForm(content, new KeepOpenStream(stream), length, fileName, checksum,
                                progress, Touch)
                        };
                    }, true, idle.Token, Timeout.InfiniteTimeSpan)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProxmoxApiException(0, null, Res.T("ProxmoxApiClient_10"));
        }

        using var response = sent;
        using var doc = await ReadJsonDocumentAsync(response, ct).ConfigureAwait(false);
        var data = DataElement(doc);
        return data.ValueKind == JsonValueKind.String ? data.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>
    ///     파일 이름이 이 콘텐츠로 올릴 수 있는 확장자인가 — 서버와 같은 규칙(pve-storage ISO_EXT_RE_0 ·
    ///     VZTMPL_EXT_RE_1 · UPLOAD_IMPORT_EXT_RE_1). 아니면 서버가 다 받은 뒤 "wrong file extension" 으로 거절한다.
    /// </summary>
    public static bool IsUploadExtensionOk(string content, string fileName)
    {
        var name = fileName.ToLowerInvariant();
        string[] allowed = content switch
        {
            "iso" => [".iso", ".img"],
            "vztmpl" => [".tar", ".tar.gz", ".tar.xz", ".tar.zst", ".tar.bz2"],
            "import" => [".ova", ".qcow2", ".raw", ".vmdk"],
            _ => []
        };
        return allowed.Length == 0
               || allowed.Any(ext => name.Length > ext.Length && name.EndsWith(ext, StringComparison.Ordinal));
    }

    /// <summary>스트림 업로드 본문 — 필드(content·checksum)를 파일보다 먼저 넣고 파일 길이를 알린다.</summary>
    internal static MultipartFormDataContent BuildUploadForm(string content, Stream stream, long length,
        string fileName, (string Value, string Algorithm)? checksum, IProgress<double>? progress, Action? onRead = null)
    {
        var form = new MultipartFormDataContent();
        void Field(string name, string value)
        {
            var part = new StringContent(value);
            part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{name}\"" };
            form.Add(part);
        }

        Field("content", content);
        if (checksum is { Value.Length: > 0 } sum)
        {
            Field("checksum", sum.Value);
            Field("checksum-algorithm", sum.Algorithm);
        }

        var file = new StreamContent(new ProgressStream(stream, length, progress, onRead), UploadBufferSize);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // 선택기 스트림은 탐색이 안 돼 길이를 스스로 셀 수 없다 — 알려 준 길이를 그대로 쓴다(청크 전송 방지)
        file.Headers.ContentLength = length;
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"filename\"", FileName = $"\"{Path.GetFileName(fileName).Replace("\"", "")}\""
        };
        form.Add(file);
        return form;
    }

    /// <summary>
    ///     업로드 본문 — 서버는 파일보다 앞선 필드만 읽으므로 content 를 먼저 넣고, 필드 이름은 브라우저처럼
    ///     따옴표로 감싼다(서버 쪽 multipart 파서가 이 형식을 기대한다). 파일 길이를 알려 청크 전송을 피한다.
    /// </summary>
    internal static MultipartFormDataContent BuildUploadForm(string content, string filePath,
        IProgress<double>? progress, Action? onRead = null)
    {
        var fileName = Path.GetFileName(filePath);
        var length = new FileInfo(filePath).Length;
        var form = new MultipartFormDataContent();
        var contentPart = new StringContent(content);
        contentPart.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"content\""
        };
        form.Add(contentPart);

        var file = new StreamContent(
            new ProgressStream(File.OpenRead(filePath), length, progress, onRead), UploadBufferSize);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"filename\"", FileName = $"\"{fileName.Replace("\"", "")}\""
        };
        form.Add(file);
        return form;
    }

    /// <summary>닫아도 안쪽 스트림은 닫지 않는 읽기 전용 감싸개 — 버려진 요청이 호출자의 스트림을 닫지 않게.</summary>
    private sealed class KeepOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    ///     읽은 양을 알려 주는 읽기 전용 스트림 — 업로드 진행률용. 길이·위치는 원래 파일 스트림을 그대로 따라
    ///     요청에 Content-Length 가 붙게 한다(청크 전송은 서버가 받지 못한다).
    /// </summary>
    private sealed class ProgressStream(Stream inner, long length, IProgress<double>? progress, Action? onRead)
        : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => inner.Position;
            set
            {
                inner.Position = value;
                _read = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Report(inner.Read(buffer, offset, count));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return Report(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        }

        private int Report(int count)
        {
            onRead?.Invoke();
            _read += count;
            if (length > 0) progress?.Report((double)_read / length);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _read = inner.Seek(offset, origin);
            return _read;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
