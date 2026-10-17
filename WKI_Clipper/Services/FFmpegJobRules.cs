using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace WKI_Clipper.Services;

/// <summary>
/// The decisions of the ffmpeg job runner that need no process, disk or clock — kept
/// pure so every edge case is unit-tested instead of discovered with a half-written clip.
/// </summary>
public static class FFmpegJobRules
{
    public const long DiskReserveBytes = 500L * 1024 * 1024;

    /// <summary>Output is only complete once ffmpeg finished; until then it lives under this suffix.</summary>
    public const string PartSuffix = ".part";

    /// <summary>Estimate x 1.1 (estimates are rough) plus a 500 MB floor for the rest of the system.</summary>
    public static long RequiredFreeBytes(long estimatedOutputBytes)
    {
        var estimate = Math.Max(0L, estimatedOutputBytes);
        // decimal: 1.1 is not exact in binary floating point and would round a whole-MB estimate up by one byte.
        return (long)Math.Ceiling(estimate * 1.1m) + DiskReserveBytes;
    }

    public static bool HasEnoughDiskSpace(long freeBytes, long estimatedOutputBytes)
        => freeBytes > RequiredFreeBytes(estimatedOutputBytes);

    /// <summary>"a.mp4" taken → "a (2).mp4" → "a (3).mp4"… The first free name wins.</summary>
    public static string UniquePath(string desiredPath, Func<string, bool> exists)
    {
        if (!exists(desiredPath)) return desiredPath;

        var dir = Path.GetDirectoryName(desiredPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);
        for (int n = 2; n < 10_000; n++)
        {
            var candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>
    /// ffmpeg's -threads is a per-file codec option, so it has to sit in front of the OUTPUT
    /// to throttle the encoder. The output is found by its (quoted) part path; if the caller
    /// did not pass it that way, the option goes to the front (throttles the decoder instead).
    /// </summary>
    public static string ApplyThreadCap(string arguments, string partPath, int? cap)
    {
        if (cap is not > 0) return arguments;
        var option = "-threads " + cap.Value.ToString(CultureInfo.InvariantCulture);

        int at = arguments.LastIndexOf("\"" + partPath + "\"", StringComparison.OrdinalIgnoreCase);
        if (at < 0) at = arguments.LastIndexOf(partPath, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? option + " " + arguments : arguments.Insert(at, option + " ");
    }

    /// <summary>
    /// ffmpeg rejects <c>-y</c> together with <c>-n</c> ("Error opening output files: Invalid
    /// argument"), so the runner owns the overwrite policy and removes both from the caller's
    /// arguments. Only whole unquoted tokens count: "-yadif" or a quoted path are left alone.
    /// </summary>
    public static string StripOverwriteFlags(string arguments)
    {
        var result = new StringBuilder(arguments.Length);
        int i = 0;
        while (i < arguments.Length)
        {
            if (char.IsWhiteSpace(arguments[i])) { result.Append(arguments[i++]); continue; }

            int start = i;
            bool quoted = false;
            while (i < arguments.Length && (quoted || !char.IsWhiteSpace(arguments[i])))
            {
                if (arguments[i] == '"') quoted = !quoted;
                i++;
            }

            var token = arguments[start..i];
            if (token is "-y" or "-n")
            {
                while (i < arguments.Length && char.IsWhiteSpace(arguments[i])) i++;   // and the gap behind it
                continue;
            }
            result.Append(token);
        }
        return result.ToString().TrimEnd();
    }

    /// <summary>
    /// A leftover from a crash, not a job that is writing right now: a ".part" file that has not
    /// been touched for a while (a live encode updates it constantly). Whether the file is ours to
    /// delete is decided elsewhere — only exact paths the runner recorded are ever considered.
    /// </summary>
    public static bool IsStalePart(string fileName, DateTime lastWriteUtc, DateTime nowUtc, TimeSpan minAge)
        => fileName.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase)
           && nowUtc - lastWriteUtc > minAge;
}

/// <summary>Reads ffmpeg's machine-readable <c>-progress</c> stream (key=value lines).</summary>
public static class FFmpegProgressParser
{
    /// <summary>
    /// Encoded position from an <c>out_time_us=</c> or <c>out_time=</c> line. False for every
    /// other line and for the placeholder values ffmpeg prints before the first frame
    /// ("N/A", negative numbers).
    /// </summary>
    public static bool TryParseOutTime(string line, out TimeSpan position)
    {
        position = default;
        if (string.IsNullOrEmpty(line)) return false;

        if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
        {
            if (!long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)
                || us < 0) return false;
            position = TimeSpan.FromTicks(us * 10);
            return true;
        }

        if (line.StartsWith("out_time=", StringComparison.Ordinal))
        {
            var value = line.AsSpan("out_time=".Length);
            if (value.Length == 0 || !char.IsAsciiDigit(value[0])) return false;
            return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out position);
        }

        return false;
    }

    public static bool IsEnd(string line) => line.Trim() == "progress=end";

    /// <summary>0..1; 0 when the expected length is unknown (the bar then stays indeterminate).</summary>
    public static double Fraction(TimeSpan position, TimeSpan expected)
    {
        if (expected <= TimeSpan.Zero) return 0;
        return Math.Clamp(position.TotalSeconds / expected.TotalSeconds, 0, 1);
    }
}
