using ProxmoxClient.Core.Api;

/// <summary>실제 PVE(tools/PveE2E)에서 찾은 응답 차이 — 가짜 서버와 문구가 달랐던 것.</summary>
public static class RealPveTests
{
    /// <summary>VM 설정의 digest 불일치는 qemu-server 문구("checksum mismatch")로 온다 — CT·방화벽과 다르다.</summary>
    public static void QemuDigestMismatchText(Checker c)
    {
        var qemu = new ProxmoxApiException(500,
            "{\"data\":null,\"message\":\"checksum mismatch (file change by other user?)\\n\"}");
        c.Is(qemu.IsDigestMismatch, "VM 설정 digest 불일치(checksum mismatch)를 알아본다");
        var lxc = new ProxmoxApiException(500,
            "{\"data\":null,\"message\":\"detected modified configuration - file changed by other user? Try again.\\n\"}");
        c.Is(lxc.IsDigestMismatch, "CT·방화벽 digest 불일치도 그대로");
    }
}
