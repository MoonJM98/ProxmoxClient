using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProxmoxClient.App.Interop;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Views;

/// <summary>
///     콘솔 위 커서 — 설정(<see cref="ConsoleSettings.LocalCursorMode" />)에 따라
///     "둘 다"(PC 화살표 + 게스트 커서) 또는 PC 커서 하나만 보인다.
///     PC 커서 하나만일 때 게스트가 보낸 커서 모양(VirtIO-GPU·QXL·VMware 화면 등)은 진짜 Windows 커서로 만들어
///     운영체제가 지연 없이 그린다. 모양을 보내지 않는 게스트(표준 VGA 등)는 게스트가 화면에 직접 그린 커서가 보인다.
///     화면 배율·DPI 가 바뀌면 크기를 맞춰 다시 만든다.
/// </summary>
public partial class ConsoleWindow
{
    private const int DotSize = 5;

    /// <summary>게스트가 보낸 마지막 커서 모양(아직 없으면 null — 대체 커서 사용).</summary>
    private RfbCursor? _cursor;

    private SafeHandle? _cursorHandle;
    private (RfbCursor? Shape, double Scale, ConsoleSettings.LocalCursorMode Mode) _cursorKey;

    private void ApplyCursorShape(RfbCursor cursor)
    {
        _cursor = cursor;
        RefreshCursor();
    }

    private void ResetCursor()
    {
        _cursor = null;
        RefreshCursor();
    }

    /// <summary>화면 배율 — 맞춤 모드면 축소 비율, 아니면 1.</summary>
    private double DisplayScale()
    {
        if (!_fitMode || _fbWidth == 0 || _fbHeight == 0) return 1;
        return Math.Min(ScreenImage.ActualWidth / _fbWidth, ScreenImage.ActualHeight / _fbHeight);
    }

    /// <summary>지금 상태(모양·배율·DPI·설정)에 맞는 커서를 화면에 건다 — 같으면 다시 만들지 않는다.</summary>
    private void RefreshCursor()
    {
        var scale = DisplayScale() * VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0 || double.IsNaN(scale)) scale = 1;
        var key = (_cursor, Math.Round(scale, 3), _settings.LocalCursor);
        if (key == _cursorKey && ScreenImage.Cursor is not null) return;
        _cursorKey = key;

        NativeCursor.Created? created = null;
        Cursor cursor;
        UpdateOverlayImage();
        if (_settings.LocalCursor == ConsoleSettings.LocalCursorMode.Both)
        {
            cursor = Cursors.Arrow; // 게스트 커서는 겹쳐 그린 모양(보낸 경우) 또는 게스트 화면의 커서
        }
        else if (_cursor is { } shape)
        {
            // 게스트가 커서를 숨겼으면(0×0) 숨김, 아니면 그 모양 그대로
            created = shape.IsEmpty
                ? null
                : NativeCursor.Create(shape.Pixels, shape.Width, shape.Height, shape.HotX, shape.HotY, scale);
            cursor = created?.Cursor ?? Cursors.None;
        }
        else
        {
            cursor = _settings.LocalCursor switch
            {
                ConsoleSettings.LocalCursorMode.Arrow => Cursors.Arrow,
                ConsoleSettings.LocalCursorMode.Dot => (created = DotCursor(scale))?.Cursor ?? Cursors.Arrow,
                _ => Cursors.None
            };
        }

        ScreenImage.Cursor = cursor;
        // 새 커서를 건 뒤에 이전 핸들을 해제한다(쓰는 중인 커서를 먼저 지우지 않게)
        _cursorHandle?.Dispose();
        _cursorHandle = created?.Handle;
    }

    /// <summary>"둘 다 보기" 에서 겹쳐 그릴 게스트 커서 그림 — 모양이 없거나(0×0 포함) 다른 모드면 숨긴다.</summary>
    private void UpdateOverlayImage()
    {
        if (_settings.LocalCursor != ConsoleSettings.LocalCursorMode.Both || _cursor is not { IsEmpty: false } shape)
        {
            CursorImage.Source = null;
            CursorImage.Visibility = Visibility.Collapsed;
            return;
        }

        var bitmap = new WriteableBitmap(shape.Width, shape.Height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, shape.Width, shape.Height), shape.Pixels, shape.Width * 4, 0);
        bitmap.Freeze();
        CursorImage.Source = bitmap;
        MoveCursorOverlay(Mouse.GetPosition(ScreenHost));
    }

    /// <summary>겹쳐 그린 게스트 커서를 마우스 위치(<c>ScreenHost</c> 기준)로 — 핫스팟을 화면 배율에 맞춰 뺀다.</summary>
    private void MoveCursorOverlay(Point position)
    {
        var inside = ScreenImage.IsMouseOver || ScreenImage.IsMouseCaptured;
        if (CursorImage.Source is null || _cursor is not { IsEmpty: false } shape || !inside)
        {
            CursorImage.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = DisplayScale();
        CursorImage.Width = shape.Width * scale;
        CursorImage.Height = shape.Height * scale;
        CursorTransform.X = position.X - shape.HotX * scale;
        CursorTransform.Y = position.Y - shape.HotY * scale;
        CursorImage.Visibility = Visibility.Visible;
    }

    /// <summary>작은 흰 점(검은 테두리) — 어느 배경에서도 보이는 최소 커서(noVNC 의 점 커서와 같은 용도).</summary>
    private static NativeCursor.Created? DotCursor(double scale)
    {
        var pixels = new byte[DotSize * DotSize * 4];
        for (var y = 0; y < DotSize; y++)
        for (var x = 0; x < DotSize; x++)
        {
            var edge = x == 0 || y == 0 || x == DotSize - 1 || y == DotSize - 1;
            var corner = (x == 0 || x == DotSize - 1) && (y == 0 || y == DotSize - 1);
            if (corner) continue;
            var i = (y * DotSize + x) * 4;
            var v = edge ? (byte)0 : (byte)0xFF;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
            pixels[i + 3] = 0xFF;
        }

        // 점은 게스트 픽셀이 아니므로 화면 배율은 빼고 DPI 만 반영
        return NativeCursor.Create(pixels, DotSize, DotSize, DotSize / 2, DotSize / 2, Math.Max(1, scale));
    }
}
