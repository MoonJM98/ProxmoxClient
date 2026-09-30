namespace ProxmoxClient.Core.Files;

/// <summary>
///     폴더 목록 캐시 — 파일 창(연결)이 살아 있는 동안만 메모리에 둔다(디스크에 남기지 않는다).
///     한 번 연 폴더는 다시 갈 때 <see cref="Peek" /> 로 곧바로 보이고, 화면은 <see cref="RefreshAsync" /> 로
///     뒤에서 새로 읽어 바뀐 것만 반영한다. 이 창에서 바꾸는 작업(올리기·새 폴더·
///     이름 바꾸기·지우기)을 하면 — 실패해도 일부는 바뀌었을 수 있으므로 — 캐시를 모두 비운다(링크로 들어간 같은
///     폴더가 다른 경로로 남아 있을 수 있어 한 폴더만 비우지 않는다). 게스트 안에서 따로 바뀐 것은
///     <see cref="Invalidate" />(새로 고침)로 다시 읽는다.
/// </summary>
public sealed class CachedGuestFileSystem : IGuestFileSystem
{
    private readonly Dictionary<string, IReadOnlyList<GuestFileEntry>> _lists;
    private readonly Lock _lock = new();
    private int _version; // 비울 때마다 올린다 — 비우기 전에 시작한 목록은 캐시에 넣지 않는다
    private readonly IGuestFileSystem _inner;

    public CachedGuestFileSystem(IGuestFileSystem inner)
    {
        _inner = inner;
        // 폴더 경로도 게스트 규칙대로(Windows 는 대소문자 무시 — C:\Users 와 c:\users 는 같은 폴더)
        _lists = new Dictionary<string, IReadOnlyList<GuestFileEntry>>(inner.NameComparer);
    }

    public char Separator => _inner.Separator;
    public StringComparer NameComparer => _inner.NameComparer;
    public string HomePath => _inner.HomePath;
    public bool CanDownloadDirectory => _inner.CanDownloadDirectory;

    /// <summary>directory 를 캐시에서 뺀다 — 다음 목록은 게스트에서 새로 읽는다.</summary>
    public void Invalidate(string directory)
    {
        lock (_lock) _lists.Remove(directory);
    }

    /// <summary>캐시에 있는 목록(없으면 null) — 게스트에 묻지 않는다.</summary>
    public IReadOnlyList<GuestFileEntry>? Peek(string directory)
    {
        lock (_lock) return _lists.GetValueOrDefault(directory);
    }

    public async Task<IReadOnlyList<GuestFileEntry>> ListAsync(string directory, CancellationToken ct = default)
    {
        return Peek(directory) ?? await RefreshAsync(directory, null, ct);
    }

    public Task<IReadOnlyList<GuestFileEntry>> RefreshAsync(string directory, CancellationToken ct = default)
    {
        return RefreshAsync(directory, null, ct);
    }

    /// <summary>캐시와 상관없이 게스트에서 새로 읽고 캐시를 바꾼다 — 받는 도중 목록은 partial 로(캐시에는 끝난 것만).</summary>
    public async Task<IReadOnlyList<GuestFileEntry>> RefreshAsync(string directory,
        Action<IReadOnlyList<GuestFileEntry>>? partial, CancellationToken ct = default)
    {
        int version;
        lock (_lock) version = _version;
        var entries = await _inner.ListAsync(directory, partial, ct);
        lock (_lock)
        {
            // 읽는 사이 바꾸는 작업이 캐시를 비웠다면 이 목록은 그 전 것일 수 있다
            if (version == _version) _lists[directory] = entries;
        }

        return entries;
    }

    public Task DownloadAsync(string directory, GuestFileEntry entry, Stream destination, IProgress<long>? progress,
        CancellationToken ct = default)
    {
        return _inner.DownloadAsync(directory, entry, destination, progress, ct);
    }

    public Task UploadAsync(string directory, string name, Stream source, long length, IProgress<long>? progress,
        CancellationToken ct = default)
    {
        return ChangeAsync(() => _inner.UploadAsync(directory, name, source, length, progress, ct));
    }

    public Task CreateDirectoryAsync(string directory, string name, CancellationToken ct = default)
    {
        return ChangeAsync(() => _inner.CreateDirectoryAsync(directory, name, ct));
    }

    public Task RenameAsync(string directory, string name, string newName, CancellationToken ct = default)
    {
        return ChangeAsync(() => _inner.RenameAsync(directory, name, newName, ct));
    }

    public Task DeleteAsync(string directory, GuestFileEntry entry, CancellationToken ct = default)
    {
        return ChangeAsync(() => _inner.DeleteAsync(directory, entry, ct));
    }

    public string ResolveLink(string directory, GuestFileEntry link) => _inner.ResolveLink(directory, link);

    public string Parent(string directory) => _inner.Parent(directory);

    public string Combine(string directory, string name) => _inner.Combine(directory, name);

    public void Dispose()
    {
        Clear();
        _inner.Dispose();
    }

    private async Task ChangeAsync(Func<Task> change)
    {
        try
        {
            await change();
        }
        finally
        {
            Clear();
        }
    }

    private void Clear()
    {
        lock (_lock)
        {
            _lists.Clear();
            _version++;
        }
    }
}
