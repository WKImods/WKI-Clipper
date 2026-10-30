namespace WKI_Clipper.Services.AutoClicker;

/// <summary>A position in virtual-screen (physical) pixels. Negative values are normal: monitors left of or above the primary one.</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>
/// A rectangle in virtual-screen (physical) pixels, like a monitor's RECT: the left/top pixel is inside, the pixel at
/// <see cref="Right"/> / <see cref="Bottom"/> is not.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public bool Contains(PixelPoint p) => Contains(p.X, p.Y);
}
