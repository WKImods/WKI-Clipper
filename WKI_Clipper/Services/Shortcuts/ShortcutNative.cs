using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using WKI_Clipper.Models;
using WKI_Clipper.Native;

namespace WKI_Clipper.Services;

/// <summary>
/// The thin native layer of the shortcut bar: reading shortcuts, icons, the default browser, Store apps - and starting
/// things. Everything that decides WHAT to do lives in the pure classes; this only touches Windows.
/// </summary>
public interface IShortcutShell
{
    /// <summary>Both Start-menu roots plus the user and public Desktop, recursive, every .lnk and .url (unreadable ones left out).</summary>
    IReadOnlyList<ShellLinkInfo> StartMenuAndDesktopLinks();

    /// <summary>One shortcut: IShellLinkW for a .lnk, the [InternetShortcut] URL= for a .url. Null when it cannot be read.</summary>
    ShellLinkInfo? ReadLink(string path);

    /// <summary>The exe the Windows default browser starts (AssocQueryString for "http"), or null.</summary>
    string? WindowsDefaultBrowserExe();

    /// <summary>Is the Store app with this AUMID installed for the current user?</summary>
    bool StoreAppInstalled(string aumid);

    /// <summary>
    /// The program's own icon as a 256 px PNG with alpha, cached on disk; null when it cannot be read.
    /// <paramref name="sourcePath"/> is an exe, .lnk or .url, or "shell:AppsFolder\AUMID". <paramref name="cacheKey"/> labels the cache file.
    /// </summary>
    string? IconPng(string sourcePath, string cacheKey);

    /// <summary>Starts like a double-click in the Start menu (spec section 4.2). False with a reason in <paramref name="error"/>; never throws.</summary>
    bool Start(LaunchTarget target, out string? error);
}

/// <summary>What <see cref="ShortcutStartPlan"/> decided: the thing to hand to ShellExecute, or why there is nothing to start.</summary>
internal sealed record StartPlan(string? FileName, string? Arguments, string? WorkingDirectory, string? Error);

/// <summary>
/// The start rules of spec section 4.2 as a pure decision: nothing is started here, which is what makes every rule testable.
/// Files must exist (a missing one is an error instead of a Windows dialog). There is no verb: the shell never runs "runas".
/// </summary>
internal static class ShortcutStartPlan
{
    public static StartPlan For(LaunchTarget t, IFileSystemView fs)
    {
        var target = (t.Target ?? "").Trim();
        var args = string.IsNullOrWhiteSpace(t.Arguments) ? null : t.Arguments.Trim();

        switch (t.Kind)
        {
            case ShortcutKind.SteamApp:
                // Digits only: the id ends up in a URL that the shell hands to Steam.
                if (target.Length == 0 || !target.All(char.IsAsciiDigit))
                    return Fail(L.T($"Ungültige Steam-App-ID \"{target}\".", $"Invalid Steam app id \"{target}\"."));
                return new StartPlan("steam://rungameid/" + target, null, null, null);

            case ShortcutKind.EpicApp:
                if (target.Length == 0) return Fail(L.T("Epic-App-Name fehlt.", "The Epic app name is missing."));
                return new StartPlan("com.epicgames.launcher://apps/" + Uri.EscapeDataString(target) + "?action=launch&silent=true", null, null, null);

            case ShortcutKind.StoreApp:
                if (target.Length == 0) return Fail(L.T("Die Store-App-ID fehlt.", "The Store app id is missing."));
                return new StartPlan("explorer.exe", @"shell:AppsFolder\" + target, null, null);

            case ShortcutKind.ShellLink:
                return ExistingFile(target, fs, startsInItsFolder: false, withArguments: false, args);

            case ShortcutKind.Exe:
                return ExistingFile(target, fs, startsInItsFolder: true, withArguments: true, args);

            case ShortcutKind.Browser:
            case ShortcutKind.DefaultBrowser:
                return ExistingFile(target, fs, startsInItsFolder: true, withArguments: false, args);

            case ShortcutKind.CatalogLauncher:
                // The Start-menu shortcut when the scan found one, else the exe from the registry.
                return IsLinkFile(target)
                    ? ExistingFile(target, fs, startsInItsFolder: false, withArguments: false, args)
                    : ExistingFile(target, fs, startsInItsFolder: true, withArguments: false, args);

            default:
                return Fail(L.T($"Unbekannte Startart {t.Kind}.", $"Unknown start kind {t.Kind}."));
        }
    }

    private static StartPlan ExistingFile(string path, IFileSystemView fs, bool startsInItsFolder, bool withArguments, string? args)
    {
        if (path.Length == 0) return Fail(L.T("Kein Startziel angegeben.", "No start target given."));
        if (!fs.FileExists(path)) return Fail(L.T($"Nicht gefunden: {path}", $"Not found: {path}"));
        return new StartPlan(path, withArguments ? args : null, startsInItsFolder ? ShortcutPaths.DirectoryOf(path) : null, null);
    }

    private static bool IsLinkFile(string path)
        => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);

    private static StartPlan Fail(string error) => new(null, null, null, error);
}

/// <summary>
/// Name of a cached icon file. The key changes when the file is rewritten (an update brings a new icon) and is the same for
/// any spelling of the path (case, slashes), so one program never fills the cache twice.
/// </summary>
public static class ShortcutIconKey
{
    public static string For(string sourcePath, DateTime lastWriteUtc)
    {
        var path = ShortcutPaths.Normalize(sourcePath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path + "|" + lastWriteUtc.Ticks));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

/// <summary>The real Windows behind <see cref="IShortcutShell"/>.</summary>
public sealed class Win32ShortcutShell : IShortcutShell
{
    private const int IconSize = 256;
    private static readonly TimeSpan ComTimeout = TimeSpan.FromSeconds(60);

    private readonly string _iconDir;
    private readonly DiskFileSystemView _disk = new();

    /// <param name="iconCacheDir">Where the PNGs go. null = %LOCALAPPDATA%\WKI_Clipper\AppIcons.</param>
    public Win32ShortcutShell(string? iconCacheDir = null)
    {
        _iconDir = iconCacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKI_Clipper", "AppIcons");
    }

    // ---- shortcuts ----

    public IReadOnlyList<ShellLinkInfo> StartMenuAndDesktopLinks()
    {
        var roots = new (string Path, int Depth)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), 8),
            (Environment.GetFolderPath(Environment.SpecialFolder.Programs), 8),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), 3),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), 3),
        };

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, depth) in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = depth,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                };
                foreach (var f in Directory.EnumerateFiles(root, "*", options))
                {
                    var ext = Path.GetExtension(f);
                    if ((ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) && seen.Add(f))
                        files.Add(f);
                }
            }
            catch (Exception ex) { Logger.Warn($"Shortcut scan: {root} could not be listed: {ex.Message}"); }
        }

        // One COM apartment for the whole batch; a link that cannot be read is simply left out.
        var result = new List<ShellLinkInfo>(files.Count);
        RunSta<object?>(() =>
        {
            foreach (var f in files)
            {
                var link = ReadLinkCore(f);
                if (link != null) lock (result) result.Add(link);
            }
            return null;
        });
        lock (result) return result.ToArray();
    }

    public ShellLinkInfo? ReadLink(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return RunSta(() => ReadLinkCore(path)); }
        catch (Exception) { return null; }
    }

    private static ShellLinkInfo? ReadLinkCore(string path)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                var url = ParseInternetShortcut(File.ReadAllText(path));
                return url == null ? null : new ShellLinkInfo(path, name, null, null, null, url);
            }

            var link = (Shell32.IShellLinkW)new Shell32.ShellLinkObject();
            try
            {
                ((IPersistFile)link).Load(path, 0);   // STGM_READ
                return new ShellLinkInfo(path, name, Read(link.GetPath), Read(link.GetArguments), Read(link.GetWorkingDirectory), null);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
        catch (Exception)
        {
            return null;   // a damaged or unreadable shortcut is left out, never an error for the scan
        }
    }

    private static string? Read(Action<StringBuilder, int> getter)
    {
        var sb = new StringBuilder(1024);
        try { getter(sb, sb.Capacity); }
        catch (COMException) { return null; }
        var s = sb.ToString();
        return s.Length == 0 ? null : s;
    }

    private static string? Read(Action<StringBuilder, int, IntPtr, uint> getPath)
        => Read((sb, cch) => getPath(sb, cch, IntPtr.Zero, 0));

    /// <summary>The URL of an Internet shortcut file: <c>URL=</c> inside the [InternetShortcut] section. Null when there is none.</summary>
    internal static string? ParseInternetShortcut(string text)
    {
        bool inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.StartsWith('['))
            {
                inSection = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (line[..eq].Trim().Equals("URL", StringComparison.OrdinalIgnoreCase))
            {
                var url = line[(eq + 1)..].Trim();
                return url.Length == 0 ? null : url;
            }
        }
        return null;
    }

    // ---- default browser, Store apps ----

    public string? WindowsDefaultBrowserExe()
    {
        try
        {
            var sb = new StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            int hr = Shell32.AssocQueryStringW(0, Shell32.ASSOCSTR_EXECUTABLE, "http", null, sb, ref len);
            if (hr != 0) return null;
            var exe = sb.ToString();
            return exe.Length == 0 ? null : exe;
        }
        catch (Exception ex)
        {
            Logger.Warn("Default browser lookup failed: " + ex.Message);
            return null;
        }
    }

    public bool StoreAppInstalled(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        return RunSta(() =>
        {
            try
            {
                var iid = Shell32.IID_IUnknown;
                int hr = Shell32.SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, ref iid, out var item);
                if (hr != 0 || item == IntPtr.Zero) return false;
                Marshal.Release(item);
                return true;
            }
            catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException) { return false; }
        });
    }

    // ---- icons ----

    public string? IconPng(string sourcePath, string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        try
        {
            bool shellName = sourcePath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
            DateTime stamp = DateTime.MinValue;
            if (!shellName)
            {
                if (!_disk.FileExists(sourcePath)) return null;
                stamp = File.GetLastWriteTimeUtc(sourcePath);
            }

            var label = SafeFileLabel(cacheKey);
            var file = Path.Combine(_iconDir, label + "_" + ShortcutIconKey.For(sourcePath, stamp) + ".png");
            if (File.Exists(file)) return file;

            var png = RunSta(() => ExtractPng(sourcePath));
            if (png == null) return null;

            Directory.CreateDirectory(_iconDir);
            var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, png);
            File.Move(tmp, file, overwrite: true);
            DeleteOutdated(label, file);
            return file;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Icon of '{sourcePath}' could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>The shell's icon at 256 px with alpha. The DIB bits are taken as 32bpp ARGB: FromHbitmap alone loses the alpha channel.</summary>
    private static byte[]? ExtractPng(string source)
    {
        var iid = Shell32.IID_IShellItemImageFactory;
        int hr = Shell32.SHCreateItemFromParsingName(source, IntPtr.Zero, ref iid, out var unk);
        if (hr != 0 || unk == IntPtr.Zero) return null;

        Shell32.IShellItemImageFactory factory;
        try { factory = (Shell32.IShellItemImageFactory)Marshal.GetObjectForIUnknown(unk); }
        finally { Marshal.Release(unk); }

        IntPtr hbm = IntPtr.Zero;
        try
        {
            hr = factory.GetImage(new Shell32.SIZE(IconSize, IconSize), Shell32.SIIGBF_ICONONLY | Shell32.SIIGBF_BIGGERSIZEOK, out hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;

            using var bmp = Image.FromHbitmap(hbm);
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
            try
            {
                using var ms = new MemoryStream();
                if (Image.GetPixelFormatSize(bmp.PixelFormat) == 32)
                {
                    using var argb = new Bitmap(bmp.Width, bmp.Height, data.Stride, PixelFormat.Format32bppArgb, data.Scan0);
                    argb.Save(ms, ImageFormat.Png);
                }
                else
                {
                    bmp.Save(ms, ImageFormat.Png);   // an icon without an alpha channel
                }
                return ms.ToArray();
            }
            finally { bmp.UnlockBits(data); }
        }
        finally
        {
            if (hbm != IntPtr.Zero) Shell32.DeleteObject(hbm);
            Marshal.ReleaseComObject(factory);
        }
    }

    private static string SafeFileLabel(string cacheKey)
    {
        var sb = new StringBuilder();
        foreach (char c in cacheKey ?? "")
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_');
        return sb.Length == 0 ? "icon" : sb.ToString();
    }

    /// <summary>An updated program gets a new key; the PNG of its previous version is no longer needed.</summary>
    private void DeleteOutdated(string label, string keep)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_iconDir, label + "_????????????????.png"))
                if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) File.Delete(f);
        }
        catch (Exception) { /* leftovers are harmless */ }
    }

    // ---- starting ----

    public bool Start(LaunchTarget target, out string? error)
    {
        error = null;
        try
        {
            var plan = ShortcutStartPlan.For(target, _disk);
            if (plan.FileName == null)
            {
                error = plan.Error ?? L.T("Nichts zu starten.", "Nothing to start.");
                return false;
            }

            // The program we start may bring itself to the front; we touch no window of anyone.
            User32.AllowSetForegroundWindow(User32.ASFW_ANY);

            var psi = new ProcessStartInfo { FileName = plan.FileName, UseShellExecute = true };
            if (plan.Arguments != null) psi.Arguments = plan.Arguments;
            if (plan.WorkingDirectory != null) psi.WorkingDirectory = plan.WorkingDirectory;
            Logger.Info($"Shortcut start: {target.Kind} -> {plan.FileName} {plan.Arguments}".TrimEnd());
            using var started = Process.Start(psi);   // null is normal when the shell hands the request to a running program
            return true;
        }
        catch (Win32Exception ex)
        {
            // 1223 = the user declined the elevation prompt of a program that asks for it.
            error = ex.NativeErrorCode == 1223 ? L.T("Start abgebrochen.", "Start cancelled.") : ex.Message;
            Logger.Warn($"Shortcut start failed ({target.Kind} {target.Target}): {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Warn($"Shortcut start failed ({target.Kind} {target.Target}): {ex.Message}");
            return false;
        }
    }

    // ---- COM apartment ----

    /// <summary>Runs shell COM calls on a fresh STA thread (the shell objects are apartment threaded; pool threads are MTA). A hung call is given up after a minute.</summary>
    private static T? RunSta<T>(Func<T?> work)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true, Name = "WKI shortcut shell (STA)" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(ComTimeout))
        {
            Logger.Warn("A shell call for the shortcut bar did not return in time and was left behind.");
            return default;
        }
        if (failure != null) throw new InvalidOperationException(failure.Message, failure);
        return result;
    }
}
