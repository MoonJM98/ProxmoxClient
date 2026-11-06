using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     Linux — USB 보안 키를 /dev/hidraw* 로 직접 쓴다(CTAPHID, libfido2 가 하는 일). FIDO 장치는 보고서 설명자의
///     사용 페이지 0xF1D0 으로 알아본다. 장치 파일을 열 권한은 보통 systemd(uaccess)·libfido2 의 udev 규칙이 준다 —
///     권한이 없으면 안내한다. 키를 꽂지 않았으면 제한 시간까지 기다린다.
/// </summary>
public sealed class LinuxHidWebAuthnProvider : IWebAuthnProvider
{
    private const string HidrawClass = "/sys/class/hidraw";

    public bool IsAvailable => Directory.Exists(HidrawClass);
    public string UnavailableReason => L.T("Tfa_WebAuthnNoLinux");
    public string PromptText => L.T("Tfa_WebAuthnPromptUsb");

    public async Task<string?> GetAssertionAsync(Uri serverOrigin, string publicKeyJson, CancellationToken ct)
    {
        var request = WebAuthnRequest.Parse(publicKeyJson);
        var deadline = DateTime.UtcNow.AddMilliseconds(request.TimeoutMs);
        string? device;
        while ((device = FindFidoDevice()) is null)
        {
            if (DateTime.UtcNow > deadline) throw new WebAuthnException(L.T("Tfa_WebAuthnNoKey"));

            await Task.Delay(500, ct); // 키를 꽂기를 기다린다
        }

        FileStream stream;
        try
        {
            stream = new FileStream(device, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new WebAuthnException(L.T("Tfa_WebAuthnHidPermission", device), ex);
        }

        await using (stream)
        {
            var transport = new HidCtapTransport(
                (frame, token) => Task.Run(() =>
                {
                    // hidraw 쓰기: 보고서 번호(0) + 64바이트
                    var report = new byte[HidCtapTransport.ReportSize + 1];
                    frame.CopyTo(report, 1);
                    stream.Write(report);
                }, token),
                token => Task.Run(() =>
                {
                    var buffer = new byte[HidCtapTransport.ReportSize];
                    var n = stream.Read(buffer);
                    return n > 0 ? buffer[..n] : throw new WebAuthnException(L.T("Tfa_WebAuthnNoKey"));
                }, CancellationToken.None));
            await transport.OpenAsync(ct);
            var assertion = await FidoClient.GetAssertionAsync(transport, request, serverOrigin, ct);
            return assertion.ToPveResponse(request.Challenge);
        }
    }

    /// <summary>FIDO 보안 키의 /dev/hidrawN — 보고서 설명자에 사용 페이지 0xF1D0(06 D0 F1)이 있는 장치.</summary>
    private static string? FindFidoDevice()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(HidrawClass).Order())
            {
                var descriptor = Path.Combine(dir, "device", "report_descriptor");
                if (!File.Exists(descriptor)) continue;

                var bytes = File.ReadAllBytes(descriptor);
                if (bytes.AsSpan().IndexOf([(byte)0x06, (byte)0xD0, (byte)0xF1]) >= 0)
                    return "/dev/" + Path.GetFileName(dir);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}
