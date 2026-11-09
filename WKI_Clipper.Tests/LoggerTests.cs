using System;
using System.IO;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// The clipper log during a test run: <see cref="TestLogSetup"/> sends it to %TEMP%, so app code under test never writes into the
/// user's real %LOCALAPPDATA%\WKI_Clipper\wki_clipper.log. The real log is only ever read here, never created or written.
/// </summary>
public sealed class LoggerTests
{
    private static readonly string TestLog = Path.Combine(Path.GetTempPath(), "wki_clipper_tests.log");

    /// <summary>Computed independently of <see cref="Logger"/>, the way the app finds its log.</summary>
    private static readonly string RealLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKI_Clipper", "wki_clipper.log");

    [Fact]
    public void The_default_path_is_the_apps_log_in_local_app_data()
        => Assert.Equal(RealLog, Logger.DefaultPath, ignoreCase: true);

    [Fact]
    public void During_a_test_run_the_log_is_the_test_log_in_temp_and_not_the_real_one()
    {
        string current = Path.GetFullPath(Logger.Path);

        Assert.NotEqual(Path.GetFullPath(Logger.DefaultPath), current, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(Path.GetFullPath(RealLog), current, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(TestLog), current, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_line_logged_by_a_test_lands_in_the_test_log_and_never_in_the_real_one()
    {
        string marker = "logger-isolation-marker-" + Guid.NewGuid().ToString("N");

        Logger.Info(marker);

        Assert.Contains(marker, ReadShared(TestLog));
        if (File.Exists(RealLog))   // read-only look; a missing real log stays missing
            Assert.DoesNotContain(marker, ReadShared(RealLog));
    }

    /// <summary>Reads a file another writer (the running app, parallel tests) may have open, without creating it.</summary>
    internal static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }
}

/// <summary>Tests that move the global clipper log. Never in parallel with anything else: other tests' lines would follow the move.</summary>
[CollectionDefinition("ClipperLog", DisableParallelization = true)]
public sealed class ClipperLogCollection { }

[Collection("ClipperLog")]
public sealed class LoggerSetPathTests
{
    private static void TryDelete(string path)
    {
        try { File.Delete(path); File.Delete(path + ".old"); } catch { }
    }

    [Fact]
    public void SetPath_moves_the_log_keeps_the_line_format_and_rotate_follows_it()
    {
        var path = Path.Combine(Path.GetTempPath(), "wki-clipperlog-" + Guid.NewGuid().ToString("N") + ".log");
        var previous = Logger.Path;
        Logger.SetPath(path);
        try
        {
            Assert.Equal(path, Logger.Path);

            // A background task an earlier test left running may log into the moved file too: only our own lines count.
            string tag = "unit-test-" + Guid.NewGuid().ToString("N");
            Logger.Info(tag + " info");
            Logger.Warn(tag + " warn");
            Logger.Error(tag + " error");
            Logger.Error(tag + " failure", new InvalidOperationException("boom"));

            var lines = Array.FindAll(File.ReadAllLines(path), l => l.Contains(tag, StringComparison.Ordinal));
            Assert.Equal(4, lines.Length);
            var stamp = @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ";
            Assert.Matches(stamp + @"\[INFO\] " + tag + " info$", lines[0]);
            Assert.Matches(stamp + @"\[WARN\] " + tag + " warn$", lines[1]);
            Assert.Matches(stamp + @"\[ERR \] " + tag + " error$", lines[2]);
            Assert.Matches(stamp + @"\[ERR \] " + tag + @" failure \| System\.InvalidOperationException: boom$", lines[3]);

            File.WriteAllBytes(path, new byte[1_000_010]);
            Logger.Rotate();
            Assert.True(File.Exists(path + ".old"));
            Assert.Equal(1_000_010, new FileInfo(path + ".old").Length);
            Assert.False(File.Exists(path));

            File.WriteAllBytes(path, new byte[1_000_000]);   // at the cap, not over it: stays
            Logger.Rotate();
            Assert.True(File.Exists(path));
            Assert.Equal(1_000_010, new FileInfo(path + ".old").Length);
        }
        finally
        {
            Logger.SetPath(previous);
            TryDelete(path);
        }

        Assert.Equal(previous, Logger.Path);
    }
}
