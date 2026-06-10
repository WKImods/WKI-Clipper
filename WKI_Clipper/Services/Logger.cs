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

    public static string Path
    {
        get
        {
            if (_path is null)
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WKI_Clipper");
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "wki_clipper.log");
            }
            return _path;
        }
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
            if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000)
            {
                File.Move(Path, Path + ".old", overwrite: true);
            }
        }
        catch { }
    }
}
