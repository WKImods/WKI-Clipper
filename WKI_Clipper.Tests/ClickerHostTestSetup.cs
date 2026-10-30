using System.IO;
using System.Runtime.CompilerServices;
using WKI_Clipper.ClickerHost;

namespace WKI_Clipper.Tests;

/// <summary>Keeps the host-log lines the tests produce out of the real wki_clicker_host.log.</summary>
internal static class ClickerHostTestSetup
{
    [ModuleInitializer]
    internal static void RedirectHostLog() => HostLog.SetPath(Path.Combine(Path.GetTempPath(), "wki_clicker_host_tests.log"));
}
