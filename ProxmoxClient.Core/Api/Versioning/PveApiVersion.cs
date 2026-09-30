using System.Globalization;

namespace ProxmoxClient.Core.Api.Versioning;

/// <summary>
///     PVE 주.부 버전(예: 8.1) — API 가 들어온 버전과 서버 버전을 견준다. 패치 번호(8.1.4 의 4)는 API 변화와
///     맞지 않아 쓰지 않는다.
/// </summary>
public readonly record struct PveApiVersion(int Major, int Minor) : IComparable<PveApiVersion>
{
    /// <summary>이 앱이 지원하는 가장 낮은 서버 버전.</summary>
    public static readonly PveApiVersion Minimum = new(7, 0);

    public int CompareTo(PveApiVersion other)
    {
        return Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);
    }

    public static bool operator <(PveApiVersion a, PveApiVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(PveApiVersion a, PveApiVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(PveApiVersion a, PveApiVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(PveApiVersion a, PveApiVersion b) => a.CompareTo(b) >= 0;

    /// <summary>"8.2.4"·"9.0"·"8" 을 읽는다. 앞의 숫자 두 개만 쓴다.</summary>
    public static bool TryParse(string? text, out PveApiVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.', '-', '~');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return false;
        var minor = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor))
            return false;
        version = new PveApiVersion(major, minor);
        return true;
    }

    public static PveApiVersion Parse(string text)
    {
        return TryParse(text, out var v) ? v : throw new FormatException($"Invalid PVE version: '{text}'");
    }

    public override string ToString()
    {
        return $"{Major}.{Minor}";
    }
}
