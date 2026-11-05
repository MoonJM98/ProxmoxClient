using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Threading;

namespace ProxmoxClient.UI.Views.Controls;

/// <summary>
///     터미널·콘솔이 쓰는 보이지 않는 입력 칸과 화면 키보드(Android). 실기기에서 확인한 두 문제를 막는다:
///     <list type="bullet">
///         <item>한 줄 칸이면 키보드의 Enter 가 '완료'가 되어 키보드가 닫힌다 → 여러 줄 칸 + Return 키로.</item>
///         <item>키보드가 닫힌 뒤 칸에 이미 포커스가 있으면 다시 눌러도 열리지 않는다 → 포커스를 잠깐 옮겼다 돌려준다
///             (처음 키보드가 열린 것과 같은 포커스 전환).</item>
///     </list>
/// </summary>
public static class SoftKeyboard
{
    public static void Configure(TextBox input)
    {
        input.AcceptsReturn = true; // Enter 는 KeyDown 에서 가로채 보낸다 — 칸에는 줄바꿈이 남지 않는다
        TextInputOptions.SetMultiline(input, true);
        TextInputOptions.SetReturnKeyType(input, TextInputReturnKeyType.Return);
        TextInputOptions.SetAutoCapitalization(input, false);
        TextInputOptions.SetShowSuggestions(input, false);
    }

    /// <summary>키보드를 연다 — 이미 포커스가 있으면(키보드만 닫힌 상태) 포커스를 옮겼다가 돌려 다시 연다.</summary>
    public static void Show(TextBox input, InputElement parking)
    {
        if (!input.IsFocused)
        {
            input.Focus();
            return;
        }

        var focusable = parking.Focusable;
        parking.Focusable = true;
        parking.Focus();
        Dispatcher.UIThread.Post(() =>
        {
            parking.Focusable = focusable;
            input.Focus();
        }, DispatcherPriority.Input);
    }
}
