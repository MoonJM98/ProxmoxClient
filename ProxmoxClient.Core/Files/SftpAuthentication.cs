using System.Formats.Asn1;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Pkcs;
using ProxmoxClient.Core.Localization;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;
using Renci.SshNet.Security.Cryptography;

namespace ProxmoxClient.Core.Files;

public sealed class SftpPassphraseRequiredException(string keyPath)
    : GuestFileException(string.Format(Res.T("Sftp_PassphraseRequired"), keyPath))
{
    public string KeyPath { get; } = keyPath;
}

public sealed class SftpPassphraseException(string keyPath)
    : GuestFileException(string.Format(Res.T("Sftp_PassphraseInvalid"), keyPath))
{
    public string KeyPath { get; } = keyPath;
}

/// <summary>Owns the authentication methods and private keys for one SFTP connection.</summary>
public sealed class SftpAuthentication : IDisposable
{
    private static readonly string[] DefaultKeyNames = ["id_ed25519", "id_ecdsa", "id_rsa"];
    private readonly List<PrivateKeyFile> _keys = [];
    private bool _disposed;

    private SftpAuthentication() { }
    public AuthenticationMethod[] Methods { get; private set; } = [];
    public bool UsedPassphrase { get; private set; }
    public bool HasSkippedEncryptedKeys => SkippedKeyError is not null;
    internal GuestFileException? SkippedKeyError { get; private set; }

    public static string DefaultKeyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>Creates the selected method only; key modes never fall back to a password.</summary>
    public static SftpAuthentication Create(SftpAccountProfile account, string? secret,
        string? defaultKeyDirectory = null)
    {
        account = account.Normalize();
        var authentication = new SftpAuthentication();
        try
        {
            if (account.AuthenticationMode == SftpAuthenticationMode.Password)
            {
                authentication.Methods = [new PasswordAuthenticationMethod(account.UserName, secret ?? string.Empty)];
                return authentication;
            }

            IPrivateKeySource[] sources;
            if (account.AuthenticationMode == SftpAuthenticationMode.DefaultKeys)
                sources = authentication.LoadDefaultKeys(defaultKeyDirectory ?? DefaultKeyDirectory, secret);
            else
            {
                if (account.PrivateKeyPath.Length == 0)
                    throw new GuestFileException(Res.T("Sftp_KeyPathRequired"));
                if (account.AuthenticationMode == SftpAuthenticationMode.Certificate && account.CertificatePath.Length == 0)
                    throw new GuestFileException(Res.T("Sftp_CertificatePathRequired"));

                var key = authentication.LoadKey(account.PrivateKeyPath, secret);
                sources = account.AuthenticationMode == SftpAuthenticationMode.Certificate
                    ? [LoadCertificate(key, account.CertificatePath)] : [key];
            }
            authentication.Methods = [new PrivateKeyAuthenticationMethod(account.UserName, sources)];
            return authentication;
        }
        catch
        {
            authentication.Dispose();
            throw;
        }
    }

    private IPrivateKeySource[] LoadDefaultKeys(string directory, string? secret)
    {
        var keys = new List<IPrivateKeySource>();
        GuestFileException? firstError = null;
        SftpPassphraseRequiredException? lockedKey = null;
        SftpPassphraseException? invalidPassphrase = null;
        foreach (var name in DefaultKeyNames)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            try { keys.Add(LoadKey(path, secret)); }
            catch (SftpPassphraseRequiredException ex) { lockedKey ??= ex; }
            catch (SftpPassphraseException ex) { invalidPassphrase ??= ex; }
            catch (GuestFileException ex) { firstError ??= ex; }
        }
        if (keys.Count > 0)
        {
            SkippedKeyError = (GuestFileException?)invalidPassphrase ?? lockedKey;
            return keys.ToArray();
        }
        if (invalidPassphrase is not null) throw invalidPassphrase;
        if (lockedKey is not null) throw lockedKey;
        if (firstError is not null) throw firstError;
        throw new GuestFileException(string.Format(Res.T("Sftp_DefaultKeysMissing"), directory));
    }

    private PrivateKeyFile LoadKey(string path, string? passphrase)
    {
        try
        {
            PrivateKeyFile key;
            // SSH.NET's encrypted PKCS#8 parser does not use SshPassPhraseNullOrEmptyException.
            using (var reader = File.OpenText(path))
            {
                string? line;
                do { line = reader.ReadLine(); } while (line is not null && string.IsNullOrWhiteSpace(line));
                if (line?.Trim() == "-----BEGIN ENCRYPTED PRIVATE KEY-----")
                {
                    key = LoadEncryptedKey(path, passphrase);
                    _keys.Add(key);
                    return key;
                }
            }
            try { key = new PrivateKeyFile(path); }
            catch (SshPassPhraseNullOrEmptyException) { key = LoadEncryptedKey(path, passphrase); }
            _keys.Add(key);
            return key;
        }
        catch (GuestFileException) { throw; }
        catch (Exception ex) when (IsKeyFileError(ex))
        {
            throw new GuestFileException(string.Format(Res.T("Sftp_KeyLoadFailed"), path, ex.Message));
        }
    }

    private PrivateKeyFile LoadEncryptedKey(string path, string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new SftpPassphraseRequiredException(path);
        try
        {
            var key = new PrivateKeyFile(path, passphrase);
            UsedPassphrase = true;
            return key;
        }
        catch (Exception ex) when (IsKeyParseError(ex)) { throw new SftpPassphraseException(path); }
    }

    private static IPrivateKeySource LoadCertificate(PrivateKeyFile key, string path)
    {
        try
        {
            var fields = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !fields[0].EndsWith("-cert-v01@openssh.com", StringComparison.Ordinal))
                throw new FormatException("An OpenSSH user certificate is required.");
            var certificate = new Certificate(Convert.FromBase64String(fields[1]));
            if (certificate.Type != Certificate.CertificateType.User)
                throw new ArgumentException("The supplied certificate is a host certificate, not a user certificate.");
            if (!certificate.Key.Public.SequenceEqual(key.Key.Public))
                throw new ArgumentException("The supplied certificate does not certify the supplied private key.");
            // Only certificate algorithms are offered. A rejected certificate must not silently use the bare key.
            return new CertificateKeySource(key, certificate);
        }
        catch (Exception ex) when (IsKeyFileError(ex))
        {
            throw new GuestFileException(string.Format(Res.T("Sftp_CertificateInvalid"), ex.Message));
        }
    }

    private sealed class CertificateKeySource : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }
        public CertificateKeySource(PrivateKeyFile key, Certificate certificate)
        {
            HostKeyAlgorithms = key.Key is RsaKey rsa
                ? [new CertificateHostAlgorithm("rsa-sha2-512-cert-v01@openssh.com", rsa, certificate,
                       new RsaDigitalSignature(rsa, HashAlgorithmName.SHA512)),
                   new CertificateHostAlgorithm("rsa-sha2-256-cert-v01@openssh.com", rsa, certificate,
                       new RsaDigitalSignature(rsa, HashAlgorithmName.SHA256)),
                   new CertificateHostAlgorithm("ssh-rsa-cert-v01@openssh.com", rsa, certificate)]
                : [new CertificateHostAlgorithm(certificate.Name, key.Key, certificate)];
        }
    }

    private static bool IsKeyParseError(Exception exception) => exception is
        SshException or ArgumentException or FormatException or InvalidOperationException or NotSupportedException
        or CryptographicException or AsnContentException or CryptoException or PkcsException;
    private static bool IsKeyFileError(Exception exception) =>
        IsKeyParseError(exception) || exception is IOException or UnauthorizedAccessException;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var method in Methods) method.Dispose();
        foreach (var key in _keys) key.Dispose();
    }
}
