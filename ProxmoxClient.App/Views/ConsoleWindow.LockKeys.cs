using System.Runtime.InteropServices;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Views;

/// <summary>
///     잠금 키(CapsLock·NumLock·ScrollLock) 맞추기 — 창이 키보드를 잡는 동안 PC 키보드 상태를 게스트 LED 와 같게 두고,
///     놓으면 잡기 전 PC 상태로 되돌린다. 상태는 키 입력을 흉내 내야만 바뀌므로(SendInput), 그 입력에는 표식을 달아
///     키보드 훅·WPF 키 처리 모두 게스트로 보내지 않게 한다.
/// </summary>
public partial class ConsoleWindow
{
    private const int VkCapital = 0x14;
    private const int VkNumLock = 0x90;
    private const int VkScroll = 0x91;

    /// <summary>우리가 흉내 낸 키 입력의 dwExtraInfo 표식("PVMC").</summary>
    private static readonly IntPtr SyntheticKeyMarker = new(0x50564D43);

    /// <summary>WPF 키 처리가 건너뛸, 흉내 낸 잠금 키 입력 수(누름·뗌 각각 1).</summary>
    private readonly Dictionary<int, int> _syntheticLockKeys = [];

    /// <summary>키보드를 잡기 전 PC 잠금 키 상태 — 놓을 때 되돌린다(잡지 않았으면 null).</summary>
    private KeyboardLeds? _hostLedsBeforeCapture;

    /// <summary>서버가 알려 준 게스트 LED(아직 모르면 null — 그때는 PC 상태를 건드리지 않는다).</summary>
    private KeyboardLeds? _guestLeds;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    /// <summary>키보드를 잡은 직후 — PC 상태를 기억하고 게스트 LED 를 PC 에 적용한다.</summary>
    private void OnKeyboardCaptured()
    {
        _syntheticLockKeys.Clear();
        _hostLedsBeforeCapture = ReadHostLeds();
        if (_guestLeds is { } guest) ApplyHostLeds(guest, countForWpf: true);
    }

    /// <summary>키보드를 놓은 뒤 — 잡기 전 PC 상태로 되돌린다(훅은 이미 없다).</summary>
    private void OnKeyboardReleased()
    {
        // 창이 아직 활성이면(캡처만 꺼짐) 되돌리는 입력이 WPF 로 들어오므로 세어 두어 게스트로 새지 않게 한다
        _syntheticLockKeys.Clear();
        if (_hostLedsBeforeCapture is { } before) ApplyHostLeds(before, countForWpf: IsActive);
        _hostLedsBeforeCapture = null;
    }

    /// <summary>게스트 LED 가 바뀜 — 키보드를 잡고 있으면 PC 키보드도 따라간다.</summary>
    private void OnGuestLeds(KeyboardLeds leds)
    {
        _guestLeds = leds;
        if (_keyboardHook != IntPtr.Zero && _hostLedsBeforeCapture is not null) ApplyHostLeds(leds, countForWpf: true);
    }

    private static KeyboardLeds ReadHostLeds()
    {
        return new KeyboardLeds((GetKeyState(VkScroll) & 1) != 0, (GetKeyState(VkNumLock) & 1) != 0,
            (GetKeyState(VkCapital) & 1) != 0);
    }

    /// <param name="countForWpf">이 창이 입력을 받을 때(잡은 동안) — WPF 키 처리가 건너뛰도록 센다.</param>
    private void ApplyHostLeds(KeyboardLeds target, bool countForWpf)
    {
        var current = ReadHostLeds();
        var keys = new List<int>();
        if (current.CapsLock != target.CapsLock) keys.Add(VkCapital);
        if (current.NumLock != target.NumLock) keys.Add(VkNumLock);
        if (current.ScrollLock != target.ScrollLock) keys.Add(VkScroll);
        if (keys.Count == 0) return;

        var inputs = keys.SelectMany(vk => new[] { KeyInput(vk, false), KeyInput(vk, true) }).ToArray();
        if (countForWpf)
            foreach (var vk in keys)
                _syntheticLockKeys[vk] = _syntheticLockKeys.GetValueOrDefault(vk) + 2;
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            App.Log($"[잠금 키] PC 상태 맞추기 실패: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>우리가 흉내 낸 잠금 키 입력인지(WPF 키 처리용) — 맞으면 센 수를 하나 줄인다.</summary>
    private bool ConsumeSyntheticLockKey(int vk)
    {
        if (!_syntheticLockKeys.TryGetValue(vk, out var left) || left <= 0) return false;
        _syntheticLockKeys[vk] = left - 1;
        return true;
    }

    private static Input KeyInput(int vk, bool keyUp)
    {
        const uint KeyEventFKeyUp = 0x0002;
        const uint KeyEventFExtendedKey = 0x0001;
        return new Input
        {
            Type = 1, // INPUT_KEYBOARD
            Keyboard = new KeybdInput
            {
                Vk = (ushort)vk,
                // NumLock 은 확장 키로 보내야 제대로 토글된다
                Flags = (keyUp ? KeyEventFKeyUp : 0) | (vk == VkNumLock ? KeyEventFExtendedKey : 0),
                ExtraInfo = SyntheticKeyMarker
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public KeybdInput Keyboard;
    }

    /// <summary>INPUT 공용체의 크기가 MOUSEINPUT 만큼 되도록 뒤를 채운다(SendInput 이 cbSize 로 검사한다).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
        private readonly uint _padding1;
        private readonly uint _padding2;
    }
}
