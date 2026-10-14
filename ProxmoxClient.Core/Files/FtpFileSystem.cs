using System.Formats.Tar;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using FluentFTP.Exceptions;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Files;

/// <summary>FTP 로그인 실패 — 비밀번호를 다시 물을 수 있다.</summary>
public sealed class FtpLoginException(string message) : GuestFileException(message);

/// <summary>
///     FTP·FTPS 게스트 파일 시스템(FluentFTP). 경로는 '/' 기준, 이름은 대소문자를 가린다.
///     FTPS 인증서는 시스템이 믿는 것이면 그대로, 아니면(자체 서명 등) <c>trustCertificate</c> 로 사용자에게 묻는다.
///     작업은 한 번에 하나씩(연결 하나를 같이 쓰므로).
/// </summary>
public sealed class FtpFileSystem : IGuestFileSystem
{
    private const int ConnectTimeoutMs = 10_000;
    private const int ReadTimeoutMs = 30_000;

    private readonly AsyncFtpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private FtpFileSystem(AsyncFtpClient client, string home)
    {
        _client = client;
        HomePath = home;
    }

    public char Separator => '/';
    public StringComparer NameComparer => StringComparer.Ordinal;
    public string HomePath { get; }
    public bool CanDownloadDirectory => true;

    /// <summary>
    ///     접속·로그인. trustCertificate(SHA-256 지문, 인증서) — 시스템이 믿지 않는 FTPS 인증서를 받아들일지
    ///     (통신 스레드에서 부른다). 사용자를 비우면 anonymous.
    /// </summary>
    public static async Task<FtpFileSystem> OpenAsync(string host, RemoteFileSettings settings, string? password,
        Func<string, X509Certificate2, bool> trustCertificate, CancellationToken ct = default)
    {
        var config = new FtpConfig
        {
            EncryptionMode = settings.Security switch
            {
                FtpSecurity.None => FtpEncryptionMode.None,
                FtpSecurity.Implicit => FtpEncryptionMode.Implicit,
                _ => FtpEncryptionMode.Explicit
            },
            ValidateAnyCertificate = false, ConnectTimeout = ConnectTimeoutMs, ReadTimeout = ReadTimeoutMs,
            DataConnectionConnectTimeout = ConnectTimeoutMs, DataConnectionReadTimeout = ReadTimeoutMs,
            DataConnectionType = FtpDataConnectionType.AutoPassive, RetryAttempts = 1
        };
        var user = settings.UserName.Length == 0 ? "anonymous" : settings.UserName;
        var client = new AsyncFtpClient(host, user, password ?? string.Empty, settings.FtpPortOrDefault, config);
        client.ValidateCertificate += (_, e) => e.Accept = e.PolicyErrors == SslPolicyErrors.None
                                                           || Trust(e.Certificate, trustCertificate);
        try
        {
            await client.Connect(ct).ConfigureAwait(false);
            var home = await client.GetWorkingDirectory(ct).ConfigureAwait(false);
            return new FtpFileSystem(client, string.IsNullOrEmpty(home) ? "/" : home);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw Translate(ex, ct);
        }
    }

    private static bool Trust(X509Certificate certificate, Func<string, X509Certificate2, bool> trustCertificate)
    {
        using var cert = new X509Certificate2(certificate);
        return trustCertificate(Convert.ToHexString(SHA256.HashData(cert.RawData)), cert);
    }

    public Task<IReadOnlyList<GuestFileEntry>> ListAsync(string directory, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var items = await _client.GetListing(directory, FtpListOption.AllFiles, ct).ConfigureAwait(false);
            return (IReadOnlyList<GuestFileEntry>)items.Where(i => i.Name is not ("." or "..")).Select(ToEntry)
                .ToList();
        }, ct);

    private static GuestFileEntry ToEntry(FtpListItem item)
    {
        var kind = item.Type switch
        {
            FtpObjectType.Directory => GuestFileKind.Directory,
            FtpObjectType.Link => GuestFileKind.Link,
            _ => GuestFileKind.File
        };
        DateTime? modified = item.Modified > DateTime.MinValue
            ? DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc)
            : null;
        // 숨김 판정이 글자(h·s)를 보므로 rwx 글자 대신 8진수만 둔다
        var mode = item.Chmod > 0 ? Convert.ToString(item.Chmod, 8) : string.Empty;
        return new GuestFileEntry(item.Name, kind, kind == GuestFileKind.File ? item.Size : 0, modified, mode,
            item.RawOwner ?? string.Empty, item.RawGroup ?? string.Empty,
            string.IsNullOrEmpty(item.LinkTarget) ? null : item.LinkTarget,
            item.LinkObject?.Type == FtpObjectType.Directory);
    }

    public Task DownloadAsync(string directory, GuestFileEntry entry, Stream destination, IProgress<long>? progress,
        CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var path = Combine(directory, entry.Name);
            var work = entry.IsDirectory
                ? WriteTarAsync(path, entry.Name, destination, progress, ct)
                : DownloadFileAsync(path, destination, progress, 0, ct);
            await work.ConfigureAwait(false);
            return true;
        }, ct);

    private async Task DownloadFileAsync(string path, Stream destination, IProgress<long>? progress, long offset,
        CancellationToken ct)
    {
        var report = progress is null ? null : new FtpProgressMap(progress, offset);
        if (!await _client.DownloadStream(destination, path, 0, report, ct).ConfigureAwait(false))
            throw new GuestFileException(Res.T("Remote_DownloadFailed", path));
    }

    /// <summary>폴더를 tar 로 — 파일마다 PC 임시 파일로 받아 넣는다(크기를 미리 알아야 해서). 링크는 따라가지 않는다.</summary>
    private async Task WriteTarAsync(string path, string name, Stream destination, IProgress<long>? progress,
        CancellationToken ct)
    {
        await using var writer = new TarWriter(destination, TarEntryFormat.Pax, true);
        long done = 0;
        var pending = new Stack<(string Path, string Name)>();
        pending.Push((path, name));
        while (pending.Count > 0)
        {
            var (folder, relative) = pending.Pop();
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, relative + "/"), ct)
                .ConfigureAwait(false);
            foreach (var item in await _client.GetListing(folder, FtpListOption.AllFiles, ct).ConfigureAwait(false))
            {
                if (item.Name is "." or ".." || item.Type == FtpObjectType.Link) continue;

                var child = relative + "/" + item.Name;
                if (item.Type == FtpObjectType.Directory) pending.Push((Combine(folder, item.Name), child));
                else done = await WriteTarFileAsync(writer, item, child, done, progress, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<long> WriteTarFileAsync(TarWriter writer, FtpListItem item, string name, long done,
        IProgress<long>? progress, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "pvc-ftp-" + Guid.NewGuid().ToString("N"));
        await using var data = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose);
        await DownloadFileAsync(item.FullName, data, progress, done, ct).ConfigureAwait(false);
        data.Position = 0;
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data };
        if (item.Modified > DateTime.MinValue) entry.ModificationTime = DateTime.SpecifyKind(item.Modified,
            DateTimeKind.Utc);
        await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
        return done + data.Length;
    }

    /// <summary>임시 이름으로 올린 뒤 바꾼다 — 끊겨도 기존 파일이 반쪽으로 남지 않게.</summary>
    public Task UploadAsync(string directory, string name, Stream source, long length, IProgress<long>? progress,
        CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var target = Combine(directory, GuestPaths.CheckName(name));
            var temp = Combine(directory, ".pvc-up." + Guid.NewGuid().ToString("N"));
            var report = progress is null ? null : new FtpProgressMap(progress, 0);
            try
            {
                var status = await _client.UploadStream(source, temp, FtpRemoteExists.NoCheck, false, report, ct)
                    .ConfigureAwait(false);
                if (status != FtpStatus.Success) throw new GuestFileException(Res.T("Remote_UploadFailed", name));
                if (await _client.DirectoryExists(target, ct).ConfigureAwait(false))
                    throw new GuestFileException(Res.T("Remote_FolderExists", name));
                await ReplaceAsync(temp, target, directory).ConfigureAwait(false);
                temp = null;
            }
            finally
            {
                if (temp is not null) await DeleteQuietlyAsync(temp).ConfigureAwait(false);
            }

            return true;
        }, ct);

    /// <summary>
    ///     temp → target. 있던 파일은 먼저 옆 이름으로 비켜 두고, 바꾼 뒤에 지운다 — 바꾸기가 실패하면 되돌린다.
    ///     중간에 취소하지 않는다(원본만 지워지고 새 파일은 남지 않는 일이 없게).
    /// </summary>
    private async Task ReplaceAsync(string temp, string target, string directory)
    {
        var none = CancellationToken.None;
        if (!await _client.FileExists(target, none).ConfigureAwait(false))
        {
            await _client.Rename(temp, target, none).ConfigureAwait(false);
            return;
        }

        var aside = Combine(directory, ".pvc-old." + Guid.NewGuid().ToString("N"));
        await _client.Rename(target, aside, none).ConfigureAwait(false);
        try
        {
            await _client.Rename(temp, target, none).ConfigureAwait(false);
        }
        catch
        {
            await _client.Rename(aside, target, none).ConfigureAwait(false); // 원본을 제자리로
            throw;
        }

        await DeleteQuietlyAsync(aside).ConfigureAwait(false);
    }

    private async Task DeleteQuietlyAsync(string path)
    {
        try
        {
            await _client.DeleteFile(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FtpException or IOException or SocketException)
        {
            // 남은 .pvc-up.* 는 다음에 지울 수 있다
        }
    }

    public Task CreateDirectoryAsync(string directory, string name, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var path = Combine(directory, GuestPaths.CheckName(name));
            if (await ExistsAsync(path, ct).ConfigureAwait(false))
                throw new GuestFileException(Res.T("Remote_Exists", name));
            return await _client.CreateDirectory(path, false, ct).ConfigureAwait(false);
        }, ct);

    public Task RenameAsync(string directory, string name, string newName, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var to = Combine(directory, GuestPaths.CheckName(newName));
            if (await ExistsAsync(to, ct).ConfigureAwait(false))
                throw new GuestFileException(Res.T("Remote_Exists", newName));
            await _client.Rename(Combine(directory, name), to, ct).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>지우기 — 폴더는 안까지, 링크는 링크만.</summary>
    public Task DeleteAsync(string directory, GuestFileEntry entry, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var path = Combine(directory, entry.Name);
            if (entry.IsDirectory) await _client.DeleteDirectory(path, ct).ConfigureAwait(false);
            else await _client.DeleteFile(path, ct).ConfigureAwait(false);
            return true;
        }, ct);

    private async Task<bool> ExistsAsync(string path, CancellationToken ct) =>
        await _client.FileExists(path, ct).ConfigureAwait(false)
        || await _client.DirectoryExists(path, ct).ConfigureAwait(false);

    public string ResolveLink(string directory, GuestFileEntry link) => GuestPaths.ResolveLink(directory, link);
    public string Parent(string directory) => GuestPaths.Parent(directory);
    public string Combine(string directory, string name) => GuestPaths.Combine(directory, name);

    private async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not GuestFileException && ex is not OperationCanceledException)
        {
            throw Translate(ex, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>FluentFTP·소켓 오류 → 게스트 오류(로그인 실패는 다시 물을 수 있게 따로).</summary>
    private static Exception Translate(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException or GuestFileException => ex,
        _ when ct.IsCancellationRequested => new OperationCanceledException(ct),
        FtpAuthenticationException auth => new FtpLoginException(Res.T("Ftp_LoginFailed", auth.Message)),
        AuthenticationException => new GuestFileException(Res.T("Ftp_CertificateRejected")),
        FtpException or IOException or SocketException or TimeoutException => new GuestFileException(ex.Message),
        _ => ex
    };

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }

    /// <summary>FluentFTP 진행률 → 보낸·받은 바이트(앞서 받은 만큼 더해서).</summary>
    private sealed class FtpProgressMap(IProgress<long> target, long offset) : IProgress<FtpProgress>
    {
        public void Report(FtpProgress value) => target.Report(offset + value.TransferredBytes);
    }
}
