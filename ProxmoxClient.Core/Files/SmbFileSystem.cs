using System.Formats.Tar;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Files;

/// <summary>
///     SMB(Windows 파일 공유) 게스트 파일 시스템 — \\주소\공유 아래를 PC 의 Windows SMB 클라이언트로 다룬다
///     (라이브러리 없이 일반 파일 API). 경로는 UNC(\\주소\공유\폴더), 이름은 대소문자를 가리지 않는다.
///     작업은 한 번에 하나씩, 네트워크를 기다리는 동안 UI 를 막지 않게 스레드 풀에서 한다.
/// </summary>
public sealed class SmbFileSystem : IGuestFileSystem
{
    private const int CopyBuffer = 1024 * 1024;
    private const int ReportEvery = 500;
    private static readonly char[] BadNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private readonly SmbConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;

    private SmbFileSystem(string root, SmbConnection connection)
    {
        _root = root;
        _connection = connection;
    }

    public char Separator => '\\';
    public StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;
    public string HomePath => _root;
    public bool CanDownloadDirectory => true;

    /// <summary>\\host\share 에 붙고 공유가 보이는지 확인한다(계정을 비우면 지금 Windows 로그인으로).</summary>
    public static Task<SmbFileSystem> OpenAsync(string host, string share, string? userName, string? password,
        CancellationToken ct = default)
    {
        var root = $@"\\{UncHost(host)}\{share.Trim('\\', '/').Replace('/', '\\')}";
        return Task.Run(() =>
        {
            var connection = SmbConnection.Open(root, userName, password);
            try
            {
                CheckReachable(root);
                return new SmbFileSystem(root, connection);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }, ct);
    }

    /// <summary>공유가 보이는지 — 없으면 "찾을 수 없음", 접근이 막혔으면 로그인 실패(계정을 적게)로 알린다.</summary>
    private static void CheckReachable(string root)
    {
        if (Directory.Exists(root)) return;

        try
        {
            _ = Directory.EnumerateFileSystemEntries(root).FirstOrDefault();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SmbAuthenticationException(Res.T("Smb_LogonFailed", ex.Message));
        }
        catch (IOException)
        {
            // 아래에서 찾을 수 없다고 알린다
        }

        throw new GuestFileException(Res.T("Smb_ShareNotFound", root));
    }

    /// <summary>UNC 에 쓸 주소 — IPv6 는 ipv6-literal.net 이름으로(UNC 는 ':' 를 받지 않는다).</summary>
    internal static string UncHost(string host)
    {
        host = host.Trim().Trim('[', ']');
        return host.Contains(':') ? host.Replace(':', '-').Replace('%', 's') + ".ipv6-literal.net" : host;
    }

    public Task<IReadOnlyList<GuestFileEntry>> ListAsync(string directory, CancellationToken ct = default) =>
        ListAsync(directory, null, ct);

    public Task<IReadOnlyList<GuestFileEntry>> ListAsync(string directory,
        Action<IReadOnlyList<GuestFileEntry>>? partial, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            var entries = new List<GuestFileEntry>();
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
            {
                ct.ThrowIfCancellationRequested();
                entries.Add(ToEntry(info));
                if (partial is not null && entries.Count % ReportEvery == 0) partial(entries.ToArray());
            }

            return (IReadOnlyList<GuestFileEntry>)entries;
        }, ct);

    private static GuestFileEntry ToEntry(FileSystemInfo info)
    {
        var attributes = info.Attributes;
        var isLink = attributes.HasFlag(FileAttributes.ReparsePoint);
        var isDirectory = info is DirectoryInfo;
        var kind = isLink ? GuestFileKind.Link : isDirectory ? GuestFileKind.Directory : GuestFileKind.File;
        var size = info is FileInfo file && !isLink ? file.Length : 0;
        DateTime? modified = info.LastWriteTimeUtc.Year > 1601 ? info.LastWriteTimeUtc : null;
        string? target = null;
        if (isLink)
            try
            {
                target = info.LinkTarget;
            }
            catch (IOException)
            {
                // 대상을 읽지 못하는 링크(원격 정션 등) — 링크 경로로만 연다
            }

        return new GuestFileEntry(info.Name, kind, size, modified, Mode(attributes, isDirectory), string.Empty,
            string.Empty, target, isLink && isDirectory);
    }

    /// <summary>PowerShell Mode 와 같은 모양(d a r h s l) — 숨김·시스템(h·s) 판정에 쓴다.</summary>
    private static string Mode(FileAttributes a, bool directory) => string.Concat(
        directory ? 'd' : '-', a.HasFlag(FileAttributes.Archive) ? 'a' : '-',
        a.HasFlag(FileAttributes.ReadOnly) ? 'r' : '-', a.HasFlag(FileAttributes.Hidden) ? 'h' : '-',
        a.HasFlag(FileAttributes.System) ? 's' : '-', a.HasFlag(FileAttributes.ReparsePoint) ? 'l' : '-');

    public Task DownloadAsync(string directory, GuestFileEntry entry, Stream destination, IProgress<long>? progress,
        CancellationToken ct = default) =>
        RunAsync(() =>
        {
            var path = Combine(directory, entry.Name);
            if (entry.IsDirectory) WriteTar(path, entry.Name, destination, progress, ct);
            else CopyFile(path, destination, progress, ct);
            return true;
        }, ct);

    private static long CopyFile(string path, Stream destination, IProgress<long>? progress, CancellationToken ct,
        long done = 0)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, CopyBuffer);
        var buffer = new byte[CopyBuffer];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            done += read;
            progress?.Report(done);
        }

        return done;
    }

    /// <summary>폴더를 tar 로 — 안의 정션·링크는 따라가지 않는다(같은 폴더를 돌거나 공유 밖으로 나가지 않게).</summary>
    private static void WriteTar(string path, string name, Stream destination, IProgress<long>? progress,
        CancellationToken ct)
    {
        using var writer = new TarWriter(destination, TarEntryFormat.Pax, true);
        long done = 0;
        var pending = new Stack<(string Path, string Name)>();
        pending.Push((path, name));
        while (pending.Count > 0)
        {
            var (folder, relative) = pending.Pop();
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, relative + "/"));
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                var child = relative + "/" + info.Name;
                if (info is DirectoryInfo) pending.Push((info.FullName, child));
                else done = WriteTarFile(writer, (FileInfo)info, child, done, progress);
            }
        }
    }

    private static long WriteTarFile(TarWriter writer, FileInfo file, string name, long done, IProgress<long>? progress)
    {
        using var data = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, CopyBuffer);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = data, ModificationTime = file.LastWriteTimeUtc
        });
        done += file.Length;
        progress?.Report(done);
        return done;
    }

    /// <summary>임시 이름으로 받은 뒤 바꿔 넣는다 — 덮어쓰면 기존 권한·속성을 잇는다(File.Replace).</summary>
    public Task UploadAsync(string directory, string name, Stream source, long length, IProgress<long>? progress,
        CancellationToken ct = default) =>
        RunAsync(() =>
        {
            var target = Combine(directory, CheckName(name));
            var temp = Combine(directory, ".pvc-up." + Guid.NewGuid().ToString("N"));
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           CopyBuffer))
                    CopyStream(source, output, progress, ct);

                if (Directory.Exists(target)) throw new GuestFileException(Res.T("Remote_FolderExists", name));
                if (File.Exists(target)) File.Replace(temp, target, null, true);
                else File.Move(temp, target);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }

            return true;
        }, ct);

    private static void CopyStream(Stream source, Stream output, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[CopyBuffer];
        long done = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            done += read;
            progress?.Report(done);
        }
    }

    public Task CreateDirectoryAsync(string directory, string name, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            var path = Combine(directory, CheckName(name));
            if (Directory.Exists(path) || File.Exists(path)) throw new GuestFileException(Res.T("Remote_Exists", name));
            Directory.CreateDirectory(path);
            return true;
        }, ct);

    /// <summary>이름 바꾸기 — 대소문자만 바꾸는 것도 된다(폴더는 임시 이름을 거친다).</summary>
    public Task RenameAsync(string directory, string name, string newName, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            var from = Combine(directory, name);
            var to = Combine(directory, CheckName(newName));
            var caseOnly = NameComparer.Equals(name, newName);
            if (!caseOnly && (File.Exists(to) || Directory.Exists(to)))
                throw new GuestFileException(Res.T("Remote_Exists", newName));

            if (!Directory.Exists(from)) File.Move(from, to);
            else if (!caseOnly) Directory.Move(from, to);
            else
            {
                var hop = Combine(directory, ".pvc-mv." + Guid.NewGuid().ToString("N"));
                Directory.Move(from, hop);
                Directory.Move(hop, to);
            }

            return true;
        }, ct);

    /// <summary>지우기 — 링크(정션)는 링크만, 폴더는 안까지(정션 안으로는 들어가지 않는다). 읽기 전용도 지운다.</summary>
    public Task DeleteAsync(string directory, GuestFileEntry entry, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            var path = Combine(directory, entry.Name);
            var info = new DirectoryInfo(path);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) info.Delete();
            else if (info.Exists) DeleteTree(info);
            else
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }

            return true;
        }, ct);

    private static void DeleteTree(DirectoryInfo root)
    {
        foreach (var file in root.EnumerateFiles("*", new EnumerationOptions
                     { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            if (file.IsReadOnly) file.IsReadOnly = false;
        root.Delete(true);
    }

    public string ResolveLink(string directory, GuestFileEntry link) => Combine(directory, link.Name);

    public string Parent(string directory)
    {
        var trimmed = directory.TrimEnd('\\');
        if (trimmed.Length <= _root.Length || !trimmed.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            return _root;

        var cut = trimmed.LastIndexOf('\\');
        return cut < _root.Length ? _root : trimmed[..cut];
    }

    public string Combine(string directory, string name) => directory.TrimEnd('\\') + "\\" + name;

    private static string CheckName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(BadNameChars) >= 0 || name.Any(char.IsControl))
            throw new GuestFileException(Res.T("GuestFiles_BadName", name));
        return name;
    }

    /// <summary>한 번에 하나씩, 스레드 풀에서 — 파일 시스템 오류는 게스트 오류로 알린다.</summary>
    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(work, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GuestFileException(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }
}
