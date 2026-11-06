using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     Linux — USB 보안 키를 /dev/hidraw* 로 직접 쓴다(CTAPHID, libfido2 가 하는 일). FIDO 장치는 보고서 설명자의
///     사용 페이지 0xF1D0 으로 알아본다. 장치 파일을 열 권한은 보통 systemd(uaccess)·libfido2 의 udev 규칙이 준다 —
///     권한이 없으면 안내한다. 키를 꽂지 않았으면 제한 시간까지 기다린다. PIN 이 필요하면 2단계 인증 창이 묻는다.
/// </summary>
public sealed class LinuxHidWebAuthnProvider : IWebAuthnProvider
{
    private const string HidrawClass = "/sys/class/hidraw";

    public bool IsAvailable => Directory.Exists(HidrawClass);
    public string UnavailableReason => L.T("Tfa_WebAuthnNoLinux");
    public string PromptText => L.T("Tfa_WebAuthnPromptUsb");

    public Task<string?> GetAssertionAsync(Uri serverOrigin, string publicKeyJson, PinPrompt? pin, CancellationToken ct)
    {
        return HidSecurityKey.GetAssertionAsync(_ => Task.FromResult(TryOpen()), serverOrigin, publicKeyJson, pin, ct);
    }

    private static IHidReportDevice? TryOpen()
    {
        if (FindFidoDevice() is not { } path) return null;

        try
        {
            return new Hidraw(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new WebAuthnException(L.T("Tfa_WebAuthnHidPermission", path), ex);
        }
    }

    /// <summary>FIDO 보안 키의 /dev/hidrawN — 보고서 설명자에 사용 페이지 0xF1D0(06 D0 F1)이 있는 장치.</summary>
    private static string? FindFidoDevice()
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(HidrawClass).Order().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var dir in dirs)
            if (ReadDescriptor(Path.Combine(dir, "device", "report_descriptor")) is { } bytes &&
                bytes.AsSpan().IndexOf([(byte)0x06, (byte)0xD0, (byte)0xF1]) >= 0)
                return "/dev/" + Path.GetFileName(dir);

        return null;
    }

    /// <summary>
    ///     sysfs 의 report_descriptor 는 크기를 4096(HID_MAX_DESCRIPTOR_SIZE)으로 알리지만 실제로는 설명자 길이만큼만 읽힌다 —
    ///     File.ReadAllBytes 는 알린 크기보다 짧으면 EndOfStreamException 을 던지므로 끝까지 읽어 모은다.
    ///     못 읽는 장치는 건너뛴다(null).
    /// </summary>
    private static byte[]? ReadDescriptor(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     /dev/hidrawN — 쓰기는 보고서 번호(0) + 64바이트, 읽기는 64바이트. 읽기는 막히는 호출이라 스레드 풀에서 한다
    ///     (취소하면 CTAPHID CANCEL 에 키가 답해 풀린다). 키를 뽑으면 <see cref="WebAuthnException" />.
    /// </summary>
    private sealed class Hidraw(FileStream stream) : IHidReportDevice
    {
        public Task WriteAsync(byte[] report, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var buffer = new byte[HidCtapTransport.ReportSize + 1];
                report.CopyTo(buffer, 1);
                try
                {
                    stream.Write(buffer);
                }
                catch (IOException ex)
                {
                    throw Unplugged(ex);
                }
            }, ct);
        }

        public Task<byte[]> ReadAsync(CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var buffer = new byte[HidCtapTransport.ReportSize];
                int n;
                try
                {
                    n = stream.Read(buffer);
                }
                catch (IOException ex)
                {
                    throw Unplugged(ex);
                }

                return n > 0 ? buffer[..n] : throw Unplugged(null);
            }, CancellationToken.None);
        }

        /// <summary>키를 뽑으면 hidraw 읽기·쓰기가 EIO(장치 없음)로 끝난다 — 키를 못 찾은 것으로 알린다.</summary>
        private static WebAuthnException Unplugged(Exception? inner)
        {
            return new WebAuthnException(L.T("Tfa_WebAuthnNoKey"), inner);
        }

        public void Dispose()
        {
            stream.Dispose();
        }
    }
}
