using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Platform;

/// <summary>USB 보안 키 하나 — 64바이트 HID 보고서를 보내고 받는다(Linux hidraw · macOS IOKit · Android USB).</summary>
public interface IHidReportDevice : IDisposable
{
    /// <summary>출력 보고서 하나(64바이트, 보고서 번호 없이).</summary>
    Task WriteAsync(byte[] report, CancellationToken ct);

    /// <summary>입력 보고서 하나를 기다린다.</summary>
    Task<byte[]> ReadAsync(CancellationToken ct);
}

/// <summary>USB 보안 키 공용 흐름 — 키가 꽂히기를 기다리고, CTAPHID 채널을 열고, 서명(필요하면 PIN)을 받는다.</summary>
public static class HidSecurityKey
{
    /// <summary>
    ///     <paramref name="tryOpen" /> 이 장치를 열 때까지(키를 꽂을 때까지) 질문의 제한 시간만큼 기다린다.
    ///     열 권한이 없으면 <paramref name="tryOpen" /> 이 <see cref="WebAuthnException" /> 을 던진다.
    /// </summary>
    public static async Task<string?> GetAssertionAsync(Func<CancellationToken, Task<IHidReportDevice?>> tryOpen,
        Uri origin, string publicKeyJson, PinPrompt? pin, CancellationToken ct)
    {
        var request = WebAuthnRequest.Parse(publicKeyJson);
        var deadline = DateTime.UtcNow.AddMilliseconds(request.TimeoutMs);
        IHidReportDevice? device;
        while ((device = await tryOpen(ct)) is null)
        {
            if (DateTime.UtcNow > deadline) throw new WebAuthnException(L.T("Tfa_WebAuthnNoKey"));

            await Task.Delay(500, ct); // 키를 꽂기를 기다린다
        }

        using (device)
        {
            var transport = new HidCtapTransport(device.WriteAsync, device.ReadAsync);
            await transport.OpenAsync(ct);
            return await FidoClient.GetPveResponseAsync(transport, request, origin, pin, ct);
        }
    }
}
