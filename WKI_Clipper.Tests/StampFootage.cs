using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Test footage whose every frame carries its own number: top left, three 64 px blocks with luma 20 + 3 x digit for
/// N mod 64, (N / 64) mod 64 and N / 4096. The number is readable from any decoded picture, so "frame-accurate" is
/// measured, not guessed. Generated with the real ffmpeg; tests that use it return early without one.
/// </summary>
internal static class StampFootage
{
    public const int Frames = 360;     // 12 s at 30 fps, a keyframe every 30 frames

    private const string Overlay =
        "-f lavfi -i \"testsrc2=size=320x180:rate=30\" " +
        "-f lavfi -i \"color=c=black:s=192x64:r=30,format=yuv420p," +
        "geq=lum='if(lt(X,64),20+3*mod(N,64),if(lt(X,128),20+3*mod(floor(N/64),64),20+3*mod(floor(N/4096),64)))':cb=128:cr=128\" ";

    /// <summary>The plain source: H.264, no B-frames, one AAC track (or two).</summary>
    public static void Make(string ffmpeg, string output, bool bframes = false, int audioTracks = 1)
    {
        var args = "-hide_banner -loglevel error -y " + Overlay
            + "-f lavfi -i \"sine=frequency=440:sample_rate=48000\" "
            + (audioTracks > 1 ? "-f lavfi -i \"sine=frequency=660:sample_rate=48000\" " : "")
            + "-t 12 -filter_complex \"[0:v][1:v]overlay=0:0:shortest=1[v]\" -map \"[v]\" -map 2:a "
            + (audioTracks > 1 ? "-map 3:a " : "")
            + $"-c:v libx264 -preset ultrafast -crf 12 -g 30 -keyint_min 30 -sc_threshold 0 -bf {(bframes ? 3 : 0)} -pix_fmt yuv420p -c:a aac \"{output}\"";
        var (code, err) = FfRun.Run(ffmpeg, args);
        Assert.True(code == 0, "stamp footage failed: " + err);
    }

    /// <summary>The video starts 0.5 s after the audio (an empty edit in the mp4).</summary>
    public static void MakeVideoOffset(string ffmpeg, string source, string output)
    {
        var (code, err) = FfRun.Run(ffmpeg,
            $"-hide_banner -loglevel error -y -itsoffset 0.5 -i \"{source}\" -i \"{source}\" -map 0:v -map 1:a -c copy \"{output}\"");
        Assert.True(code == 0, "offset footage failed: " + err);
    }

    /// <summary>Two chapters (0-6 s, 6-12 s); the mp4 muxer writes them as a QuickTime chapter text track.</summary>
    public static void MakeChapters(string ffmpeg, string source, string output, string metadataFile)
    {
        File.WriteAllText(metadataFile,
            ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=6000\ntitle=Eins\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=6000\nEND=12000\ntitle=Zwei\n");
        var (code, err) = FfRun.Run(ffmpeg,
            $"-hide_banner -loglevel error -y -i \"{source}\" -f ffmetadata -i \"{metadataFile}\" -map 0 -map_chapters 1 -c copy \"{output}\"");
        Assert.True(code == 0, "chapter footage failed: " + err);
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Frame numbers of the first <paramref name="count"/> pictures (all when null) of the input, in decode order.</summary>
    public static List<int> ReadStamps(string ffmpeg, string file, int? count = 1, string inputArgs = "")
    {
        var args = $"-hide_banner -loglevel error {inputArgs} -i \"{file}\" " + (count is { } n ? $"-frames:v {n} " : "")
                   + "-fps_mode passthrough -an -vf crop=192:64:0:0,extractplanes=y -f rawvideo -";
        var bytes = RunBinary(ffmpeg, args);
        const int frameBytes = 192 * 64;
        var stamps = new List<int>();
        for (int at = 0; at + frameBytes <= bytes.Length; at += frameBytes)
        {
            int D(int x) => (int)Math.Round((bytes[at + 32 * 192 + x] - 20) / 3.0);
            stamps.Add(D(32) + 64 * D(96) + 4096 * D(160));
        }
        return stamps;
    }

    public static int FirstStamp(string ffmpeg, string file) => ReadStamps(ffmpeg, file, 1).Single();

    /// <summary>The last picture of the file (the stamps of its final 0.3 s, last one).</summary>
    public static int LastStamp(string ffmpeg, string file) => ReadStamps(ffmpeg, file, null, "-sseof -0.3").Last();

    /// <summary>Frame number of a decoded BMP (bottom-up BGR) of the stamp footage, e.g. from the frame-grab fallback.</summary>
    public static int StampOfBmp(byte[] bmp)
    {
        int dataOffset = BitConverter.ToInt32(bmp, 10);
        int width = BitConverter.ToInt32(bmp, 18);
        int height = BitConverter.ToInt32(bmp, 22);
        int bits = BitConverter.ToInt16(bmp, 28);
        Assert.Equal(24, bits);
        int stride = (width * 3 + 3) / 4 * 4;
        int y = Math.Abs(height) - 1 - 32;                  // rows are stored bottom-up
        int D(int x)
        {
            int r = bmp[dataOffset + y * stride + x * 3 + 2];
            double luma = r * 219.0 / 255 + 16;
            return (int)Math.Round((luma - 20) / 3.0);
        }
        return D(32) + 64 * D(96) + 4096 * D(160);
    }

    /// <summary>pts (seconds) of the first packet of a stream, via framecrc.</summary>
    public static double FirstPts(string ffmpeg, string file, string map)
    {
        var (_, crc) = Framecrc(ffmpeg, file, map, "");
        return crc.First().Pts;
    }

    /// <summary>(pts in seconds, size) of every packet — or only the keyframes with <c>-discard nokey</c>.</summary>
    public static (int Code, List<(double Pts, long Size)> Packets) Framecrc(string ffmpeg, string file, string map, string inputArgs)
    {
        var (code, text) = RunText(ffmpeg, $"-hide_banner -loglevel error {inputArgs} -i \"{file}\" -map {map} -c copy -f framecrc -");
        var tb = Regex.Match(text, @"#tb 0: (\d+)/(\d+)");
        Assert.True(tb.Success, "no timebase in framecrc output: " + text);
        double scale = double.Parse(tb.Groups[1].Value, CultureInfo.InvariantCulture) / double.Parse(tb.Groups[2].Value, CultureInfo.InvariantCulture);
        var packets = new List<(double, long)>();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Split(',').Select(s => s.Trim()).ToArray();
            if (f.Length < 5) continue;
            packets.Add((long.Parse(f[2], CultureInfo.InvariantCulture) * scale, long.Parse(f[4], CultureInfo.InvariantCulture)));
        }
        return (code, packets);
    }

    private static (int Code, string Text) RunText(string ffmpeg, string args)
    {
        var bytes = RunBinary(ffmpeg, args, out int code);
        return (code, Encoding.UTF8.GetString(bytes));
    }

    public static byte[] RunBinary(string ffmpeg, string args) => RunBinary(ffmpeg, args, out _);

    private static byte[] RunBinary(string ffmpeg, string args, out int exitCode)
    {
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        err.Wait();
        exitCode = p.ExitCode;
        return ms.ToArray();
    }

    /// <summary>Stderr of <c>ffmpeg -i file</c>: streams, chapters, duration.</summary>
    public static string Describe(string ffmpeg, string file) => FfRun.Run(ffmpeg, $"-hide_banner -i \"{file}\"").StdErr;
}
