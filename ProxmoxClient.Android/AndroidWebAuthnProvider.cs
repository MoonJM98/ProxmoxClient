using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Usb;
using Android.Nfc;
using Android.Nfc.Tech;
using Android.OS;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;

namespace ProxmoxClient.Droid;

/// <summary>
///     Android 보안 키(WebAuthn) — NFC 또는 USB 보안 키에 직접 서명을 받는다(CTAP2·PIN, 안 되면 U2F; Core 의 <see cref="FidoClient" />).
///     둘을 함께 기다리다 먼저 닿거나 꽂힌 키를 쓴다.
///     WebView·Credential Manager 를 쓰지 않는 까닭: 일반 앱이 다른 사이트(PVE 주소)의 origin 으로 WebAuthn 을 부르려면
///     그 사이트의 /.well-known/assetlinks.json 에 이 앱이 등록돼 있거나(WEB_AUTHENTICATION_SUPPORT_FOR_APP),
///     구글이 허가한 브라우저(권한 있는 앱)여야 한다 — 사용자마다 다른 PVE 서버에서는 둘 다 불가능하다.
///     보안 키와 직접 통신하면 clientDataJSON 의 origin 을 PVE 주소로 직접 만들 수 있다.
///     키를 기다리는 동안만 지금 액티비티에 NFC 읽기 모드를 켜고 USB 장치 목록을 살핀다.
/// </summary>
internal sealed class AndroidWebAuthnProvider(Context context) : IWebAuthnProvider
{
    private const string UsbPermissionAction = "io.github.moonjm98.proxmoxclient.USB_PERMISSION";

    private bool HasUsbHost => context.PackageManager?.HasSystemFeature(PackageManager.FeatureUsbHost) == true;

    public bool IsAvailable => NfcAdapter.GetDefaultAdapter(context) is not null || HasUsbHost;
    public string UnavailableReason => L.T("Tfa_WebAuthnNoNfc");

    public string PromptText => NfcAdapter.GetDefaultAdapter(context) is { IsEnabled: true }
        ? HasUsbHost ? L.T("Tfa_WebAuthnPromptAndroid") : L.T("Tfa_WebAuthnPromptNfc")
        : L.T("Tfa_WebAuthnPromptUsb");

    public async Task<string?> GetAssertionAsync(Uri serverOrigin, string publicKeyJson, PinPrompt? pin,
        CancellationToken ct)
    {
        var adapter = NfcAdapter.GetDefaultAdapter(context);
        var nfc = adapter is { IsEnabled: true };
        if (!nfc && !HasUsbHost)
            throw new WebAuthnException(adapter is null ? UnavailableReason : L.T("Tfa_WebAuthnNfcOff"));

        var activity = MainActivity.Current ?? throw new WebAuthnException(UnavailableReason);
        var request = WebAuthnRequest.Parse(publicKeyJson);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        session.CancelAfter(Math.Max(request.TimeoutMs, 30000) + 120000); // PIN 입력 시간을 더 준다
        var token = session.Token;
        var pinCache = new PinCache(pin);

        if (nfc)
        {
            // 읽기 모드 콜백(바인더 스레드)에서 통신을 끝까지 한다 — 콜백이 끝나기 전에 태그를 쓴다
            var callback = new ReaderCallback(tag =>
                HandleTagAsync(tag, request, serverOrigin, pinCache.Prompt, result, token).GetAwaiter().GetResult());
            var extras = new Bundle();
            extras.PutInt(NfcAdapter.ExtraReaderPresenceCheckDelay, 1000);
            adapter!.EnableReaderMode(activity, callback,
                NfcReaderFlags.NfcA | NfcReaderFlags.NfcB | NfcReaderFlags.SkipNdefCheck, extras);
        }

        if (HasUsbHost) _ = Task.Run(() => UsbLoopAsync(request, serverOrigin, pinCache.Prompt, result, token), token);

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
            await session.CancelAsync(); // 다른 쪽(USB 살피기) 멈춤
            if (nfc) adapter!.DisableReaderMode(activity);
        }
    }

    /// <summary>NFC 키가 닿았다 — FIDO 애플릿을 고르고 서명받는다. 키를 떼면(통신 끊김) 다시 대기를 기다린다.</summary>
    private static async Task HandleTagAsync(Tag tag, WebAuthnRequest request, Uri origin, PinPrompt? pin,
        TaskCompletionSource<string?> result, CancellationToken ct)
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
                result.TrySetResult(await FidoClient.GetPveResponseAsync(transport, request, origin, pin, ct));
            }
        }
        catch (Exception ex) when (ex is Java.IO.IOException or TagLostException)
        {
            // 키를 떼었다(PIN 을 넣으려고 뗀 경우 포함) — 다시 대면 처음부터, PIN 은 기억해 둔 것을 쓴다
        }
        catch (Exception ex)
        {
            result.TrySetException(ex);
        }
    }

    /// <summary>USB 보안 키가 꽂히기를 기다린다 — 찾으면 권한을 묻고 CTAPHID 로 서명받는다.</summary>
    private async Task UsbLoopAsync(WebAuthnRequest request, Uri origin, PinPrompt? pin,
        TaskCompletionSource<string?> result, CancellationToken ct)
    {
        var manager = (UsbManager?)context.GetSystemService(Context.UsbService);
        if (manager is null) return;

        var denied = new HashSet<string>();
        try
        {
            while (!ct.IsCancellationRequested && !result.Task.IsCompleted)
            {
                foreach (var device in manager.DeviceList?.Values ?? [])
                {
                    if (denied.Contains(device.DeviceName) || FindFidoInterface(device) is not { } iface) continue;

                    if (!manager.HasPermission(device) && !await RequestPermissionAsync(manager, device, ct))
                    {
                        denied.Add(device.DeviceName);
                        if (NfcAdapter.GetDefaultAdapter(context) is not { IsEnabled: true })
                            result.TrySetException(new WebAuthnException(L.T("Tfa_WebAuthnUsbDenied")));
                        continue;
                    }

                    using var hid = UsbHid.Open(manager, device, iface.Interface, iface.In, iface.Out);
                    if (hid is null) continue;

                    var transport = new HidCtapTransport(hid.WriteAsync, hid.ReadAsync);
                    await transport.OpenAsync(ct);
                    result.TrySetResult(await FidoClient.GetPveResponseAsync(transport, request, origin, pin, ct));
                    return;
                }

                await Task.Delay(500, ct);
            }
        }
        catch (System.OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            result.TrySetException(ex);
        }
    }

    /// <summary>
    ///     FIDO 처럼 보이는 HID 인터페이스 — 클래스 HID·하위 클래스 0·프로토콜 0(키보드·마우스 아님), 인터럽트 입출력 끝점.
    ///     보고서 설명자(사용 페이지 0xF1D0)는 권한을 받은 뒤 <see cref="UsbHid.Open" /> 에서 확인한다.
    /// </summary>
    private static (UsbInterface Interface, UsbEndpoint In, UsbEndpoint Out)? FindFidoInterface(UsbDevice device)
    {
        for (var i = 0; i < device.InterfaceCount; i++)
        {
            var iface = device.GetInterface(i);
            if (iface.InterfaceClass != UsbClass.Hid || iface.InterfaceSubclass != 0 || iface.InterfaceProtocol != 0)
                continue;

            UsbEndpoint? input = null, output = null;
            for (var e = 0; e < iface.EndpointCount; e++)
            {
                var ep = iface.GetEndpoint(e);
                if (ep?.Type != UsbAddressing.XferInterrupt) continue;

                if (ep.Direction == UsbAddressing.In) input = ep;
                else output = ep;
            }

            if (input is not null && output is not null) return (iface, input, output);
        }

        return null;
    }

    /// <summary>USB 장치 권한을 묻는다(시스템 대화상자) — 결과는 이 앱만 받는 방송으로 온다.</summary>
    private async Task<bool> RequestPermissionAsync(UsbManager manager, UsbDevice device, CancellationToken ct)
    {
        var granted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new PermissionReceiver(ok => granted.TrySetResult(ok));
        var filter = new IntentFilter(UsbPermissionAction);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else
            context.RegisterReceiver(receiver, filter);
        try
        {
            // 시스템이 결과(EXTRA_PERMISSION_GRANTED)를 채워 넣으므로 변경 가능, 대신 패키지를 지정한 명시적 인텐트
            var intent = new Intent(UsbPermissionAction).SetPackage(context.PackageName);
            var flags = OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0;
            manager.RequestPermission(device, PendingIntent.GetBroadcast(context, 0, intent, flags));
            await using var reg = ct.Register(() => granted.TrySetCanceled(ct));
            return await granted.Task;
        }
        finally
        {
            context.UnregisterReceiver(receiver);
        }
    }

    /// <summary>NFC 키는 PIN 을 넣으려고 떼었다 다시 대므로, 받은 PIN 을 기억했다가 다음 탭에 쓴다(틀렸으면 다시 묻는다).</summary>
    private sealed class PinCache(PinPrompt? inner)
    {
        private string? _pin;

        public PinPrompt? Prompt => inner is null ? null : AskAsync;

        private async Task<string?> AskAsync(PinPromptInfo info, CancellationToken ct)
        {
            if (_pin is not null && info.Error is null) return _pin;

            _pin = await inner!(info, ct);
            return _pin;
        }
    }

    private sealed class ReaderCallback(Action<Tag> onTag) : Java.Lang.Object, NfcAdapter.IReaderCallback
    {
        public void OnTagDiscovered(Tag? tag)
        {
            if (tag is not null) onTag(tag);
        }
    }

    private sealed class PermissionReceiver(Action<bool> onResult) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            onResult(intent?.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false) == true);
        }
    }

    /// <summary>USB HID 보안 키 — 인터럽트 끝점으로 64바이트 보고서를 주고받는다.</summary>
    private sealed class UsbHid : IHidReportDevice
    {
        private readonly UsbDeviceConnection _connection;
        private readonly UsbInterface _interface;
        private readonly UsbEndpoint _in;
        private readonly UsbEndpoint _out;

        private UsbHid(UsbDeviceConnection connection, UsbInterface iface, UsbEndpoint input, UsbEndpoint output)
        {
            _connection = connection;
            _interface = iface;
            _in = input;
            _out = output;
        }

        /// <summary>장치를 열고 보고서 설명자에 FIDO 사용 페이지(06 D0 F1)가 있는지 본다. 아니면 null.</summary>
        public static UsbHid? Open(UsbManager manager, UsbDevice device, UsbInterface iface, UsbEndpoint input,
            UsbEndpoint output)
        {
            var connection = manager.OpenDevice(device);
            if (connection is null) return null;

            if (!connection.ClaimInterface(iface, true))
            {
                connection.Close();
                return null;
            }

            // GET_DESCRIPTOR(HID 보고서 설명자 0x22) — 표준·인터페이스 대상·장치→호스트(0x81)
            var descriptor = new byte[512];
            var n = connection.ControlTransfer((UsbAddressing)0x81, 0x06, 0x2200, iface.Id, descriptor,
                descriptor.Length, 1000);
            if (n <= 0 || descriptor.AsSpan(0, n).IndexOf([(byte)0x06, (byte)0xD0, (byte)0xF1]) < 0)
            {
                connection.ReleaseInterface(iface);
                connection.Close();
                return null;
            }

            return new UsbHid(connection, iface, input, output);
        }

        public Task WriteAsync(byte[] report, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                if (_connection.BulkTransfer(_out, report, report.Length, 1000) < 0)
                    throw new WebAuthnException(L.T("Tfa_WebAuthnNoKey"));
            }, ct);
        }

        public Task<byte[]> ReadAsync(CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var buffer = new byte[HidCtapTransport.ReportSize];
                // 짧게 끊어 기다리며 취소를 본다(키가 뽑혀도 여기서 끝난다)
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = _connection.BulkTransfer(_in, buffer, buffer.Length, 250);
                    if (n > 0) return buffer[..n];
                }
            }, ct);
        }

        public void Dispose()
        {
            _connection.ReleaseInterface(_interface);
            _connection.Close();
        }
    }
}
