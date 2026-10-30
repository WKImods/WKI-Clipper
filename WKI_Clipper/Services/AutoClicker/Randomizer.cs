using System;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// Seedable random source of the engine: uniform draws, a Box-Muller normal and the randomized batch duration. Not thread-safe (the
/// engine thread owns one) and allocation-free after construction.
/// </summary>
public sealed class Randomizer
{
    /// <summary>A drawn duration never falls below this.</summary>
    public const double MinDurationMs = 1;

    private readonly Random _random;
    private double _spare;
    private bool _hasSpare;

    public Randomizer() : this(Environment.TickCount ^ Guid.NewGuid().GetHashCode()) { }

    public Randomizer(int seed) => _random = new Random(seed);

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => _random.NextDouble();

    /// <summary>Uniform in [<paramref name="min"/>, <paramref name="max"/>).</summary>
    public double NextRange(double min, double max) => min + (max - min) * _random.NextDouble();

    /// <summary>true with the given probability (0 never, 1 always).</summary>
    public bool Chance(double probability) => _random.NextDouble() < probability;

    /// <summary>Standard normal (mean 0, sigma 1) by the Box-Muller transform; each pair of uniforms yields two values.</summary>
    public double NextGaussian()
    {
        if (_hasSpare)
        {
            _hasSpare = false;
            return _spare;
        }
        double u1 = 1.0 - _random.NextDouble();   // (0, 1], so the log is finite
        double u2 = _random.NextDouble();
        double radius = Math.Sqrt(-2.0 * Math.Log(u1));
        double angle = 2.0 * Math.PI * u2;
        _spare = radius * Math.Sin(angle);
        _hasSpare = true;
        return radius * Math.Cos(angle);
    }

    /// <summary>
    /// One batch duration: log-normal with mean <paramref name="baseMs"/> and sigma base x percent / 100, so it is always positive and
    /// its mean is exactly the base at every percent (a floored normal would lift the mean once sigma nears the base). The 1 ms floor
    /// is only a safety net. The engine adds the result to an absolute deadline, so the long-run rate stays the set one. Percent 0
    /// returns the base without drawing.
    /// </summary>
    public double NextDuration(double baseMs, double percent)
    {
        double cv = percent / 100.0;
        if (!(cv > 0) || !(baseMs > 0)) return baseMs;
        double sigmaL = Math.Sqrt(Math.Log(1.0 + cv * cv));
        double mu = Math.Log(baseMs) - 0.5 * sigmaL * sigmaL;   // E[exp(mu + sigmaL * z)] = baseMs
        return Math.Max(MinDurationMs, Math.Exp(mu + sigmaL * NextGaussian()));
    }
}
