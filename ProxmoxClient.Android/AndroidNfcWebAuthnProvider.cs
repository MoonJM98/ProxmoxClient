using Android.Content;
using Android.Nfc;
using Android.Nfc.Tech;
using Android.OS;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;

namespace ProxmoxClient.Droid;

/// <summary>
///     Android 보안 키(WebAuthn) — NFC 보안 키에 직접 서명을 받는다(CTAP2, 안 되면 U2F; Core 의 <see cref="FidoClient" />).
///     WebView·Credential Manager 를 쓰지 않는 까닭: 일반 앱이 다른 사이트(PVE 주소)의 origin 으로 WebAuthn 을 부르려면
///     그 사이트의 /.well-known/assetlinks.json 에 이 앱이 등록돼 있거나(WEB_AUTHENTICATION_SUPPORT_FOR_APP),
///     구글이 허가한 브라우저(권한 있는 앱)여야 한다 — 사용자마다 다른 PVE 서버에서는 둘 다 불가능하다.
///     보안 키와 직접 통신하면 clientDataJSON 의 origin 을 PVE 주소로 직접 만들 수 있다.
///     키를 기다리는 동안만 지금 액티비티에 NFC 읽기 모드를 켠다.
/// </summary>
internal sealed class AndroidNfcWebAuthnProvider(Context context) : IWebAuthnProvider
{
    public bool IsAvailable => NfcAdapter.GetDefaultAdapter(context) is not null;
    public string UnavailableReason => L.T("Tfa_WebAuthnNoNfc");
    public string PromptText => L.T("Tfa_WebAuthnPromptNfc");

    public async Task<string?> GetAssertionAsync(Uri serverOrigin, string publicKeyJson, CancellationToken ct)
    {
        var adapter = NfcAdapter.GetDefaultAdapter(context) ?? throw new WebAuthnException(UnavailableReason);
        if (!adapter.IsEnabled) throw new WebAuthnException(L.T("Tfa_WebAuthnNfcOff"));

        var activity = MainActivity.Current ?? throw new WebAuthnException(UnavailableReason);
        var request = WebAuthnRequest.Parse(publicKeyJson);
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.TimeoutMs);
        var token = timeout.Token;
        // 읽기 모드 콜백(바인더 스레드)에서 통신을 끝까지 한다 — 콜백이 끝나기 전에 태그를 쓴다
        var callback = new ReaderCallback(tag =>
            HandleTagAsync(tag, request, serverOrigin, result, token).GetAwaiter().GetResult());

        var extras = new Bundle();
        extras.PutInt(NfcAdapter.ExtraReaderPresenceCheckDelay, 1000);
        adapter.EnableReaderMode(activity, callback,
            NfcReaderFlags.NfcA | NfcReaderFlags.NfcB | NfcReaderFlags.SkipNdefCheck, extras);
        try
        {
            await using var reg = token.Register(() => result.TrySetCanceled(token));
            return await result.Task;
        }
        catch (System.OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw WebAuthnException.Timeout();
        }
        finally
        {
            adapter.DisableReaderMode(activity);
        }
    }

    /// <summary>키가 닿았다 — FIDO 애플릿을 고르고 서명받는다. 키를 너무 빨리 떼면(통신 끊김) 다시 대기를 기다린다.</summary>
    private static async Task HandleTagAsync(Tag tag, WebAuthnRequest request, Uri origin,
        TaskCompletionSource<string> result, CancellationToken ct)
    {
        if (result.Task.IsCompleted) return;

        var iso = IsoDep.Get(tag);
        if (iso is null)
        {
            result.TrySetException(WebAuthnException.NotFidoKey());
            return;
        }

        try
        {
            using (iso)
            {
                iso.Connect();
                iso.Timeout = 5000; // 지문 확인 등으로 느린 응답(9100 은 따로 기다린다)
                var transport = new NfcCtapTransport((apdu, _) => Task.Run(() => iso.Transceive(apdu)!));
                await transport.SelectAsync(ct);
                var assertion = await FidoClient.GetAssertionAsync(transport, request, origin, ct);
                result.TrySetResult(assertion.ToPveResponse(request.Challenge));
            }
        }
        catch (Exception ex) when (ex is Java.IO.IOException or TagLostException)
        {
            // 키를 떼었다 — 다시 대면 처음부터
        }
        catch (Exception ex)
        {
            result.TrySetException(ex);
        }
    }

    private sealed class ReaderCallback(Action<Tag> onTag) : Java.Lang.Object, NfcAdapter.IReaderCallback
    {
        public void OnTagDiscovered(Tag? tag)
        {
            if (tag is not null) onTag(tag);
        }
    }
}
