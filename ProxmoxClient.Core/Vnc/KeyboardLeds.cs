namespace ProxmoxClient.Core.Vnc;

/// <summary>
///     키보드 잠금 키 상태(LED) — QEMU LED State 의사 인코딩(-261)이 보내는 1바이트:
///     비트0 ScrollLock, 비트1 NumLock, 비트2 CapsLock.
/// </summary>
public readonly record struct KeyboardLeds(bool ScrollLock, bool NumLock, bool CapsLock)
{
    public static KeyboardLeds FromQemu(byte state)
    {
        return new KeyboardLeds((state & 1) != 0, (state & 2) != 0, (state & 4) != 0);
    }
}
