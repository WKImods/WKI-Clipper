using System;
using System.IO;

namespace WKI_Clipper.ClickerHost;

/// <summary>
/// The helper process's own append-only log (<c>wki_clicker_host.log</c> next to the clipper's log). Size-capped like the main log:
/// a file over 1 MB moves to <c>.old</c> when the host starts and again whenever it grows past the cap while running. Warnings and
/// errors are also handed to <see cref="Forward"/> so the clipper's log gets them too.
/// </summary>
internal static class HostLog
{
    public const long MaxBytes = 1_000_000;
    private const int SizeCheckEvery = 200;

    private static readonly object Lock = new();
    private static string? _path;
    private static int _writesSinceCheck;

    /// <summary>Warning/error lines as (level "warn"/"error", text); set by the server so the clipper can log them. Called on the writing thread.</summary>
    public static Action<string, string>? Forward { get; set; }

    public static string Path
    {
        get
        {
            if (_path is null)
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKI_Clipper");
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "wki_clicker_host.log");
            }
            return _path;
        }
    }

    /// <summary>Test seam: log somewhere else (null = back to the default).</summary>
    internal static void SetPath(string? path)
    {
        lock (Lock)
        {
            _path = path;
            _writesSinceCheck = 0;
        }
    }

    public static void Info(string msg) => Write("INFO", msg);

    public static void Warn(string msg)
    {
        Write("WARN", msg);
        Forward?.Invoke("warn", msg);
    }

    public static void Error(string msg)
    {
        Write("ERR ", msg);
        Forward?.Invoke("error", msg);
    }

    public static void Error(string msg, Exception ex) => Error(msg + " | " + ex);

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Lock)
            {
                if (++_writesSinceCheck >= SizeCheckEvery)
                {
                    _writesSinceCheck = 0;
                    RotateLocked();
                }
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* never crash on log failure */ }
    }

    /// <summary>Moves a log over <see cref="MaxBytes"/> to <c>.old</c> (replacing the previous one).</summary>
    public static void Rotate()
    {
        lock (Lock) RotateLocked();
    }

    private static void RotateLocked()
    {
        try
        {
            if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                File.Move(Path, Path + ".old", overwrite: true);
        }
        catch { }
    }
}
