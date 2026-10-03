using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>끌어낼 파일 하나 — 내용은 받는 쪽이 달라고 할 때 <see cref="Fetch" /> 로 임시 파일에 받는다.</summary>
internal sealed record VirtualFile(string Name, long Size, DateTime? Modified, Func<string> Fetch);

/// <summary>
///     탐색기용 가상 파일 끌기 자료(FileGroupDescriptorW + FileContents) — 끄는 동안에는 이름·크기만 알리고,
///     놓은 뒤 받는 쪽이 파일마다 내용을 달라고 하면 그때 게스트에서 받는다. 비동기(<see cref="IDataObjectAsyncCapability" />)
///     를 알려 탐색기가 놓자마자 돌아오고 자기 작업 스레드·진행 창에서 받게 한다. 이 객체의 메서드는 COM 스레드에서
///     불리므로 UI 를 건드리지 않는다.
/// </summary>
internal sealed class VirtualFileDataObject(IReadOnlyList<VirtualFile> files)
    : IComDataObject, IDataObjectAsyncCapability
{
    private const int DvEFormatEtc = unchecked((int)0x80040064);
    private const int DvELIndex = unchecked((int)0x80040068);
    private const int OleEAdviseNotSupported = unchecked((int)0x80040003);
    private const int DataSSameFormatEtc = 0x00040130;
    private const int ENotImpl = unchecked((int)0x80004001);
    private const int DropEffectCopy = 1;

    private static readonly long MinFileTime = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    private static readonly short DescriptorFormat = Format("FileGroupDescriptorW");
    private static readonly short ContentsFormat = Format("FileContents");
    private static readonly short DropEffectFormat = Format("Preferred DropEffect");

    private bool _async = true;
    private int _failed;
    private int _fetched;

    /// <summary>받는 쪽이 비동기로 받기 시작했다(끝은 <see cref="Finished" /> 로).</summary>
    public bool StartedAsync { get; private set; }

    /// <summary>비동기 받기가 끝났을 때(COM 스레드) — 실패한 파일이 있었으면 true.</summary>
    public event Action<bool>? Finished;

    /// <summary>받다가 실패한 파일이 있었다.</summary>
    public bool AnyFailed => _failed > 0;

    /// <summary>받는 쪽에 건넨 파일 수.</summary>
    public int Fetched => _fetched;

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.cfFormat == DescriptorFormat && Has(format, TYMED.TYMED_HGLOBAL))
        {
            medium = Global(Descriptor());
            return;
        }

        if (format.cfFormat == DropEffectFormat && Has(format, TYMED.TYMED_HGLOBAL))
        {
            medium = Global(BitConverter.GetBytes(DropEffectCopy));
            return;
        }

        if (format.cfFormat != ContentsFormat || !Has(format, TYMED.TYMED_ISTREAM))
            throw new COMException(null, DvEFormatEtc);
        if (format.lindex < 0 || format.lindex >= files.Count) throw new COMException(null, DvELIndex);

        medium = new STGMEDIUM
        {
            tymed = TYMED.TYMED_ISTREAM,
            unionmember = Marshal.GetComInterfaceForObject<ComStream, IStream>(Open(files[format.lindex]))
        };
    }

    /// <summary>파일 내용을 받아 스트림으로 — 실패하면 받는 쪽에 오류(HRESULT)로 알린다(받는 쪽이 오류 창을 띄운다).</summary>
    private ComStream Open(VirtualFile file)
    {
        try
        {
            var path = file.Fetch();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Interlocked.Increment(ref _fetched);
            return new ComStream(stream, file.Name);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            App.Log($"[게스트 파일] 끌어내기 받기 실패({file.Name}): {ex}");
            throw new COMException(ex.Message, ex.HResult < 0 ? ex.HResult : unchecked((int)0x80004005));
        }
    }

    public int QueryGetData(ref FORMATETC format)
    {
        var ok = (format.cfFormat == DescriptorFormat || format.cfFormat == DropEffectFormat)
                 && Has(format, TYMED.TYMED_HGLOBAL)
                 || format.cfFormat == ContentsFormat && Has(format, TYMED.TYMED_ISTREAM);
        return ok ? 0 : DvEFormatEtc;
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException(null, ENotImpl);

        return new FormatEnumerator(
        [
            Etc(DescriptorFormat, TYMED.TYMED_HGLOBAL, -1),
            Etc(ContentsFormat, TYMED.TYMED_ISTREAM, -1),
            Etc(DropEffectFormat, TYMED.TYMED_HGLOBAL, -1)
        ]);
    }

    public int GetCanonicalFormatEtc(ref FORMATETC format, out FORMATETC result)
    {
        result = format;
        result.ptd = IntPtr.Zero;
        return DataSSameFormatEtc;
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException(null, DvEFormatEtc);

    // 받는 쪽이 알려 주는 값(끝난 효과·끌기 설명 등)은 쓰지 않는다
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) =>
        throw new COMException(null, ENotImpl);

    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection)
    {
        connection = 0;
        return OleEAdviseNotSupported;
    }

    public void DUnadvise(int connection) => throw new COMException(null, OleEAdviseNotSupported);

    public int EnumDAdvise(out IEnumSTATDATA? advise)
    {
        advise = null;
        return OleEAdviseNotSupported;
    }

    public void SetAsyncMode(bool doOpAsync) => _async = doOpAsync;
    public void GetAsyncMode(out bool isOpAsync) => isOpAsync = _async;
    public void StartOperation(IntPtr bindContext) => StartedAsync = true;
    public void InOperation(out bool inAsyncOp) => inAsyncOp = StartedAsync;

    public void EndOperation(int result, IntPtr bindContext, uint effects)
    {
        Finished?.Invoke(AnyFailed || result < 0);
    }

    /// <summary>FILEGROUPDESCRIPTORW — 개수 + 파일마다 592바이트(FILEDESCRIPTORW).</summary>
    private byte[] Descriptor()
    {
        const int Size = 592;
        const uint Flags = 0x80000000 | 0x4000 | 0x40 | 0x20 | 0x4; // UNICODE·PROGRESSUI·FILESIZE·WRITESTIME·ATTRIBUTES
        const uint Normal = 0x80; // FILE_ATTRIBUTE_NORMAL
        var bytes = new byte[4 + Size * files.Count];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var at = 4 + Size * i;
            var file = files[i];
            BitConverter.TryWriteBytes(bytes.AsSpan(at), Flags);
            BitConverter.TryWriteBytes(bytes.AsSpan(at + 36), Normal);
            var modified = file.Modified ?? DateTime.UtcNow;
            var written = modified.Ticks <= MinFileTime ? 0 : modified.ToFileTimeUtc(); // 1601 전이면 0
            BitConverter.TryWriteBytes(bytes.AsSpan(at + 56), written);
            BitConverter.TryWriteBytes(bytes.AsSpan(at + 64), (uint)(file.Size >> 32));
            BitConverter.TryWriteBytes(bytes.AsSpan(at + 68), (uint)(file.Size & 0xFFFFFFFF));
            var name = Truncate(file.Name, 259);
            System.Text.Encoding.Unicode.GetBytes(name, bytes.AsSpan(at + 72));
        }

        return bytes;
    }

    /// <summary>이름을 max 자로 — 서로게이트 쌍을 가르지 않는다.</summary>
    private static string Truncate(string name, int max)
    {
        if (name.Length <= max) return name;

        return char.IsHighSurrogate(name[max - 1]) ? name[..(max - 1)] : name[..max];
    }

    /// <summary>받는 쪽이 ReleaseStgMedium(GlobalFree)으로 푸는 HGLOBAL.</summary>
    private static STGMEDIUM Global(byte[] bytes)
    {
        const uint GmemMoveable = 0x0002;
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero) throw new OutOfMemoryException();

        Marshal.Copy(bytes, 0, GlobalLock(handle), bytes.Length);
        GlobalUnlock(handle);
        return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    private static bool Has(FORMATETC format, TYMED tymed) => (format.tymed & tymed) != 0;

    private static FORMATETC Etc(short format, TYMED tymed, int index) => new()
    {
        cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = tymed
    };

    private static short Format(string name) => unchecked((short)DataFormats.GetDataFormat(name).Id);

    /// <summary>제공하는 형식 목록.</summary>
    private sealed class FormatEnumerator(FORMATETC[] formats) : IEnumFORMATETC
    {
        private int _index;

        public int Next(int count, FORMATETC[] result, int[]? fetched)
        {
            var n = 0;
            while (n < count && _index < formats.Length) result[n++] = formats[_index++];
            if (fetched is { Length: > 0 }) fetched[0] = n;
            return n == count ? 0 : 1;
        }

        public int Skip(int count)
        {
            _index = Math.Min(formats.Length, _index + count);
            return _index < formats.Length ? 0 : 1;
        }

        public int Reset()
        {
            _index = 0;
            return 0;
        }

        public void Clone(out IEnumFORMATETC clone) => clone = new FormatEnumerator(formats) { _index = _index };
    }
}
