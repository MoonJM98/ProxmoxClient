using System.Windows;
using System.Windows.Controls.Primitives;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views;

/// <summary>
///     특수 키 — 웹 UI noVNC 의 키 모음과 같다. Ctrl·Alt·Shift·Win 은 누르면 게스트에서 눌린 채로 남고(체크 상태),
///     다시 누르면 뗀다. 그 사이 PC 키보드로 누른 키와 합쳐진다(예: Ctrl 켜고 PC 에서 C). Tab·Esc·Ctrl+Alt+Del 은 한 번 누른다.
/// </summary>
public partial class ConsoleWindow
{
    /// <summary>토글 이름 → (XT 스캔코드, X keysym).</summary>
    private static readonly Dictionary<string, (int Scan, int Keysym)> StickyKeys = new(StringComparer.Ordinal)
    {
        ["Ctrl"] = (0x1D, 0xFFE3),
        ["Alt"] = (0x38, 0xFFE9),
        ["Shift"] = (0x2A, 0xFFE1),
        ["Win"] = (0xE05B, 0xFFEB)
    };

    private static readonly Dictionary<string, (int Scan, int Keysym)> TapKeys = new(StringComparer.Ordinal)
    {
        ["Tab"] = (0x0F, 0xFF09),
        ["Esc"] = (0x01, 0xFF1B)
    };

    private void OnKeysMenu(object sender, RoutedEventArgs e)
    {
        KeysPopup.IsOpen = true;
    }

    private void OnStickyKeyToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string name } toggle
            || !StickyKeys.TryGetValue(name, out var key)) return;

        SendGuestKey(key.Scan, key.Keysym, toggle.IsChecked == true);
        UpdateModifierIndicators();
    }

    private void OnTapKey(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;

        if (name == "CtrlAltDel")
        {
            if (_session?.IsConnected == true) _ = _session.SendCtrlAltDelAsync();
            return;
        }

        if (!TapKeys.TryGetValue(name, out var key)) return;
        SendGuestKey(key.Scan, key.Keysym, true);
        SendGuestKey(key.Scan, key.Keysym, false);
    }

    /// <summary>눌린 채인 키를 모두 뗀다 — 연결이 끊기거나 창을 닫을 때 게스트에 키가 눌린 채 남지 않게.</summary>
    private void ReleaseStickyKeys()
    {
        foreach (var toggle in new[] { KeyCtrl, KeyAlt, KeyShift, KeyWin })
            toggle.IsChecked = false; // Unchecked 가 key-up 을 보낸다(끊겼으면 보내지 않는다)
    }

    /// <summary>
    ///     상태 표시줄 오른쪽 Ctrl·Alt·Shift·Win — 특수 키로 누른 채 둔 것이나 PC 키보드로 누르고 있는 것이 있으면 켠다.
    /// </summary>
    private void UpdateModifierIndicators()
    {
        SetIndicator(ModCtrl, KeyCtrl.IsChecked == true || IsPhysicallyHeld(0xA2, 0xA3));
        SetIndicator(ModAlt, KeyAlt.IsChecked == true || IsPhysicallyHeld(0xA4, 0xA5));
        SetIndicator(ModShift, KeyShift.IsChecked == true || IsPhysicallyHeld(0xA0, 0xA1));
        SetIndicator(ModWin, KeyWin.IsChecked == true || IsPhysicallyHeld(0x5B, 0x5C));
    }

    private bool IsPhysicallyHeld(int leftVk, int rightVk)
    {
        return _pressedKeys.ContainsKey(leftVk) || _pressedKeys.ContainsKey(rightVk);
    }

    private static void SetIndicator(FrameworkElement tag, bool on)
    {
        tag.Tag = on ? "on" : null; // 스타일(ModifierTag)이 켜진 모습으로 바꾼다
    }
}
