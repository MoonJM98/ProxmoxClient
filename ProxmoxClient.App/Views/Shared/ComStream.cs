using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     읽기 전용 COM IStream — 끌어내기로 받은 임시 파일을 받는 쪽(탐색기)에 건넨다. 받는 쪽이 놓아 주는 때를 알 수
///     없으므로 파일은 지우지 않는다(지난 끌어내기 폴더는 다음 끌기 때 정리).
/// </summary>
internal sealed class ComStream(Stream stream, string name) : IStream
{
    private const int StgENotImplemented = unchecked((int)0x80030001); // STG_E_INVALIDFUNCTION

    public void Read(byte[] buffer, int count, IntPtr bytesRead)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read == 0) break;
            total += read;
        }

        if (bytesRead != IntPtr.Zero) Marshal.WriteInt32(bytesRead, total);
    }

    public void Seek(long offset, int origin, IntPtr newPosition)
    {
        var position = stream.Seek(offset, (SeekOrigin)origin);
        if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, position);
    }

    public void Stat(out STATSTG stat, int flags)
    {
        const int StatFlagNoName = 1;
        stat = new STATSTG
        {
            type = 2, // STGTY_STREAM
            cbSize = stream.Length,
            pwcsName = (flags & StatFlagNoName) != 0 ? null! : name
        };
    }

    public void Clone(out IStream clone) => throw new COMException(null, StgENotImplemented);
    public void Commit(int flags) { }
    public void CopyTo(IStream target, long count, IntPtr read, IntPtr written) =>
        throw new COMException(null, StgENotImplemented);
    public void LockRegion(long offset, long count, int type) => throw new COMException(null, StgENotImplemented);
    public void Revert() { }
    public void SetSize(long size) => throw new COMException(null, StgENotImplemented);
    public void UnlockRegion(long offset, long count, int type) => throw new COMException(null, StgENotImplemented);
    public void Write(byte[] buffer, int count, IntPtr written) => throw new COMException(null, StgENotImplemented);
}
