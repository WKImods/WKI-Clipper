using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.ClickerHost;
using WKI_Clipper.Models;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>How the clipper starts its own helper process (the same exe in host mode).</summary>
public readonly record struct HostCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// <c>WKI_Clipper.exe --clicker-host &lt;pipe&gt; &lt;pid&gt;</c>. Under <c>dotnet WKI_Clipper.dll</c> (a developer start) the process is
    /// dotnet.exe, so the assembly goes first, or the helper would start as a bare dotnet host.
    /// </summary>
    public static HostCommand Build(string processPath, string? entryAssemblyPath, string pipeName, int parentPid)
    {
        var args = new List<string>();
        bool viaDotnet = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        if (viaDotnet && !string.IsNullOrEmpty(entryAssemblyPath)) args.Add(entryAssemblyPath);
        args.Add(HostMain.Switch);
        args.Add(pipeName);
        args.Add(parentPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new HostCommand(processPath, args);
    }
}

/// <summary>
/// What a short-lived release process lets go of after the clipper killed a helper that may have held input down (keyboard hold,
/// duty hold): <c>WKI_Clipper.exe --clicker-release &lt;Left|Right|Middle|-&gt; &lt;vk&gt; &lt;0|1&gt;</c>. It is made from the settings at
/// kill time; the main app itself never injects input.
/// </summary>
public readonly record struct ClickerRelease(ClickerMouseButton? Button, int Vk, bool Shift)
{
    public const string Switch = "--clicker-release";

    /// <summary>The output of <paramref name="settings"/>: the mouse button, or the key plus Shift (upper case and Caps Lock both use Shift).</summary>
    public static ClickerRelease For(ClickerSettings settings)
        => settings.InputType == ClickerInput.Keyboard
            ? new ClickerRelease(null, settings.KeyboardVk is > 0 and <= 0xFE ? settings.KeyboardVk : 0, Shift: true)
            : new ClickerRelease(settings.MouseButton, 0, Shift: false);

    public IReadOnlyList<string> ToArguments() => new[]
    {
        Switch,
        Button?.ToString() ?? "-",
        Vk.ToString(CultureInfo.InvariantCulture),
        Shift ? "1" : "0"
    };

    /// <summary>true when the command line asks for release mode at all (even with broken arguments, so a bad start never opens the full app).</summary>
    public static bool IsInvocation(string[]? args)
        => args is { Length: > 0 } && string.Equals(args[0], Switch, StringComparison.Ordinal);

    /// <summary>null unless the arguments are exactly what <see cref="ToArguments"/> writes.</summary>
    public static ClickerRelease? TryParse(string[]? args)
    {
        if (args is null || args.Length != 4 || !string.Equals(args[0], Switch, StringComparison.Ordinal)) return null;
        ClickerMouseButton? button = args[1] switch
        {
            "-" => null,
            nameof(ClickerMouseButton.Left) => ClickerMouseButton.Left,
            nameof(ClickerMouseButton.Right) => ClickerMouseButton.Right,
            nameof(ClickerMouseButton.Middle) => ClickerMouseButton.Middle,
            _ => (ClickerMouseButton)(-1)
        };
        if (button is { } b && !Enum.IsDefined(b)) return null;
        if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int vk) || vk > 0xFE) return null;
        if (args[3] is not ("0" or "1")) return null;
        return new ClickerRelease(button, vk, args[3] == "1");
    }
}

/// <summary>
/// The real launcher: a named pipe server only the current user can open, then the clipper's own exe in host mode. The helper is
/// put into a kill-on-close job object, so it dies with the clipper however that ends (the helper also watches its parent and the pipe).
/// </summary>
public sealed class ClickerHostLauncher : IClickerHostLauncher, IDisposable
{
    private readonly Lazy<KillOnCloseJob?> _job = new(KillOnCloseJob.TryCreate);

    public IClickerHostSession Launch(string pipeName, int parentPid)
    {
        string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The path of the running program is unknown.");
        var command = HostCommand.Build(processPath, Assembly.GetEntryAssembly()?.Location, pipeName, parentPid);

        // The server exists before the process does, so the helper can never find the name unclaimed (or claimed by someone else first).
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        try
        {
            var info = new ProcessStartInfo(command.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(processPath) ?? Environment.CurrentDirectory
            };
            foreach (var arg in command.Arguments) info.ArgumentList.Add(arg);

            process = Process.Start(info) ?? throw new InvalidOperationException("The helper process did not start.");
            if (_job.Value is { } job && !job.TryAssign(process))
                Logger.Warn("AutoClicker: the helper process could not be put into the kill-on-close job.");
            return new HostSession(server, process);
        }
        catch
        {
            try { process?.Kill(); } catch { }
            process?.Dispose();
            server.Dispose();
            throw;
        }
    }

    /// <summary>Starts the release process. Deliberately not in the kill-on-close job: it must outlive a clipper that is closing.</summary>
    public void LaunchRelease(ClickerRelease release)
    {
        string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The path of the running program is unknown.");
        var info = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(processPath) ?? Environment.CurrentDirectory
        };
        bool viaDotnet = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        if (viaDotnet && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } dll) info.ArgumentList.Add(dll);
        foreach (var arg in release.ToArguments()) info.ArgumentList.Add(arg);
        using var process = Process.Start(info);
        Logger.Info($"AutoClicker: release process started ({string.Join(' ', release.ToArguments())}).");
    }

    public void Dispose()
    {
        if (_job.IsValueCreated) _job.Value?.Dispose();
    }

    private sealed class HostSession : IClickerHostSession
    {
        private readonly NamedPipeServerStream _server;
        private readonly Process _process;

        public HostSession(NamedPipeServerStream server, Process process)
        {
            _server = server;
            _process = process;
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => Exited?.Invoke();
        }

        public event Action? Exited;

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public async Task<Stream> ConnectAsync(CancellationToken ct)
        {
            await _server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            return _server;
        }

        public bool WaitForExit(int milliseconds)
        {
            try { return _process.WaitForExit(milliseconds); }
            catch (InvalidOperationException) { return true; }
        }

        public void Kill()
        {
            try { _process.Kill(); }
            catch (Exception) { /* already gone, or not ours to end */ }
        }

        public void Dispose()
        {
            try { _server.Dispose(); } catch { }
            try { _process.Dispose(); } catch { }
        }
    }
}
