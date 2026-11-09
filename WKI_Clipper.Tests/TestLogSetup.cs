using System.IO;
using System.Runtime.CompilerServices;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Services;

namespace WKI_Clipper.Tests;

/// <summary>
/// Keeps the log lines the tests produce out of the user's real logs in %LOCALAPPDATA%\WKI_Clipper: app code under test logs
/// through the static <see cref="Logger"/> (wki_clipper.log) and <see cref="HostLog"/> (wki_clicker_host.log), and an installed
/// clipper may be running and writing the same files. Runs before any test code, so no test line ever reaches the defaults.
/// Both test logs live in %TEMP% and are size-capped like the real ones: a file over 1 MB moves to .old when the tests start.
/// Only this test process is redirected: a child process running clipper code would log to the defaults again (none does today).
/// </summary>
internal static class TestLogSetup
{
    public static readonly string ClipperLog = Path.Combine(Path.GetTempPath(), "wki_clipper_tests.log");
    public static readonly string ClickerHostLog = Path.Combine(Path.GetTempPath(), "wki_clicker_host_tests.log");

    [ModuleInitializer]
    internal static void RedirectLogs()
    {
        Logger.SetPath(ClipperLog);
        Logger.Rotate();
        HostLog.SetPath(ClickerHostLog);
        HostLog.Rotate();
    }
}
