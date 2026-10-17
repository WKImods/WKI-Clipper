using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WKI_Clipper.Native;

/// <summary>
/// Read-only access to XInput controllers. Only XInputGetState is ever called: no XInputSetState (rumble
/// would fight the game's own), no SendInput, no drivers, no HID handles.
///
/// XInput keeps delivering to a process that is not in the foreground (verified on the owner's PC with the
/// browser and Steam in front). Windows.Gaming.Input and GameInput do not by default (focus-gated), which
/// is why they are not used.
/// </summary>
internal static class XInput
{
    /// <summary>Newest first: Windows 8+ ships 1.4; 1.3 is the DirectX redistributable; 9.1.0 is the Vista-era fallback.</summary>
    public static readonly string[] LibraryNames = { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" };

    public const uint ERROR_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        /// <summary>Changes whenever the pad reports something new.</summary>
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint GetStateDelegate(uint userIndex, out XINPUT_STATE state);

    /// <summary>
    /// First library of <paramref name="names"/> whose XInputGetState export <paramref name="resolveExport"/>
    /// can find (non-zero pointer), with that pointer. A resolver that throws counts as "not there".
    /// </summary>
    internal static (string Library, IntPtr Function)? Resolve(IEnumerable<string> names, Func<string, IntPtr> resolveExport)
    {
        foreach (var name in names)
        {
            IntPtr fn;
            try { fn = resolveExport(name); }
            catch { continue; }
            if (fn != IntPtr.Zero) return (name, fn);
        }
        return null;
    }

    /// <summary>Loads XInputGetState from the first available system library; null when there is none.</summary>
    public static GetStateDelegate? LoadGetState(out string? library)
    {
        library = null;
        var found = Resolve(LibraryNames, ResolveFromSystem32);
        if (found is null) return null;
        library = found.Value.Library;
        return Marshal.GetDelegateForFunctionPointer<GetStateDelegate>(found.Value.Function);
    }

    private static IntPtr ResolveFromSystem32(string name)
    {
        // System32 only: a controller poll must never pick up a DLL planted next to the exe or in the cwd.
        if (!NativeLibrary.TryLoad(name, typeof(XInput).Assembly, DllImportSearchPath.System32, out var handle))
            return IntPtr.Zero;
        if (NativeLibrary.TryGetExport(handle, "XInputGetState", out var fn))
            return fn;
        NativeLibrary.Free(handle);
        return IntPtr.Zero;
    }
}
