using System;
using System.IO;

namespace WKI_Clipper.Services;

/// <summary>
/// Tiny append-only file logger. Helps diagnose startup issues when there is
/// no UI yet to surface errors.
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static string? _path;

    /// <summary>Where the app logs unless redirected: %LOCALAPPDATA%\WKI_Clipper\wki_clipper.log. Computing it creates nothing.</summary>
    internal static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKI_Clipper", "wki_clipper.log");

    public static string Path
    {
        get
        {
            lock (_lock)
            {
                if (_path is null)
                {
                    var path = DefaultPath;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                    _path = path;
                }
                return _path;
            }
        }
    }

    /// <summary>Test seam: log somewhere else (null = back to the default).</summary>
    internal static void SetPath(string? path)
    {
        lock (_lock) _path = path;
    }

    public static void Info(string msg)  => Write("INFO", msg);
    public static void Warn(string msg)  => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);
    public static void Error(string msg, Exception ex) => Write("ERR ", msg + " | " + ex);

    private static void Write(string level, string msg)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* never crash on log failure */ }
    }

    public static void Rotate()
    {
        try
        {
            lock (_lock)
            {
                var path = Path;
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }
            }
        }
        catch { }
    }
}
