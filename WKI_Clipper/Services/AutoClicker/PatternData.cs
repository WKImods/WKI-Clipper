using System;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// One recorded track of a mouse pattern in 1 ms bins (mouse-patterns design 4.3): bin i moved by (<see cref="Dx"/>[i], <see cref="Dy"/>[i])
/// at <see cref="AtMs"/>[i] milliseconds after the start. <see cref="AtMs"/> is strictly increasing, its first value is &gt;= 0, and bins
/// without motion are left out. <see cref="PatternCodec"/> enforces this when it reads a file.
/// </summary>
public sealed record PatternTrack(int[] AtMs, int[] Dx, int[] Dy)
{
    public static PatternTrack Empty { get; } = new(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());

    public int Count => AtMs.Length;

    /// <summary>The summed motion of the whole track, in 64 bit (thousands of bins near int.MaxValue cannot overflow it).</summary>
    public (long X, long Y) Total
    {
        get
        {
            long x = 0, y = 0;
            for (int i = 0; i < Dx.Length; i++) x += Dx[i];
            for (int i = 0; i < Dy.Length; i++) y += Dy[i];
            return (x, y);
        }
    }
}

/// <summary>
/// The samples of one pattern: the raw sensor counts (what games with a hidden pointer read), the on-screen pointer offsets and the
/// length of the motion. Stored per pattern by <see cref="MousePatternStore"/>, never in the settings.
/// </summary>
public sealed record PatternData(PatternTrack Raw, PatternTrack Pointer, double DurationMs);
