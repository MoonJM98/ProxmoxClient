using System.Security.Cryptography;
using System.Text;
using ProxmoxClient.Core.Localization;

namespace ProxmoxClient.Core.Vnc;

/// <summary>
///     VNC Authentication(RFB 보안 유형 2)의 DES challenge-response.
///     비밀번호(Proxmox에서는 VNC 티켓) 첫 8바이트를 비트 반전한 키로
///     16바이트 챌린지를 DES-ECB로 암호화해 응답한다.
/// </summary>
public static class VncDesAuth
{
    /// <summary>챌린지(16바이트)에 대한 응답 생성.</summary>
    public static byte[] CreateResponse(byte[] challenge, string password)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (challenge.Length != 16) throw new ArgumentException(Res.T("VncDesAuth_01"), nameof(challenge));

        var keyBytes = Encoding.ASCII.GetBytes(password ?? string.Empty);
        var key = new byte[8];
        var n = Math.Min(8, keyBytes.Length);
        Array.Copy(keyBytes, key, n);
        for (var i = 0; i < 8; i++) key[i] = ReverseBits(key[i]);

        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        des.Key = key;
        using var encryptor = des.CreateEncryptor();
        return encryptor.TransformFinalBlock(challenge, 0, challenge.Length);
    }

    private static byte ReverseBits(byte value)
    {
        byte result = 0;
        for (var i = 0; i < 8; i++)
        {
            result <<= 1;
            if ((value & (1 << i)) != 0) result |= 1;
        }

        return result;
    }
}