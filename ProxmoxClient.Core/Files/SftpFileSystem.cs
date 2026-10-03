using System.Globalization;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace ProxmoxClient.Core.Files;

/// <summary>Direct SFTP access using POSIX paths. Operations are serialized per connection.</summary>
public sealed class SftpFileSystem : IGuestFileSystem
{
    private readonly ISftpClient _client;
    private readonly SftpAuthentication? _authentication;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SftpFileSystem(ISftpClient client) => _client = client;
    private SftpFileSystem(ISftpClient client, SftpAuthentication authentication)
    {
        _client = client;
        _authentication = authentication;
    }
    public bool UsedPassphrase => _authentication?.UsedPassphrase == true;
    public bool HasSkippedEncryptedKeys => _authentication?.HasSkippedEncryptedKeys == true;
    public char Separator => '/';
    public StringComparer NameComparer => StringComparer.Ordinal;
    public string HomePath => _client.WorkingDirectory;
    public bool CanDownloadDirectory => false;
    public void Dispose()
    {
        try { _client.Dispose(); }
        finally { _authentication?.Dispose(); }
    }

    public static async Task<SftpFileSystem> OpenAsync(string host, int port, string user, string password,
        Func<string, bool> trustHostKey, CancellationToken ct = default) =>
        await OpenAsync(host, port, new SftpAccountProfile { UserName = user }, password, trustHostKey, ct)
            .ConfigureAwait(false);

    public static async Task<SftpFileSystem> OpenAsync(string host, int port, SftpAccountProfile account, string? secret,
        Func<string, bool> trustHostKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var authentication = await Task.Run(() => SftpAuthentication.Create(account, secret), ct).ConfigureAwait(false);
        SftpClient? client = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            client = new SftpClient(new ConnectionInfo(host, port, account.UserName.Trim(), authentication.Methods))
            {
                OperationTimeout = TimeSpan.FromSeconds(30),
                KeepAliveInterval = TimeSpan.FromSeconds(15)
            };
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
            client.HostKeyReceived += (_, e) => e.CanTrust = trustHostKey(e.FingerPrintSHA256);
            await client.ConnectAsync(ct).ConfigureAwait(false);
            return new SftpFileSystem(client, authentication);
        }
        catch (Exception ex)
        {
            try { client?.Dispose(); }
            finally { authentication.Dispose(); }
            // Readable defaults can fail while an encrypted candidate was skipped. Ask for its phrase then retry.
            if (ex is SshAuthenticationException && authentication.SkippedKeyError is { } skippedKeyError)
                throw skippedKeyError;
            throw;
        }
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        catch (Exception ex) when (ex is SshException or SocketException)
        { throw new GuestFileException(ex.Message); }
        finally { _gate.Release(); }
    }
    private Task RunAsync(Func<Task> work, CancellationToken ct) => RunAsync(async () =>
    {
        await work().ConfigureAwait(false);
        return true;
    }, ct);

    public Task<IReadOnlyList<GuestFileEntry>> ListAsync(string directory, CancellationToken ct = default) =>
        ListAsync(directory, null, ct);

    public Task<IReadOnlyList<GuestFileEntry>> ListAsync(string directory,
        Action<IReadOnlyList<GuestFileEntry>>? partial, CancellationToken ct = default) => RunAsync<IReadOnlyList<GuestFileEntry>>(async () =>
    {
        var entries = new List<GuestFileEntry>();
        await foreach (var file in _client.ListDirectoryAsync(directory, ct).ConfigureAwait(false))
        {
            if (file.Name is "." or "..") continue;
            entries.Add(new GuestFileEntry(file.Name,
                file.IsSymbolicLink ? GuestFileKind.Link : file.IsDirectory ? GuestFileKind.Directory :
                file.IsRegularFile ? GuestFileKind.File : GuestFileKind.Other,
                file.Length, file.LastWriteTimeUtc, Permissions(file.Attributes),
                file.UserId.ToString(CultureInfo.InvariantCulture), file.GroupId.ToString(CultureInfo.InvariantCulture)));
            if (entries.Count % 100 == 0) partial?.Invoke(entries.ToArray());
        }
        partial?.Invoke(entries.ToArray());
        return entries;
    }, ct);

    private static string Permissions(SftpFileAttributes attributes)
    {
        static int Bits(bool read, bool write, bool execute) => (read ? 4 : 0) | (write ? 2 : 0) | (execute ? 1 : 0);
        return $"{Bits(attributes.OwnerCanRead, attributes.OwnerCanWrite, attributes.OwnerCanExecute)}" +
               $"{Bits(attributes.GroupCanRead, attributes.GroupCanWrite, attributes.GroupCanExecute)}" +
               $"{Bits(attributes.OthersCanRead, attributes.OthersCanWrite, attributes.OthersCanExecute)}";
    }

    public Task DownloadAsync(string directory, GuestFileEntry entry, Stream destination, IProgress<long>? progress,
        CancellationToken ct = default) => RunAsync(async () =>
    {
        if (entry.IsDirectory) throw new NotSupportedException("SFTP directory downloads are not supported.");
        using var source = await _client.OpenAsync(Combine(directory, GuestPaths.CheckName(entry.Name)),
            FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
        await CopyAsync(source, destination, progress, ct).ConfigureAwait(false);
    }, ct);

    // Writes to a temporary file next to the target and swaps it in only after the copy completes, so a cancelled or
    // failed upload never truncates the existing file (the agent and container paths work the same way).
    public Task UploadAsync(string directory, string name, Stream source, long length, IProgress<long>? progress,
        CancellationToken ct = default) => RunAsync(async () =>
    {
        var target = Combine(directory, GuestPaths.CheckName(name));
        var temp = Combine(directory, ".pvc-up." + Guid.NewGuid().ToString("N"));
        try
        {
            using (var destination = await _client.OpenAsync(temp, FileMode.CreateNew, FileAccess.Write, ct)
                       .ConfigureAwait(false))
                await CopyAsync(source, destination, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await Task.Run(() => Replace(temp, target), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            try { await _client.DeleteFileAsync(temp, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is SshException or SocketException or IOException or InvalidOperationException) { }
            throw;
        }
    }, ct);

    // Keeps the existing file's permission bits (overwriting in place used to), then renames over it. POSIX rename
    // replaces atomically; servers without that extension need the old file removed first.
    private void Replace(string temp, string target)
    {
        if (_client.Exists(target))
        {
            try
            {
                var attributes = _client.GetAttributes(target);
                if (attributes.IsRegularFile)
                    _client.ChangePermissions(temp, Convert.ToInt16(Permissions(attributes), 8));
            }
            catch (SshException) { }

            try
            {
                _client.RenameFile(temp, target, true);
                return;
            }
            catch (Exception ex) when (ex is SshException or NotSupportedException) { }

            _client.DeleteFile(target);
        }

        _client.RenameFile(temp, target);
    }

    private static async Task CopyAsync(Stream source, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            progress?.Report(total += count);
        }
        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    public Task CreateDirectoryAsync(string directory, string name, CancellationToken ct = default) =>
        RunAsync(() => _client.CreateDirectoryAsync(Combine(directory, GuestPaths.CheckName(name)), ct), ct);
    public Task RenameAsync(string directory, string name, string newName, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var destination = Combine(directory, GuestPaths.CheckName(newName));
            var file = await FindFileAsync(directory, GuestPaths.CheckName(name), ct).ConfigureAwait(false);
            // RenameFileAsync resolves symbolic links. MoveTo renames the directory entry itself.
            await Task.Run(() => file.MoveTo(destination), ct).ConfigureAwait(false);
        }, ct);
    public Task DeleteAsync(string directory, GuestFileEntry entry, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var file = await FindFileAsync(directory, GuestPaths.CheckName(entry.Name), ct).ConfigureAwait(false);
            await DeleteTreeAsync(file, ct).ConfigureAwait(false);
        }, ct);

    private async Task<ISftpFile> FindFileAsync(string directory, string name, CancellationToken ct)
    {
        // GetAsync canonicalizes the whole path, which can follow a symbolic link before lstat.
        await foreach (var file in _client.ListDirectoryAsync(directory, ct).ConfigureAwait(false))
            if (file.Name == name) return file;
        throw new SftpPathNotFoundException(Combine(directory, name));
    }

    private async Task DeleteTreeAsync(ISftpFile file, CancellationToken ct)
    {
        if (file.IsDirectory && !file.IsSymbolicLink)
        {
            await foreach (var child in _client.ListDirectoryAsync(file.FullName, ct).ConfigureAwait(false))
                if (child.Name is not ("." or ".."))
                    await DeleteTreeAsync(child, ct).ConfigureAwait(false);
        }
        // Listed SftpFile.DeleteAsync removes the literal path without canonicalizing links.
        await file.DeleteAsync(ct).ConfigureAwait(false);
    }

    // SFTP resolves the link on the server when navigating into it.
    public string ResolveLink(string directory, GuestFileEntry link) => Combine(directory, GuestPaths.CheckName(link.Name));
    public string Parent(string directory) => GuestPaths.Parent(directory);
    public string Combine(string directory, string name) => GuestPaths.Combine(directory, name);
}
