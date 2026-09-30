using System.Runtime.InteropServices;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     OLE 끌기(DoDragDrop)를 직접 부른다 — WPF DragDrop 은 자료를 자기 DataObject 로 감싸 받는 쪽(탐색기)이
///     <see cref="IDataObjectAsyncCapability" /> 를 찾지 못한다(그러면 탐색기가 비동기로 받지 않는다).
///     UI(STA) 스레드에서 부른다. 끝날 때까지 이 호출 안에서 메시지를 돌린다.
/// </summary>
internal static class VirtualFileDrag
{
    private const int DragDropSDrop = 0x00040100;
    private const int DragDropSCancel = 0x00040101;
    private const int DragDropSUseDefaultCursors = 0x00040102;
    private const uint MkLButton = 0x0001;

    /// <summary>끌기 — 놓았으면 true(받는 쪽이 복사했는지는 자료 객체가 알려 준다).</summary>
    public static bool Run(IComDataObject data, int allowedEffects)
    {
        var hr = DoDragDrop(data, new DropSource(), allowedEffects, out _);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return hr == DragDropSDrop;
    }

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(IComDataObject data, IOleDropSource source, int okEffects, out int effect);

    [ComImport]
    [Guid("00000121-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleDropSource
    {
        [PreserveSig]
        int QueryContinueDrag(int escapePressed, uint keyState);

        [PreserveSig]
        int GiveFeedback(int effect);
    }

    /// <summary>Esc 면 취소, 왼쪽 버튼을 떼면 놓기, 커서는 기본 모양.</summary>
    private sealed class DropSource : IOleDropSource
    {
        public int QueryContinueDrag(int escapePressed, uint keyState)
        {
            if (escapePressed != 0) return DragDropSCancel;

            return (keyState & MkLButton) == 0 ? DragDropSDrop : 0;
        }

        public int GiveFeedback(int effect) => DragDropSUseDefaultCursors;
    }
}

/// <summary>
///     받는 쪽이 끌어온 자료를 뒤에서(자기 작업 스레드에서) 받게 하는 약속 — 탐색기는 이것이 있으면 놓자마자 돌아오고
///     파일 내용은 진행 창을 띄워 따로 가져간다.
/// </summary>
[ComImport]
[Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDataObjectAsyncCapability
{
    void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool doOpAsync);

    void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool isOpAsync);

    void StartOperation(IntPtr bindContext);

    void InOperation([MarshalAs(UnmanagedType.Bool)] out bool inAsyncOp);

    void EndOperation(int result, IntPtr bindContext, uint effects);
}
