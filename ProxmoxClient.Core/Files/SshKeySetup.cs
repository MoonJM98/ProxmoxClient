using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using ProxmoxClient.Core.Localization;
using Renci.SshNet;

namespace ProxmoxClient.Core.Files;

public enum SshKeyType { Ed25519, Rsa3072 }

public sealed record SshGeneratedKey(string PrivateKeyPath, string PublicKeyPath, string PublicKey);

/// <summary>Creates local key pairs and exports public keys without launching a process with a secret.</summary>
public static class SshKeySetup
{
    private static readonly string[] DefaultNames = ["id_ed25519", "id_ecdsa", "id_rsa"];
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string DefaultPrivateKeyPath(SshKeyType type = SshKeyType.Rsa3072) =>
        Path.Combine(SftpAuthentication.DefaultKeyDirectory, type switch
        {
            SshKeyType.Ed25519 => "id_ed25519",
            SshKeyType.Rsa3072 => "id_rsa",
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        });

    public static IReadOnlyList<string> DiscoverDefaultKeys(string? directory = null) =>
        DefaultNames.Select(name => Path.Combine(directory ?? SftpAuthentication.DefaultKeyDirectory, name))
            .Where(File.Exists).ToArray();

    public static Task<SshGeneratedKey> GenerateAsync(string privateKeyPath, SshKeyType type, string? passphrase,
        string? comment, CancellationToken ct = default) =>
        Task.Run(() => Generate(privateKeyPath, type, passphrase, comment, ct), ct);

    public static string ReadPublicKey(string privateKeyPath, string? passphrase, string? comment = null)
    {
        ValidateComment(comment);
        using var authentication = SftpAuthentication.Create(new SftpAccountProfile
        {
            UserName = "public-key-export", AuthenticationMode = SftpAuthenticationMode.PrivateKey,
            PrivateKeyPath = privateKeyPath
        }, passphrase);
        var method = (PrivateKeyAuthenticationMethod)authentication.Methods.Single();
        // The first RSA algorithm is ssh-rsa; SHA-2 names affect authentication signatures, not this public file.
        var algorithm = method.KeyFiles.Single().HostKeyAlgorithms.First();
        return FormatPublicKey(algorithm.Name, algorithm.Data, comment);
    }

    private static SshGeneratedKey Generate(string privateKeyPath, SshKeyType type, string? passphrase,
        string? comment, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(privateKeyPath)) throw new GuestFileException(Res.T("SshKey_PathRequired"));
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        ValidateComment(comment);
        if (type == SshKeyType.Ed25519 && !string.IsNullOrEmpty(passphrase))
            throw new GuestFileException(Res.T("SshKey_Ed25519Passphrase"));
        var path = Path.GetFullPath(privateKeyPath.Trim());
        var publicPath = path + ".pub";
        if (File.Exists(path) || Directory.Exists(path) || File.Exists(publicPath) || Directory.Exists(publicPath))
            throw new GuestFileException(Res.T("SshKey_AlreadyExists", path, publicPath));

        byte[]? privatePem = null;
        var privateCreated = false;
        var publicCreated = false;
        try
        {
            var material = type == SshKeyType.Rsa3072 ? GenerateRsa(passphrase, comment) : GenerateEd25519(comment);
            privatePem = material.PrivatePem;
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var privateFile = CreateFile(path, true))
            {
                privateCreated = true;
                // The private file is created with restricted access before any key bytes are written.
                using var publicFile = CreateFile(publicPath, false);
                publicCreated = true;
                ct.ThrowIfCancellationRequested();
                privateFile.Write(privatePem);
                ct.ThrowIfCancellationRequested();
                publicFile.Write(Utf8.GetBytes(material.PublicKey + "\n"));
                privateFile.Flush(flushToDisk: true);
                publicFile.Flush(flushToDisk: true);
                ct.ThrowIfCancellationRequested();
            }
            return new SshGeneratedKey(path, publicPath, material.PublicKey);
        }
        catch
        {
            // Only paths this operation created can be removed; existing files are never overwritten or deleted.
            var remaining = new List<string>();
            if (publicCreated) DeleteCreatedFile(publicPath, remaining);
            if (privateCreated) DeleteCreatedFile(path, remaining);
            if (remaining.Count > 0) throw new GuestFileException(Res.T("SshKey_CleanupFailed", string.Join("\n", remaining)));
            throw;
        }
        finally
        {
            if (privatePem is not null) CryptographicOperations.ZeroMemory(privatePem);
        }
    }

    private static void DeleteCreatedFile(string path, List<string> remaining)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { remaining.Add(path); }
    }

    private static FileStream CreateFile(string path, bool privateKey)
    {
        if (privateKey && OperatingSystem.IsWindows()) return CreateRestrictedWindowsFile(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = privateKey ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        return new FileStream(path, options);
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateRestrictedWindowsFile(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException(Res.T("SshKey_CurrentUserRequired"));
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
            FileShare.None, 4096, FileOptions.None, security);
    }

    private static (byte[] PrivatePem, string PublicKey) GenerateRsa(string? passphrase, string? comment)
    {
        using var rsa = RSA.Create(3072);
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        using var publicBlob = new MemoryStream();
        WriteString(publicBlob, "ssh-rsa");
        WriteMpint(publicBlob, parameters.Exponent!);
        WriteMpint(publicBlob, parameters.Modulus!);
        var der = string.IsNullOrEmpty(passphrase) ? rsa.ExportPkcs8PrivateKey()
            : rsa.ExportEncryptedPkcs8PrivateKey(passphrase,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 210000));
        try
        {
            return (Pem(string.IsNullOrEmpty(passphrase) ? "PRIVATE KEY" : "ENCRYPTED PRIVATE KEY", der),
                FormatPublicKey("ssh-rsa", publicBlob.ToArray(), comment));
        }
        finally { CryptographicOperations.ZeroMemory(der); }
    }

    private static (byte[] PrivatePem, string PublicKey) GenerateEd25519(string? comment)
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var publicKey = new byte[32];
        var combined = new byte[64];
        using var publicBlob = new MemoryStream();
        using var privateBlob = new MemoryStream();
        using var keyBlob = new MemoryStream();
        try
        {
            Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.GeneratePublicKey(seed, 0, publicKey, 0);
            seed.CopyTo(combined, 0);
            publicKey.CopyTo(combined, 32);
            WriteString(publicBlob, "ssh-ed25519");
            WriteString(publicBlob, publicKey);
            var check = RandomNumberGenerator.GetBytes(4);
            privateBlob.Write(check);
            privateBlob.Write(check);
            WriteString(privateBlob, "ssh-ed25519");
            WriteString(privateBlob, publicKey);
            WriteString(privateBlob, combined);
            WriteString(privateBlob, comment?.Trim() ?? string.Empty);
            for (byte padding = 1; privateBlob.Length % 8 != 0; padding++) privateBlob.WriteByte(padding);
            keyBlob.Write("openssh-key-v1\0"u8);
            WriteString(keyBlob, "none");
            WriteString(keyBlob, "none");
            WriteString(keyBlob, ReadOnlySpan<byte>.Empty);
            WriteUInt32(keyBlob, 1);
            WriteString(keyBlob, publicBlob.GetBuffer().AsSpan(0, (int)publicBlob.Length));
            WriteString(keyBlob, privateBlob.GetBuffer().AsSpan(0, (int)privateBlob.Length));
            return (Pem("OPENSSH PRIVATE KEY", keyBlob.GetBuffer().AsSpan(0, (int)keyBlob.Length)),
                FormatPublicKey("ssh-ed25519", publicBlob.ToArray(), comment));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(combined);
            CryptographicOperations.ZeroMemory(privateBlob.GetBuffer());
            CryptographicOperations.ZeroMemory(keyBlob.GetBuffer());
        }
    }

    private static byte[] Pem(string label, ReadOnlySpan<byte> bytes)
    {
        var chars = PemEncoding.Write(label, bytes);
        try
        {
            // OpenSSH's private-key armour parser requires a final newline after the END marker.
            var pem = new byte[Utf8.GetByteCount(chars) + 1];
            Utf8.GetBytes(chars.AsSpan(), pem.AsSpan());
            pem[^1] = (byte)'\n';
            return pem;
        }
        finally { Array.Clear(chars); }
    }

    private static string FormatPublicKey(string type, byte[] blob, string? comment) =>
        $"{type} {Convert.ToBase64String(blob)}" + (string.IsNullOrWhiteSpace(comment) ? string.Empty : " " + comment.Trim());

    private static void ValidateComment(string? comment)
    {
        if (comment?.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029') == true)
            throw new GuestFileException(Res.T("SshKey_CommentSingleLine"));
    }

    private static void WriteString(Stream destination, string value) => WriteString(destination, Utf8.GetBytes(value));
    private static void WriteString(Stream destination, ReadOnlySpan<byte> value)
    {
        WriteUInt32(destination, (uint)value.Length);
        destination.Write(value);
    }
    private static void WriteUInt32(Stream destination, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        destination.Write(bytes);
    }
    private static void WriteMpint(Stream destination, ReadOnlySpan<byte> value)
    {
        while (value.Length > 0 && value[0] == 0) value = value[1..];
        var prefix = value.Length > 0 && (value[0] & 0x80) != 0;
        WriteUInt32(destination, (uint)value.Length + (prefix ? 1u : 0u));
        if (prefix) destination.WriteByte(0);
        destination.Write(value);
    }
}
