using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ProxmoxClient.App.Views;

/// <summary>키보드 — 전역 후킹(Win·Alt+Tab 등 전달)과 가상 키 → X keysym 변환.</summary>
public partial class ConsoleWindow
{
    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    /// <summary>
    ///     키를 가로채도 되는 상태 — 이 창이 활성(WPF IsActive)이고, Windows 포어그라운드 창이며, 최소화되지 않았을 때만.
    ///     셋 중 하나라도 아니면 키는 호스트(다른 창)로 그대로 간다.
    /// </summary>
    private bool CanCaptureKeyboard()
    {
        return IsActive && WindowState != WindowState.Minimized
                        && GetForegroundWindow() == new WindowInteropHelper(this).Handle;
    }

    private void InstallKeyboardHook()
    {
        // 활성화 알림 순간엔 포어그라운드가 아직 안 바뀌었을 수 있다 — 설치는 활성·최소화 아님만 보고, 키마다 모두 확인한다
        if (_keyboardHook != IntPtr.Zero || !IsActive || WindowState == WindowState.Minimized) return;

        _hookProc = KeyboardHookCallback;
        _keyboardHook = SetWindowsHookEx(WhKeyboardLL, _hookProc, GetModuleHandle(null), 0);
        if (_keyboardHook != IntPtr.Zero) OnKeyboardCaptured();
    }
    private void RemoveKeyboardHook()
    {
        if (_keyboardHook == IntPtr.Zero) return;

        UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
        _heldModifiers.Clear();
        ReleasePressedKeys();
        OnKeyboardReleased(); // 잠금 키를 잡기 전 PC 상태로
    }
    /// <summary>
    ///     포커스를 잃는 순간 눌려 있던 키의 key-up 을 게스트에 보낸다.
    ///     (Alt+Tab 등으로 떠나면 up 이벤트가 훅에 오지 않아 게스트에서 키가 계속 눌린 상태로 남는다)
    /// </summary>
    private void ReleasePressedKeys()
    {
        foreach (var (xtScanCode, keysym) in _pressedKeys.Values) SendGuestKey(xtScanCode, keysym, false);

        _pressedKeys.Clear();
        UpdateModifierIndicators();
    }
    private void SendGuestKey(int xtScanCode, int keysym, bool down)
    {
        if (_session?.IsConnected == true) _ = _session.SendKeyAsync(xtScanCode, down, keysym);
    }
    /// <summary>
    ///     저수준 훅 정보 → XT 스캔코드(확장 키는 0xE0nn). 0 이면 스캔코드 전송 불가(keysym 경로 사용).
    /// </summary>
    private static int ToXtScanCode(int vk, KbdLlHookStruct hook)
    {
        const int VkPause = 0x13;
        const int VkNumLock = 0x90;
        const int VkRShift = 0xA1;

        if (vk == VkPause) return 0; // E1 1D 45 멀티바이트 시퀀스 — keysym 으로 전송

        var scan = (int)hook.ScanCode & 0xFF;
        if (scan == 0) return 0;

        // NumLock·RShift 는 훅이 extended 로 보고하지만 XT 에서는 비확장 코드
        if (vk is VkNumLock or VkRShift) return scan;

        return (hook.Flags & LlkhfExtended) != 0 ? 0xE000 | scan : scan;
    }
    /// <summary>
    ///     저수준 키보드 훅 — 창이 포어그라운드일 때 모든 키를 가로채 VM에 전달.
    ///     IME(한/영), Win, Alt+Tab 등 호스트 OS에 도달하지 않게 차단.
    ///     Alt+F4만 예외로 통과. 연결돼 있지 않으면 가로채지 않는다.
    /// </summary>
    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_keyboardHook, code, wParam, lParam);

        var msg = wParam.ToInt32();
        var down = msg is WmKeydown or WmSyskeydown;
        var up = msg is WmKeyup or WmSyskeyup;
        if (!down && !up) return CallNextHookEx(_keyboardHook, code, wParam, lParam);

        // 잠금 키 맞추기로 우리가 흉내 낸 입력 — PC 상태만 바꾸고 게스트에는 보내지 않는다
        const int ExtraInfoOffset = 16; // KBDLLHOOKSTRUCT.dwExtraInfo(x86·x64 모두)
        if (Marshal.ReadIntPtr(lParam, ExtraInfoOffset) == SyntheticKeyMarker)
            return CallNextHookEx(_keyboardHook, code, wParam, lParam);

        if (!CanCaptureKeyboard())
        {
            // 키는 호스트로 넘긴다. 비활성·최소화인데 훅이 남았으면(알림을 놓친 경우) 훅을 걷고 눌린 키도 뗀다 —
            // 포어그라운드만 잠깐 다른 경우(활성화 직후)는 훅을 둔다
            var next = CallNextHookEx(_keyboardHook, code, wParam, lParam);
            if (!IsActive || WindowState == WindowState.Minimized)
                Dispatcher.BeginInvoke(new Action(RemoveKeyboardHook));
            return next;
        }

        // PtrToStructure<T> 는 내부적으로 박싱 할당 — 키 입력마다 호출되는 훅이므로 필요한 필드만 오프셋으로 직접 읽는다
        var hook = new KbdLlHookStruct
        {
            VkCode = (uint)Marshal.ReadInt32(lParam, 0),
            ScanCode = (uint)Marshal.ReadInt32(lParam, 4),
            Flags = (uint)Marshal.ReadInt32(lParam, 8)
        };
        var vk = (int)hook.VkCode;

        // Alt+F4: 창 닫기 허용
        if (vk == 0x73 && _heldModifiers.Contains(0xA4)) return CallNextHookEx(_keyboardHook, code, wParam, lParam);

        var isModifier = vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
        if (isModifier)
        {
            if (down)
                _heldModifiers.Add(vk);
            else
                _heldModifiers.Remove(vk);
        }

        // 연결 전·끊긴 뒤(정지 안내 등)에는 보낼 곳이 없다 — 키를 삼키지 않고 넘긴다(Alt+Tab·Win·Tab·Enter 가 먹게).
        // 수정 키 상태는 위에서 계속 따라가 둔다(연결된 뒤 Alt 를 떼도 어긋나지 않게)
        if (_session?.IsConnected != true) return CallNextHookEx(_keyboardHook, code, wParam, lParam);

        // Ctrl+Alt+Del: Windows SAS를 차단하고 VM에 직접 전송
        if (vk == 0x2E && down && _heldModifiers.Contains(0xA2) && _heldModifiers.Contains(0xA4))
        {
            if (_session?.IsConnected == true) _ = _session.SendCtrlAltDelAsync();
            return 1;
        }

        var shift = _heldModifiers.Contains(0xA0) || _heldModifiers.Contains(0xA1);
        var keysym = vk == 0x0D && (hook.Flags & LlkhfExtended) != 0
            ? KeysymKpEnter // 넘패드 Enter 는 VK 가 같고 extended 플래그로만 구분된다
            : VirtualKeyToKeysym(vk, shift ? ModifierKeys.Shift : ModifierKeys.None);

        var xtScanCode = ToXtScanCode(vk, hook);

        if (down)
        {
            if (keysym != 0 || xtScanCode != 0)
            {
                _pressedKeys[vk] = (xtScanCode, keysym);
                SendGuestKey(xtScanCode, keysym, true);
            }
        }
        else if (_pressedKeys.Remove(vk, out var pressed))
        {
            SendGuestKey(pressed.XtScanCode, pressed.Keysym, false);
        }
        else if (keysym != 0 || xtScanCode != 0)
        {
            SendGuestKey(xtScanCode, keysym, false);
        }

        if (isModifier || vk is 0x5B or 0x5C) UpdateModifierIndicators(); // 상태 표시줄의 Ctrl·Alt·Shift·Win

        if (keysym == 0 && xtScanCode == 0)
        {
            // 훅 콜백 안에서 동기 파일 IO 금지 — 지연되면 Windows 가 훅을 조용히 제거한다
            var flags = hook.Flags;
            _ = Task.Run(() => App.Log($"[훅] 매핑 없음 VK=0x{vk:X2} flags=0x{flags:X} down={down}"));
        }

        return 1;
    }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        SendKey(e, true);
    }
    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        SendKey(e, false);
    }
    private void SendKey(KeyEventArgs e, bool down)
    {
        if (_session?.IsConnected != true || !CanCaptureKeyboard()) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ConsumeSyntheticLockKey(KeyInterop.VirtualKeyFromKey(key)))
        {
            e.Handled = true; // 잠금 키 맞추기로 흉내 낸 입력 — 게스트로 보내지 않는다
            return;
        }
        if (key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return; // Alt+F4 창 닫기 허용

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return;

        var scan = MapVirtualKey((uint)virtualKey, MapVkVkToVscEx);
        if (scan == 0) return;

        var keysym = VirtualKeyToKeysym(virtualKey, Keyboard.Modifiers);
        _ = _session.SendKeyAsync((int)(scan & 0xFFFF), down, keysym);
        e.Handled = true;
    }
    /// <summary>Windows VK → X11 keysym 기본 매핑 (QEMU가 LED 상태 추적에 사용).</summary>
    private static int VirtualKeyToKeysym(int vk, ModifierKeys modifiers)
    {
        if (vk is >= 0x41 and <= 0x5A) return modifiers.HasFlag(ModifierKeys.Shift) ? vk : vk + 0x20;

        if (vk is >= 0x30 and <= 0x39) return vk;

        if (vk is >= 0x60 and <= 0x69) return vk - 0x60 + 0xFFB0; // KP_0..KP_9 — 일반 숫자로 보내면 넘패드 키로 인식되지 않는다

        return vk switch
        {
            0x2D => 0xFF63, // Insert
            0x2C => 0xFF61, // PrintScreen
            0x91 => 0xFF14, // ScrollLock
            0x13 => 0xFF13, // Pause
            0x5D => 0xFF67, // Apps(메뉴)
            0x6A => 0xFFAA, // KP_Multiply
            0x6B => 0xFFAB, // KP_Add
            0x6C => 0xFFAC, // KP_Separator
            0x6D => 0xFFAD, // KP_Subtract
            0x6E => 0xFFAE, // KP_Decimal
            0x6F => 0xFFAF, // KP_Divide
            0xE2 => 0x3C, // OEM_102 (<>)
            0x20 => 0x20, // Space
            0x0D => 0xFF0D, // Enter
            0x09 => 0xFF09, // Tab
            0x1B => 0xFF1B, // Escape
            0x08 => 0xFF08, // BackSpace
            0x2E => 0xFFFF, // Delete
            0x24 => 0xFF50, // Home
            0x23 => 0xFF57, // End
            0x25 => 0xFF51, // Left
            0x26 => 0xFF52, // Up
            0x27 => 0xFF53, // Right
            0x28 => 0xFF54, // Down
            0x70 => 0xFFBE, // F1
            0x71 => 0xFFBF, // F2
            0x72 => 0xFFC0, // F3
            0x73 => 0xFFC1, // F4
            0x74 => 0xFFC2, // F5
            0x75 => 0xFFC3, // F6
            0x76 => 0xFFC4, // F7
            0x77 => 0xFFC5, // F8
            0x78 => 0xFFC6, // F9
            0x79 => 0xFFC7, // F10
            0x7A => 0xFFC8, // F11
            0x7B => 0xFFC9, // F12
            0xA0 => 0xFFE1, // LShift
            0xA1 => 0xFFE2, // RShift
            0xA2 => 0xFFE3, // LCtrl
            0xA3 => 0xFFE4, // RCtrl
            0xA4 => 0xFFE9, // LAlt
            0xA5 => 0xFFEA, // RAlt
            0x21 => 0xFF55, // PageUp
            0x22 => 0xFF56, // PageDown
            0x90 => 0xFF7F, // NumLock
            0x14 => 0xFFE5, // CapsLock
            0x15 => 0xFFEA, // 한/영 → RAlt (한국어 키보드에서 물리적으로 RAlt 자리)
            0x19 => 0xFFE4, // 한자 → RCtrl (물리적으로 RCtrl 자리)
            0x1C => 0xFF23, // 변환(Henkan)
            0x1D => 0xFF22, // 무변환(Muhenkan)
            0x1E => 0xFF0D,
            0x5B => 0xFFEB,
            0x5C => 0xFFEC,
            0xC0 => 0x60,
            0xBD => 0x2D,
            0xBB => 0x3D,
            0xBC => 0x2C,
            0xBE => 0x2E,
            0xBF => 0x2F,
            0xDC => 0x5C,
            0xBA => 0x3B,
            0xDE => 0x27,
            0xDD => 0x5D,
            0xDB => 0x5B,
            _ => 0
        };
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }
}
