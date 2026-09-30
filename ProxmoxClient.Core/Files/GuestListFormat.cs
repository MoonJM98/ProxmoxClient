using System.Globalization;
using System.Text;

namespace ProxmoxClient.Core.Files;

/// <summary>
///     게스트 목록 형식(CT·Linux VM·Windows VM 공통) — 항목마다 NUL 로 끝나는 세 필드:
///     "종류/크기/수정(유닉스 초, 소수 가능)/권한/소유자/그룹", 이름, 링크 대상. 종류는 find %y 글자(d·f·l, 그 밖은 기타).
///     에이전트 출력은 이 바이트 전체를 base64 한 글 하나로 싣는다(에이전트 출력의 글자 변환을 피하려고).
/// </summary>
internal static class GuestListFormat
{
    /// <summary>에이전트 출력(base64) → 항목.</summary>
    public static IReadOnlyList<GuestFileEntry> ParseBase64(string output)
    {
        var bytes = Convert.FromBase64String(output.Trim());
        var reader = new GuestRecordStream((meta, name, link) => Entry(meta, name, link), null);
        reader.Write(bytes, 0, bytes.Length);
        return reader.Entries;
    }

    /// <summary>필드 셋 → 항목. owner·group 은 번호를 이름으로 바꿀 때(없으면 받은 그대로).</summary>
    public static GuestFileEntry Entry(string meta, string name, string link, Func<string, string>? owner = null,
        Func<string, string>? group = null)
    {
        var m = meta.Split('/');
        var kind = m[0] switch
        {
            "d" => GuestFileKind.Directory,
            "f" => GuestFileKind.File,
            "l" => GuestFileKind.Link,
            _ => GuestFileKind.Other
        };
        long.TryParse(m.ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
        DateTime? modified = double.TryParse(m.ElementAtOrDefault(2), NumberStyles.Float, CultureInfo.InvariantCulture,
                                 out var seconds) && seconds is > 0 and < 253402300799
            ? DateTime.UnixEpoch.AddSeconds(seconds)
            : null;
        var user = m.ElementAtOrDefault(4) ?? string.Empty;
        var grp = m.ElementAtOrDefault(5) ?? string.Empty;
        return new GuestFileEntry(name, kind, size, modified, m.ElementAtOrDefault(3) ?? string.Empty,
            owner is null ? user : owner(user), group is null ? grp : group(grp),
            kind == GuestFileKind.Link && link.Length > 0 ? link : null);
    }
}

/// <summary>
///     목록 레코드를 받는 대로 읽는 스트림 — CT 노드 셸처럼 출력이 흘러 들어오면 큰 폴더도 앞부분부터 보인다.
///     partial 은 지금까지 읽은 목록(누적)을 가끔(<see cref="ReportEvery" />개·<see cref="ReportInterval" /> 마다) 알린다.
///     Write 를 부르는 스레드에서 알린다.
/// </summary>
internal sealed class GuestRecordStream(Func<string, string, string, GuestFileEntry> make,
    Action<IReadOnlyList<GuestFileEntry>>? partial) : Stream
{
    private const int ReportEvery = 200;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(150);

    private readonly List<GuestFileEntry> _entries = [];
    private readonly MemoryStream _field = new();
    private readonly List<string> _fields = new(3);
    private long _lastReport = Environment.TickCount64;
    private int _reported;

    /// <summary>읽은 항목(끝까지 받은 뒤 쓴다).</summary>
    public IReadOnlyList<GuestFileEntry> Entries => _entries;

    public override void Write(byte[] buffer, int offset, int count)
    {
        for (var i = offset; i < offset + count; i++)
        {
            if (buffer[i] != 0)
            {
                _field.WriteByte(buffer[i]);
                continue;
            }

            _fields.Add(Encoding.UTF8.GetString(_field.GetBuffer(), 0, (int)_field.Length));
            _field.SetLength(0);
            if (_fields.Count < 3) continue;

            if (_fields[1].Length > 0) _entries.Add(make(_fields[0], _fields[1], _fields[2]));
            _fields.Clear();
        }

        Report();
    }

    private void Report()
    {
        if (partial is null || _entries.Count - _reported < ReportEvery) return;
        if (Environment.TickCount64 - _lastReport < ReportInterval.TotalMilliseconds) return;

        _reported = _entries.Count;
        _lastReport = Environment.TickCount64;
        partial(_entries.ToArray());
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
