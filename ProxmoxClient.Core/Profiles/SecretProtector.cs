using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Profiles;

/// <summary>
///     디스크에 저장하는 비밀값 암호화 — Windows DPAPI(CryptProtectData, 현재 사용자 범위).
///     같은 Windows 계정에서만 복호화할 수 있어 프로필 파일이 복사·유출돼도 평문이 드러나지 않는다.
///     외부 패키지 없이 crypt32 를 직접 호출한다.
/// </summary>
internal static class SecretProtector
{
    private const string Prefix = "dpapi:v1:";
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>이 앱 전용 부가 엔트로피 — 같은 계정의 다른 프로그램이 DPAPI 로 쉽게 풀지 못하게 한다.</summary>
    private static readonly byte[] Entropy = "ProxmoxClient.ProfileSecret.v1"u8.ToArray();

    public static string Protect(string plainText)
    {
        EnsureWindows();
        var plain = Encoding.UTF8.GetBytes(plainText);
        try
        {
            return Prefix + Convert.ToBase64String(Transform(plain, true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>복호화. 형식이 다르거나 다른 계정·PC 에서 암호화된 값이면 false.</summary>
    public static bool TryUnprotect(string protectedText, out string? plainText)
    {
        plainText = null;
        if (!OperatingSystem.IsWindows() || !protectedText.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        try
        {
            var plain = Transform(Convert.FromBase64String(protectedText[Prefix.Length..]), false);
            try
            {
                plainText = Encoding.UTF8.GetString(plain);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(Res.T("SecretProtector_01"));
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPtr = Marshal.AllocHGlobal(Math.Max(input.Length, 1));
        var entropyPtr = Marshal.AllocHGlobal(Entropy.Length);
        try
        {
            Marshal.Copy(input, 0, inputPtr, input.Length);
            Marshal.Copy(Entropy, 0, entropyPtr, Entropy.Length);
            var inputBlob = new DataBlob { cbData = input.Length, pbData = inputPtr };
            var entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = entropyPtr };

            var ok = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());

            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                Marshal.Copy(new byte[output.cbData], 0, output.pbData, output.cbData); // 네이티브 사본 지우기
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.Copy(new byte[Math.Max(input.Length, 1)], 0, inputPtr, Math.Max(input.Length, 1)); // 평문 입력 지우기
            Marshal.FreeHGlobal(inputPtr);
            Marshal.FreeHGlobal(entropyPtr);
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }
}