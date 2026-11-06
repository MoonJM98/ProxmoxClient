using System.Text;

namespace ProxmoxClient.Core.Api;

/// <summary>
///     CTAP2 에 필요한 만큼의 CBOR(RFC 8949) — 정수·바이트열·문자열·배열·맵·참/거짓·null.
///     읽은 값: long, byte[], string, List&lt;object?&gt;, Dictionary&lt;object, object?&gt;, bool, null.
/// </summary>
public static class Cbor
{
    public sealed class Writer
    {
        private readonly MemoryStream _out = new();

        public byte[] ToArray()
        {
            return _out.ToArray();
        }

        public Writer Int(long value)
        {
            if (value >= 0) Head(0, (ulong)value);
            else Head(1, (ulong)(-1 - value));
            return this;
        }

        public Writer Bytes(ReadOnlySpan<byte> value)
        {
            Head(2, (ulong)value.Length);
            _out.Write(value);
            return this;
        }

        public Writer Text(string value)
        {
            var b = Encoding.UTF8.GetBytes(value);
            Head(3, (ulong)b.Length);
            _out.Write(b);
            return this;
        }

        public Writer Array(int count)
        {
            Head(4, (ulong)count);
            return this;
        }

        public Writer Map(int count)
        {
            Head(5, (ulong)count);
            return this;
        }

        public Writer Bool(bool value)
        {
            _out.WriteByte(value ? (byte)0xF5 : (byte)0xF4);
            return this;
        }

        private void Head(int major, ulong value)
        {
            var m = (byte)(major << 5);
            if (value < 24)
            {
                _out.WriteByte((byte)(m | (byte)value));
            }
            else if (value <= byte.MaxValue)
            {
                _out.WriteByte((byte)(m | 24));
                _out.WriteByte((byte)value);
            }
            else if (value <= ushort.MaxValue)
            {
                _out.WriteByte((byte)(m | 25));
                _out.WriteByte((byte)(value >> 8));
                _out.WriteByte((byte)value);
            }
            else if (value <= uint.MaxValue)
            {
                _out.WriteByte((byte)(m | 26));
                for (var i = 3; i >= 0; i--) _out.WriteByte((byte)(value >> (8 * i)));
            }
            else
            {
                _out.WriteByte((byte)(m | 27));
                for (var i = 7; i >= 0; i--) _out.WriteByte((byte)(value >> (8 * i)));
            }
        }
    }

    /// <summary>값 하나를 읽는다. 형식이 틀리면 <see cref="FormatException" />.</summary>
    public static object? Decode(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var value = Read(data, ref pos, 0);
        return value;
    }

    private static object? Read(ReadOnlySpan<byte> d, ref int pos, int depth)
    {
        if (depth > 16 || pos >= d.Length) throw new FormatException("CBOR");

        var first = d[pos++];
        var major = first >> 5;
        var info = first & 0x1F;
        if (major == 7)
            return info switch
            {
                20 => false,
                21 => true,
                22 or 23 => null,
                _ => throw new FormatException("CBOR")
            };

        var arg = ReadArg(d, ref pos, info);
        switch (major)
        {
            case 0:
                return (long)arg;
            case 1:
                return -1 - (long)arg;
            case 2:
            case 3:
                if (arg > (ulong)(d.Length - pos)) throw new FormatException("CBOR");
                var slice = d.Slice(pos, (int)arg);
                pos += (int)arg;
                return major == 2 ? slice.ToArray() : Encoding.UTF8.GetString(slice);
            case 4:
            {
                if (arg > (ulong)(d.Length - pos)) throw new FormatException("CBOR");
                var list = new List<object?>((int)arg);
                for (ulong i = 0; i < arg; i++) list.Add(Read(d, ref pos, depth + 1));
                return list;
            }
            case 5:
            {
                if (arg > (ulong)(d.Length - pos)) throw new FormatException("CBOR");
                var map = new Dictionary<object, object?>();
                for (ulong i = 0; i < arg; i++)
                {
                    var key = Read(d, ref pos, depth + 1) ?? throw new FormatException("CBOR");
                    map[key] = Read(d, ref pos, depth + 1);
                }

                return map;
            }
            case 6:
                return Read(d, ref pos, depth + 1); // 태그는 무시
            default:
                throw new FormatException("CBOR");
        }
    }

    private static ulong ReadArg(ReadOnlySpan<byte> d, ref int pos, int info)
    {
        if (info < 24) return (ulong)info;

        var n = info switch { 24 => 1, 25 => 2, 26 => 4, 27 => 8, _ => throw new FormatException("CBOR") };
        if (pos + n > d.Length) throw new FormatException("CBOR");
        ulong v = 0;
        for (var i = 0; i < n; i++) v = (v << 8) | d[pos++];
        return v;
    }
}
