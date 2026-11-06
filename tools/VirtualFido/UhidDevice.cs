using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProxmoxClient.Tools.Fido;

/// <summary>
///     Linux /dev/uhid — 사용자 공간 HID 장치(커널 Documentation/hid/uhid.rst). UHID_CREATE2 로 장치를 만들면 커널의
///     hid-generic 이 붙어 /dev/hidrawN 이 생긴다. hidraw 로 쓴 출력 보고서는 UHID_OUTPUT 으로 오고, UHID_INPUT2 로
///     보낸 입력 보고서는 hidraw 에서 읽힌다. 닫거나 UHID_DESTROY 를 보내면 장치가 빠진다(키를 뽑은 것과 같다).
/// </summary>
internal sealed class UhidDevice : IDisposable
{
    // struct uhid_event(packed) — type(4) + 가장 큰 공용체(uhid_create2_req 4372)
    private const int EventSize = 4376;
    private const uint Destroy = 1;
    private const uint Output = 6;
    private const uint GetReport = 9;
    private const uint GetReportReply = 10;
    private const uint Create2 = 11;
    private const uint Input2 = 12;
    private const uint SetReport = 13;
    private const uint SetReportReply = 14;
    private const ushort BusUsb = 0x03;
    private const short PollIn = 0x0001;

    /// <summary>FIDO CTAPHID 보고서 설명자 — 사용 페이지 0xF1D0, 사용 0x01, 64바이트 입력·출력(보고서 번호 없음).</summary>
    public static readonly byte[] FidoReportDescriptor =
    [
        0x06, 0xD0, 0xF1, // Usage Page (FIDO Alliance)
        0x09, 0x01, // Usage (CTAPHID)
        0xA1, 0x01, // Collection (Application)
        0x09, 0x20, //   Usage (Input Report Data)
        0x15, 0x00, //   Logical Minimum (0)
        0x26, 0xFF, 0x00, //   Logical Maximum (255)
        0x75, 0x08, //   Report Size (8)
        0x95, 0x40, //   Report Count (64)
        0x81, 0x02, //   Input (Data, Var, Abs)
        0x09, 0x21, //   Usage (Output Report Data)
        0x15, 0x00, //   Logical Minimum (0)
        0x26, 0xFF, 0x00, //   Logical Maximum (255)
        0x75, 0x08, //   Report Size (8)
        0x95, 0x40, //   Report Count (64)
        0x91, 0x02, //   Output (Data, Var, Abs)
        0xC0 // End Collection
    ];

    private readonly SafeFileHandle _handle;
    private readonly Lock _writeLock = new();
    private readonly Thread _reader;
    private volatile bool _stop;

    /// <summary>출력 보고서(64바이트, 보고서 번호 없이)를 받을 때마다 — uhid 읽기 스레드에서 부른다.</summary>
    private readonly Action<byte[]> _onOutput;

    public string Uniq { get; }

    public UhidDevice(string name, string uniq, Action<byte[]> onOutput)
    {
        Uniq = uniq;
        _onOutput = onOutput;
        _handle = File.OpenHandle("/dev/uhid", FileMode.Open, FileAccess.ReadWrite);
        var ev = new byte[EventSize];
        BinaryPrimitives.WriteUInt32LittleEndian(ev, Create2);
        Encoding.ASCII.GetBytes(name, ev.AsSpan(4, 127));
        Encoding.ASCII.GetBytes("virtual-fido/" + uniq, ev.AsSpan(132, 63));
        Encoding.ASCII.GetBytes(uniq, ev.AsSpan(196, 63));
        BinaryPrimitives.WriteUInt16LittleEndian(ev.AsSpan(260), (ushort)FidoReportDescriptor.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(ev.AsSpan(262), BusUsb);
        BinaryPrimitives.WriteUInt32LittleEndian(ev.AsSpan(264), 0x1209); // pid.codes 시험용
        BinaryPrimitives.WriteUInt32LittleEndian(ev.AsSpan(268), 0xF1D0);
        BinaryPrimitives.WriteUInt32LittleEndian(ev.AsSpan(272), 1);
        FidoReportDescriptor.CopyTo(ev, 280);
        WriteEvent(ev);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "uhid " + uniq };
        _reader.Start();
    }

    /// <summary>입력 보고서 하나(64바이트)를 보낸다 — 이쪽(호스트)의 hidraw 읽기에 나온다.</summary>
    public void SendInput(byte[] report)
    {
        var ev = new byte[EventSize];
        BinaryPrimitives.WriteUInt32LittleEndian(ev, Input2);
        BinaryPrimitives.WriteUInt16LittleEndian(ev.AsSpan(4), (ushort)report.Length);
        report.CopyTo(ev, 6);
        WriteEvent(ev);
    }

    /// <summary>장치를 뺀다(UHID_DESTROY) — 열려 있는 hidraw 는 읽기·쓰기가 실패한다.</summary>
    public void Dispose()
    {
        if (_stop) return;

        _stop = true;
        _reader.Join(1000);
        try
        {
            var ev = new byte[EventSize];
            BinaryPrimitives.WriteUInt32LittleEndian(ev, Destroy);
            WriteEvent(ev);
        }
        catch (IOException)
        {
        }

        _handle.Dispose();
    }

    private void WriteEvent(byte[] ev)
    {
        lock (_writeLock)
        {
            if (_handle.IsClosed) throw new IOException("uhid closed");

            var n = Native.write(_handle.DangerousGetHandle().ToInt32(), ev, ev.Length);
            if (n != ev.Length) throw new IOException($"uhid write {n} errno {Marshal.GetLastPInvokeError()}");
        }
    }

    private void ReadLoop()
    {
        var fd = _handle.DangerousGetHandle().ToInt32();
        var ev = new byte[EventSize];
        var pfd = new Native.PollFd[1];
        while (!_stop)
        {
            pfd[0] = new Native.PollFd { Fd = fd, Events = PollIn };
            if (Native.poll(pfd, 1, 100) <= 0 || (pfd[0].Revents & PollIn) == 0) continue;

            var n = Native.read(fd, ev, ev.Length);
            if (n < 4) continue;

            var type = BinaryPrimitives.ReadUInt32LittleEndian(ev);
            switch (type)
            {
                case Output:
                {
                    // uhid_output_req: data[4096] size(2) rtype(1). hidraw 쓰기는 보고서 번호 0 을 앞에 붙여 65바이트로 온다
                    var size = BinaryPrimitives.ReadUInt16LittleEndian(ev.AsSpan(4 + 4096));
                    var data = ev.AsSpan(4, Math.Min((int)size, 4096)).ToArray();
                    if (data.Length == 65 && data[0] == 0) data = data[1..];
                    try
                    {
                        _onOutput(data);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"virtual key: {ex}");
                    }

                    break;
                }
                case GetReport or SetReport:
                {
                    // 기능 보고서는 없다 — EIO 로 답한다(커널이 기다리지 않게)
                    var reply = new byte[EventSize];
                    BinaryPrimitives.WriteUInt32LittleEndian(reply, type == GetReport ? GetReportReply : SetReportReply);
                    ev.AsSpan(4, 4).CopyTo(reply.AsSpan(4)); // id
                    BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(8), 5); // EIO
                    WriteEvent(reply);
                    break;
                }
            }
        }
    }

    /// <summary>/sys/class/hidraw 에서 이 장치(HID_UNIQ)의 /dev/hidrawN 을 찾는다. 아직 없으면 null.</summary>
    public string? FindHidraw()
    {
        if (!Directory.Exists("/sys/class/hidraw")) return null;

        foreach (var dir in Directory.EnumerateDirectories("/sys/class/hidraw"))
            try
            {
                var uevent = File.ReadAllText(Path.Combine(dir, "device", "uevent"));
                if (uevent.Contains("HID_UNIQ=" + Uniq + "\n", StringComparison.Ordinal))
                    return "/dev/" + Path.GetFileName(dir);
            }
            catch (IOException)
            {
            }

        return null;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        [DllImport("libc", SetLastError = true)]
        public static extern int poll([In, Out] PollFd[] fds, nuint nfds, int timeout);

        [DllImport("libc", SetLastError = true)]
        public static extern nint read(int fd, [Out] byte[] buf, nint count);

        [DllImport("libc", SetLastError = true)]
        public static extern nint write(int fd, [In] byte[] buf, nint count);
    }
}
