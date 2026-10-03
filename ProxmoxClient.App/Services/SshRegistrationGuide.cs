using System.Buffers.Binary;
using System.Text;

namespace ProxmoxClient.App.Services;

/// <summary>Builds a command the target Linux SSH user can inspect and run to register a public key.</summary>
internal static class SshRegistrationGuide
{
    private const int MaxPasteBytes = 3500;

    // Match the key after an optional quoted options field, preserving any existing access restrictions.
    private const string CompactMatch = """function p(l,i,c,q,e){sub(/^[[:space:]]+/,"",l);q=e=0;for(i=1;i<=length(l);i++){c=substr(l,i,1);if(e){e=0;continue}if(c=="\\"&&q){e=1;continue}if(c=="\""){q=!q;continue}if(!q&&c~/[[:space:]]/)break}r=substr(l,i);return substr(l,1,i-1)} /^[[:space:]]*#/{next}{x=p($0);if(x!=t)x=p(r);if(x==t&&p(r)==b)v=1}END{exit!v}""";

    /// <summary>A single physical line that fits Linux canonical terminal input; unused comments are omitted.</summary>
    internal static string CreatePasteCommand(string publicKey)
    {
        var (type, blob, _) = ParsePublicKey(publicKey);
        var command = "(set -e; umask 077; "
            + """d="$(cd "$HOME" && pwd -P)/.ssh"; f="$d/authorized_keys"; """
            + """[ ! -L "$d" ] && { [ ! -e "$d" ] || [ -d "$d" ]; } || { printf '%s\n' 'Unsafe ~/.ssh path.' >&2; exit 1; }; """
            + """mkdir -p "$d"; chmod 700 "$d"; """
            + """[ ! -L "$f" ] && { [ ! -e "$f" ] || [ -f "$f" ]; } || { printf '%s\n' 'Unsafe authorized_keys path.' >&2; exit 1; }; """
            + """touch "$f"; chmod 600 "$f"; """
            + "k=" + Quote(type + " " + blob) + "; "
            + """if awk -v t="${k%% *}" -v b="${k#* }" '""" + CompactMatch
            + """' "$f"; then s=already; else r=$?; [ "$r" -eq 1 ] || exit "$r"; """
            + """[ ! -s "$f" ] || printf '\n' >> "$f"; printf '%s\n' "$k" >> "$f"; s=added; fi; """
            + """printf '%s: %s\n' "$s" "$f")""";
        if (Encoding.UTF8.GetByteCount(command) > MaxPasteBytes)
            throw new InvalidOperationException("Sftp_RegistrationCommandLong");
        return command;
    }

    internal static string CreateAuthorizedKeysCommand(string publicKey)
    {
        var (type, blob, key) = ParsePublicKey(publicKey);
        return $$"""
            (
              set -e
              umask 077
              if [ -L "$HOME/.ssh" ] || { [ -e "$HOME/.ssh" ] && [ ! -d "$HOME/.ssh" ]; }; then
                printf '%s\n' 'Refusing a symlink or non-directory ~/.ssh.' >&2
                exit 1
              fi
              mkdir -p "$HOME/.ssh"
              chmod 700 "$HOME/.ssh"
              if [ -L "$HOME/.ssh/authorized_keys" ] || { [ -e "$HOME/.ssh/authorized_keys" ] && [ ! -f "$HOME/.ssh/authorized_keys" ]; }; then
                printf '%s\n' 'Refusing a symlink or non-regular ~/.ssh/authorized_keys.' >&2
                exit 1
              fi
              touch "$HOME/.ssh/authorized_keys"
              chmod 600 "$HOME/.ssh/authorized_keys"
              sftp_key={{Quote(key)}}
              sftp_key_type={{Quote(type)}}
              sftp_key_blob={{Quote(blob)}}
              if awk -v kt="$sftp_key_type" -v kb="$sftp_key_blob" '
                function field(line, i, c, quoted, escaped) {
                  sub(/^[[:space:]]+/, "", line)
                  quoted = escaped = 0
                  for (i = 1; i <= length(line); i++) {
                    c = substr(line, i, 1)
                    if (escaped) { escaped = 0; continue }
                    if (c == "\\" && quoted) { escaped = 1; continue }
                    if (c == "\"") { quoted = !quoted; continue }
                    if (!quoted && c ~ /[[:space:]]/) break
                  }
                  rest = substr(line, i)
                  return substr(line, 1, i - 1)
                }
                /^[[:space:]]*#/ { next }
                {
                  first = field($0)
                  if (first != kt) first = field(rest)
                  if (first == kt && field(rest) == kb) found = 1
                }
                END { exit !found }
              ' "$HOME/.ssh/authorized_keys"; then
                sftp_result=already
              else
                sftp_match_status=$?
                if [ "$sftp_match_status" -ne 1 ]; then exit "$sftp_match_status"; fi
                if [ -s "$HOME/.ssh/authorized_keys" ]; then printf '\n' >> "$HOME/.ssh/authorized_keys"; fi
                printf '%s\n' "$sftp_key" >> "$HOME/.ssh/authorized_keys"
                sftp_result=added
              fi
              printf '%s: %s\n' "$sftp_result" "$(cd "$HOME" && pwd -P)/.ssh/authorized_keys"
            )
            """;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static (string Type, string Blob, string Key) ParsePublicKey(string publicKey)
    {
        var line = (publicKey ?? string.Empty).Trim();
        if (line.Length is 0 or > 8192 || Encoding.UTF8.GetByteCount(line) > 8192
            || line.Any(c => (char.IsControl(c) && c != '\t') || c is '\u2028' or '\u2029')) throw InvalidKey();
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !SupportedType(parts[0])) throw InvalidKey();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { throw InvalidKey(); }
        var type = parts[0];
        var input = bytes.AsSpan();
        if (!ReadString(ref input).SequenceEqual(Encoding.ASCII.GetBytes(type))) throw InvalidKey();
        switch (type)
        {
            case "ssh-ed25519":
                if (ReadString(ref input).Length != 32) throw InvalidKey();
                break;
            case "ssh-rsa":
                var exponent = ReadString(ref input);
                var modulus = ReadString(ref input);
                if (!PositiveInteger(exponent) || !PositiveInteger(modulus)) throw InvalidKey();
                break;
            default:
                var curve = type["ecdsa-sha2-".Length..];
                if (!ReadString(ref input).SequenceEqual(Encoding.ASCII.GetBytes(curve))) throw InvalidKey();
                var point = ReadString(ref input);
                var length = curve switch { "nistp256" => 65, "nistp384" => 97, _ => 133 };
                if (point.Length != length || point[0] != 4) throw InvalidKey();
                break;
        }
        if (!input.IsEmpty) throw InvalidKey();
        var blob = Convert.ToBase64String(bytes);
        var key = type + " " + blob;
        if (parts.Length > 2) key += " " + string.Join(' ', parts.AsSpan(2).ToArray());
        return (type, blob, key);
    }

    private static bool SupportedType(string type) => type is "ssh-ed25519" or "ssh-rsa"
        or "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521";

    private static ReadOnlySpan<byte> ReadString(ref Span<byte> input)
    {
        if (input.Length < 4) throw InvalidKey();
        var length = BinaryPrimitives.ReadUInt32BigEndian(input);
        input = input[4..];
        if (length > input.Length) throw InvalidKey();
        var value = input[..(int)length];
        input = input[(int)length..];
        return value;
    }

    private static bool PositiveInteger(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || (value[0] & 0x80) != 0) return false;
        foreach (var octet in value) if (octet != 0) return true;
        return false;
    }

    private static InvalidOperationException InvalidKey() => new("Sftp_PublicKeyInvalid");
}
