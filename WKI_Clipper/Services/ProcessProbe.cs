using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WKI_Clipper.Services;

/// <summary>Read-only view of the running processes, so the AutoClicker guard is tested against a fake.</summary>
public interface IProcessProbe
{
    /// <summary>The names (no ".exe") of every running process; empty when they cannot be listed.</summary>
    IReadOnlyCollection<string> RunningNames();
}

/// <summary>The real process list behind <see cref="IProcessProbe"/>. Names only: no handle is ever opened for reading or writing another process.</summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    public IReadOnlyCollection<string> RunningNames()
    {
        try
        {
            var all = Process.GetProcesses();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in all)
            {
                try { names.Add(p.ProcessName); }
                catch (Exception) { /* exited while listing */ }
                finally { p.Dispose(); }
            }
            return names;
        }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
