using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ProxmoxClient.UI.Views.Console;

/// <summary>
///     원격 화면을 256×256 타일 비트맵으로 나눠 GPU 에 올린다 — 바뀐 영역이 걸친 타일만 다시 올라간다.
///     비트맵 하나로 두면 커서만 움직여도 화면 전체(1080p ≈ 8MB)를 프레임마다 다시 올려야 해서, 모바일 GPU·메모리 대역폭에서
///     지연과 배터리 소모가 크다. 모든 OS 공통 핫패스.
/// </summary>
internal sealed class TiledFramebuffer : IDisposable
{
    public const int TileSize = 256;

    private readonly WriteableBitmap[] _tiles;
    private readonly int _columns;
    private readonly int _rows;

    public TiledFramebuffer(int width, int height)
    {
        Width = width;
        Height = height;
        _columns = (width + TileSize - 1) / TileSize;
        _rows = (height + TileSize - 1) / TileSize;
        _tiles = new WriteableBitmap[_columns * _rows];
        for (var r = 0; r < _rows; r++)
        for (var c = 0; c < _columns; c++)
        {
            var w = Math.Min(TileSize, width - c * TileSize);
            var h = Math.Min(TileSize, height - r * TileSize);
            _tiles[r * _columns + c] = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>바뀐 영역(dirty)이 걸친 타일만 원본(BGRX, 행 간격 Width*4)에서 복사한다.</summary>
    public void Update(byte[] source, PixelRect dirty)
    {
        dirty = dirty.Intersect(new PixelRect(0, 0, Width, Height));
        if (dirty.Width <= 0 || dirty.Height <= 0) return;

        var c0 = dirty.X / TileSize;
        var c1 = (dirty.Right - 1) / TileSize;
        var r0 = dirty.Y / TileSize;
        var r1 = (dirty.Bottom - 1) / TileSize;
        var count = (c1 - c0 + 1) * (r1 - r0 + 1);

        void CopyTile(int index)
        {
            var c = c0 + index % (c1 - c0 + 1);
            var r = r0 + index / (c1 - c0 + 1);
            var tileRect = new PixelRect(c * TileSize, r * TileSize, Math.Min(TileSize, Width - c * TileSize),
                Math.Min(TileSize, Height - r * TileSize));
            var part = tileRect.Intersect(dirty);
            if (part.Width <= 0 || part.Height <= 0) return;

            using var locked = _tiles[r * _columns + c].Lock();
            var rowBytes = part.Width * 4;
            for (var y = part.Y; y < part.Bottom; y++)
            {
                var src = (y * Width + part.X) * 4;
                if (src + rowBytes > source.Length) break;

                var dst = locked.Address + (y - tileRect.Y) * locked.RowBytes + (part.X - tileRect.X) * 4;
                System.Runtime.InteropServices.Marshal.Copy(source, src, dst, rowBytes);
            }
        }

        // 화면 전체가 바뀐 프레임(해상도 변경·스크롤 등)은 여러 코어로 나눠 복사
        if (count >= 8 && Environment.ProcessorCount > 1)
            Parallel.For(0, count, CopyTile);
        else
            for (var i = 0; i < count; i++)
                CopyTile(i);
    }

    /// <summary>
    ///     보기 좌표 <paramref name="offset" />·배율 <paramref name="scale" /> 로 그린다. 타일 가장자리는 화면 픽셀에 맞춰 반올림해
    ///     확대·축소해도 타일 사이에 틈이 보이지 않게 한다.
    /// </summary>
    public void Render(DrawingContext context, Point offset, double scale, Rect clip)
    {
        for (var r = 0; r < _rows; r++)
        {
            var y0 = Math.Round(offset.Y + r * TileSize * scale);
            var y1 = Math.Round(offset.Y + Math.Min(Height, (r + 1) * TileSize) * scale);
            if (y1 < clip.Top || y0 > clip.Bottom) continue;

            for (var c = 0; c < _columns; c++)
            {
                var x0 = Math.Round(offset.X + c * TileSize * scale);
                var x1 = Math.Round(offset.X + Math.Min(Width, (c + 1) * TileSize) * scale);
                if (x1 < clip.Left || x0 > clip.Right) continue;

                var tile = _tiles[r * _columns + c];
                context.DrawImage(tile, new Rect(tile.Size), new Rect(x0, y0, x1 - x0, y1 - y0));
            }
        }
    }

    public void Dispose()
    {
        foreach (var tile in _tiles) tile.Dispose();
    }
}
