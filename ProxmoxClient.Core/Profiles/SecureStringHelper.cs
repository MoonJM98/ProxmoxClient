using System.Runtime.InteropServices;
using System.Security;

namespace ProxmoxClient.Core.Profiles;

/// <summary>SecureString 유틸리티 — 변환 후 즉시 메모리에서 제거.</summary>
public static class SecureStringHelper
{
    /// <summary>SecureString을 임시 평문 문자열로 변환 (HTTP 전송용 — 전송 후 GC에 맡김).</summary>
    public static string? ToPlainString(SecureString? secureString)
    {
        if (secureString is null || secureString.Length == 0) return null;

        var ptr = Marshal.SecureStringToBSTR(secureString);
        try
        {
            return Marshal.PtrToStringBSTR(ptr);
        }
        finally
        {
            Marshal.ZeroFreeBSTR(ptr);
        }
    }

    /// <summary>문자열에서 SecureString 생성 (입력 문자열은 호출자가 정리).</summary>
    public static SecureString? FromString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        var ss = new SecureString();
        foreach (var ch in value) ss.AppendChar(ch);

        ss.MakeReadOnly();
        return ss;
    }
}