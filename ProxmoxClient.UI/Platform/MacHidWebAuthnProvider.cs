using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     macOS — USB 보안 키를 IOKit(IOHIDManager)으로 직접 쓴다(CTAPHID). 기본 사용 페이지가 0xF1D0(FIDO)인 장치만 고른다.
///     입력 보고서는 장치를 전용 스레드의 실행 루프에 붙여 콜백으로 받는다.
///     권한: 샌드박스 앱이면 com.apple.security.device.usb 엔타이틀먼트가 필요하다(지금 배포판은 샌드박스가 아니라 필요 없다).
///     FIDO 장치는 키보드가 아니라 "입력 모니터링" 권한 창도 뜨지 않는다. PIN 이 필요하면 2단계 인증 창이 묻는다.
///     (이 저장소의 빌드 환경에서는 실행해 보지 못했다 — 컴파일만 확인.)
/// </summary>
public sealed class MacHidWebAuthnProvider : IWebAuthnProvider
{
    public bool IsAvailable => OperatingSystem.IsMacOS();
    public string UnavailableReason => L.T("Tfa_WebAuthnNoMac");
    public string PromptText => L.T("Tfa_WebAuthnPromptUsb");

    public Task<string?> GetAssertionAsync(Uri serverOrigin, string publicKeyJson, PinPrompt? pin, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new WebAuthnException(UnavailableReason);

        return HidSecurityKey.GetAssertionAsync(_ => Task.FromResult(OperatingSystem.IsMacOS() ? MacHid.TryOpen() : null), serverOrigin, publicKeyJson,
            pin, ct);
    }
}

[SupportedOSPlatform("macos")]
internal sealed class MacHid : IHidReportDevice
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string IoKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const int KCFNumberSInt32Type = 3;
    private const uint KCFStringEncodingUtf8 = 0x08000100;
    private const int KIOHIDReportTypeOutput = 1;

    private static readonly ReportCallback Callback = OnReport; // 네이티브가 들고 있으니 수집되지 않게
    private readonly Channel<byte[]> _reports = Channel.CreateUnbounded<byte[]>();
    private readonly nint _manager;
    private readonly nint _device;
    private readonly nint _buffer = Marshal.AllocHGlobal(HidCtapTransport.ReportSize);
    private readonly GCHandle _self;
    private readonly Thread _loopThread;
    private nint _runLoop;
    private volatile bool _closed;

    private MacHid(nint manager, nint device)
    {
        _manager = manager;
        _device = device;
        _self = GCHandle.Alloc(this);
        using var ready = new ManualResetEventSlim();
        _loopThread = new Thread(() => RunLoop(ready)) { IsBackground = true, Name = "FIDO HID" };
        _loopThread.Start();
        ready.Wait();
    }

    /// <summary>FIDO 장치를 찾아 연다. 없으면 null.</summary>
    public static IHidReportDevice? TryOpen()
    {
        var manager = IOHIDManagerCreate(0, 0);
        if (manager == 0) return null;

        var keepManager = false;
        try
        {
            var key = CFStringCreateWithCString(0, "PrimaryUsagePage", KCFStringEncodingUtf8);
            var page = 0xF1D0;
            var value = CFNumberCreate(0, KCFNumberSInt32Type, ref page);
            var dict = CFDictionaryCreateMutable(0, 1, CfExport("kCFTypeDictionaryKeyCallBacks"),
                CfExport("kCFTypeDictionaryValueCallBacks"));
            CFDictionarySetValue(dict, key, value);
            IOHIDManagerSetDeviceMatching(manager, dict);
            CFRelease(dict);
            CFRelease(value);
            CFRelease(key);
            if (IOHIDManagerOpen(manager, 0) != 0) return null;

            var set = IOHIDManagerCopyDevices(manager);
            if (set == 0) return null;

            try
            {
                var count = (int)CFSetGetCount(set);
                if (count == 0) return null;

                var devices = new nint[count];
                CFSetGetValues(set, devices);
                var device = devices[0];
                if (IOHIDDeviceOpen(device, 0) != 0) throw new WebAuthnException(L.T("Tfa_WebAuthnMacOpen"));

                CFRetain(device);
                keepManager = true;
                return new MacHid(manager, device);
            }
            finally
            {
                CFRelease(set);
            }
        }
        finally
        {
            if (!keepManager) CFRelease(manager);
        }
    }

    public Task WriteAsync(byte[] report, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var r = IOHIDDeviceSetReport(_device, KIOHIDReportTypeOutput, 0, report, report.Length);
            if (r != 0) throw new WebAuthnException(L.T("Tfa_WebAuthnNoKey"));
        }, ct);
    }

    public async Task<byte[]> ReadAsync(CancellationToken ct)
    {
        try
        {
            return await _reports.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            throw new WebAuthnException(L.T("Tfa_WebAuthnNoKey"));
        }
    }

    public void Dispose()
    {
        if (_closed) return;

        _closed = true;
        if (_runLoop != 0) CFRunLoopStop(_runLoop);
        _loopThread.Join(2000);
        IOHIDDeviceClose(_device, 0);
        CFRelease(_device);
        CFRelease(_manager);
        _self.Free();
        Marshal.FreeHGlobal(_buffer);
        _reports.Writer.TryComplete();
    }

    /// <summary>장치를 이 스레드의 실행 루프에 붙이고, 닫을 때까지 입력 보고서 콜백을 돌린다.</summary>
    private void RunLoop(ManualResetEventSlim ready)
    {
        _runLoop = CFRunLoopGetCurrent();
        var mode = Marshal.ReadIntPtr(CfExport("kCFRunLoopDefaultMode"));
        IOHIDDeviceRegisterInputReportCallback(_device, _buffer, HidCtapTransport.ReportSize,
            Marshal.GetFunctionPointerForDelegate(Callback), GCHandle.ToIntPtr(_self));
        IOHIDDeviceScheduleWithRunLoop(_device, _runLoop, mode);
        ready.Set();
        while (!_closed) CFRunLoopRunInMode(mode, 0.25, false);
        IOHIDDeviceUnscheduleFromRunLoop(_device, _runLoop, mode);
    }

    private static void OnReport(nint context, int result, nint sender, int type, uint reportId, nint report,
        nint length)
    {
        if (GCHandle.FromIntPtr(context).Target is not MacHid self || result != 0) return;

        var data = new byte[(int)length];
        Marshal.Copy(report, data, 0, data.Length);
        self._reports.Writer.TryWrite(data);
    }

    private static nint CfExport(string name)
    {
        return NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), name);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReportCallback(nint context, int result, nint sender, int type, uint reportId, nint report,
        nint length);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint alloc, string text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern nint CFNumberCreate(nint alloc, int type, ref int value);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryCreateMutable(nint alloc, nint capacity, nint keyCallbacks,
        nint valueCallbacks);

    [DllImport(CoreFoundation)]
    private static extern void CFDictionarySetValue(nint dict, nint key, nint value);

    [DllImport(CoreFoundation)]
    private static extern nint CFSetGetCount(nint set);

    [DllImport(CoreFoundation)]
    private static extern void CFSetGetValues(nint set, [Out] nint[] values);

    [DllImport(CoreFoundation)]
    private static extern nint CFRetain(nint obj);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint obj);

    [DllImport(CoreFoundation)]
    private static extern nint CFRunLoopGetCurrent();

    [DllImport(CoreFoundation)]
    private static extern int CFRunLoopRunInMode(nint mode, double seconds,
        [MarshalAs(UnmanagedType.U1)] bool returnAfterSourceHandled);

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopStop(nint runLoop);

    [DllImport(IoKit)]
    private static extern nint IOHIDManagerCreate(nint alloc, uint options);

    [DllImport(IoKit)]
    private static extern void IOHIDManagerSetDeviceMatching(nint manager, nint matching);

    [DllImport(IoKit)]
    private static extern int IOHIDManagerOpen(nint manager, uint options);

    [DllImport(IoKit)]
    private static extern nint IOHIDManagerCopyDevices(nint manager);

    [DllImport(IoKit)]
    private static extern int IOHIDDeviceOpen(nint device, uint options);

    [DllImport(IoKit)]
    private static extern int IOHIDDeviceClose(nint device, uint options);

    [DllImport(IoKit)]
    private static extern int IOHIDDeviceSetReport(nint device, int type, nint reportId, byte[] report, nint length);

    [DllImport(IoKit)]
    private static extern void IOHIDDeviceRegisterInputReportCallback(nint device, nint report, nint length,
        nint callback, nint context);

    [DllImport(IoKit)]
    private static extern void IOHIDDeviceScheduleWithRunLoop(nint device, nint runLoop, nint mode);

    [DllImport(IoKit)]
    private static extern void IOHIDDeviceUnscheduleFromRunLoop(nint device, nint runLoop, nint mode);
}
