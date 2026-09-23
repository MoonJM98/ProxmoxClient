using System.Net.Http.Headers;
using System.Text.Json;

namespace ProxmoxClient.Core.Api;

public sealed partial class ProxmoxApiClient
{
    /// <summary>업로드 버퍼 — 너무 작으면 느리고, 크게 잡아도 이득이 거의 없다.</summary>
    private const int UploadBufferSize = 1 << 20;

    /// <summary>
    ///     로컬 파일을 저장소에 올린다(POST nodes/{node}/storage/{storage}/upload) — ISO·CT 템플릿 등.
    ///     파일은 메모리에 통째로 읽지 않고 흘려 보내며, 수 GB 파일도 올릴 수 있도록 제한 시간을 두지 않는다.
    ///     서버는 받은 파일을 옮기는 작업(UPID)을 돌려준다.
    /// </summary>
    /// <param name="content">서버 콘텐츠 종류: iso, vztmpl, import.</param>
    /// <param name="progress">보낸 비율(0~1).</param>
    public async Task<string> UploadToStorageAsync(string node, string storage, string content, string filePath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post,
                    $"nodes/{Escape(node)}/storage/{Escape(storage)}/upload")
                {
                    Content = BuildUploadForm(content, filePath, progress)
                }, true, ct, Timeout.InfiniteTimeSpan)
            .ConfigureAwait(false);
        using var doc = await ReadJsonDocumentAsync(response, ct).ConfigureAwait(false);
        var data = DataElement(doc);
        return data.ValueKind == JsonValueKind.String ? data.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>
    ///     업로드 본문 — 서버는 파일보다 앞선 필드만 읽으므로 content 를 먼저 넣고, 필드 이름은 브라우저처럼
    ///     따옴표로 감싼다(서버 쪽 multipart 파서가 이 형식을 기대한다). 파일 길이를 알려 청크 전송을 피한다.
    /// </summary>
    internal static MultipartFormDataContent BuildUploadForm(string content, string filePath,
        IProgress<double>? progress)
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
            new ProgressStream(File.OpenRead(filePath), length, progress), UploadBufferSize);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"filename\"", FileName = $"\"{fileName.Replace("\"", "")}\""
        };
        form.Add(file);
        return form;
    }

    /// <summary>
    ///     읽은 양을 알려 주는 읽기 전용 스트림 — 업로드 진행률용. 길이·위치는 원래 파일 스트림을 그대로 따라
    ///     요청에 Content-Length 가 붙게 한다(청크 전송은 서버가 받지 못한다).
    /// </summary>
    private sealed class ProgressStream(Stream inner, long length, IProgress<double>? progress) : Stream
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
