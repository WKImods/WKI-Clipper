using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>What one input did to a running hotkey capture.</summary>
public enum RecorderResult
{
    /// <summary>Still capturing.</summary>
    Pending,
    /// <summary>The combo is complete: <see cref="HotkeyRecorder.Result"/> holds it.</summary>
    Done,
    /// <summary>Keep the old hotkey (cancel button, a click beside the field, focus loss).</summary>
    Cancelled,
    /// <summary>The clear button: no hotkey (the required one goes back to the default).</summary>
    Cleared
}

/// <summary>
/// The capture logic of the hotkey box (free-hotkeys design section 4). Every input is part of the combo: keys incl. Esc and Backspace,
/// modifiers (left/right kept apart when <c>sided</c>), mouse buttons incl. the left one, the wheel. The combo is the set of inputs held
/// together (up to 5 main keys in press order); it is complete when everything is released again, also when it holds only modifiers.
/// A wheel notch completes it at once (a wheel cannot be held). Cancelling and clearing are the box's buttons, not keys.
/// <para>
/// A window does not see every key-down: RegisterHotKey (other programs, the shell's Win shortcuts) takes it, and Windows hands Print
/// Screen to a window as a key-up only. The low-level hook in the helper sees those presses, so the recorder counts such a lone key-up
/// as a full press - unless the key was already down when the capture started (the Enter that started it). And it does not finish
/// while such a key is still physically down: its key-up is yet to come.
/// </para>
/// Pure, so the WPF control only feeds it virtual keys as the clicker host's low-level hook reports them, and the live key state.
/// </summary>
public sealed class HotkeyRecorder
{
    private static readonly int[] KeyboardVks = Enumerable.Range(0x03, 0xFE - 0x03 + 1)
        .Where(vk => !ClickerVk.IsMouse(vk) && vk is not (ClickerVk.Shift or ClickerVk.Control or ClickerVk.Menu or 0xE5 or 0xE7))
        .ToArray();   // generic modifiers mirror the sided ones; VK_PROCESSKEY / VK_PACKET are no physical keys

    private readonly bool _sided;
    private readonly Func<int, bool>? _liveState;
    private readonly HashSet<int> _held = new();
    private readonly HashSet<int> _seen = new();
    private readonly HashSet<int> _downAtStart = new();   // released ones are removed: a later press of the key counts again
    private readonly List<int> _keys = new();
    private ClickerModifiers _mods;
    private bool _waitedForUnseenKey;

    /// <param name="liveState">
    /// The physical key state (GetAsyncKeyState), read at the start and before finishing; null = unknown (tests of the plain combo
    /// logic): then no key counts as down at the start and nothing is waited for.
    /// </param>
    public HotkeyRecorder(bool sided, Func<int, bool>? liveState = null)
    {
        _sided = sided;
        _liveState = liveState;
        if (liveState is not null) _downAtStart = KeysDown(liveState);
    }

    /// <summary>
    /// Whether the hotkey boxes record left and right apart: the stored choice, else whether a saved hotkey already has a sided
    /// modifier (settings from before the choice was stored), so the next recording does not quietly turn generic.
    /// </summary>
    public static bool RecordsSided(ClickerSettings c) => c.SidedModifiers ?? (HasSidedModifier(c.Hotkey) || HasSidedModifier(c.MasterHotkey));

    private static bool HasSidedModifier(ClickerHotkey? hotkey)
        => hotkey is not null && ModifierFamily.Families.Any(f => (hotkey.Modifiers & (f.Left | f.Right)) != 0);

    /// <summary>The keyboard keys <paramref name="liveState"/> reports as down (generic modifier codes left out: they mirror the sided keys).</summary>
    public static HashSet<int> KeysDown(Func<int, bool> liveState) => KeyboardVks.Where(liveState).ToHashSet();

    /// <summary>The finished combo after <see cref="RecorderResult.Done"/>.</summary>
    public ClickerHotkey? Result { get; private set; }

    /// <summary>What is recorded so far (for the live label while keys are held).</summary>
    public ClickerHotkey Preview => new() { Modifiers = _mods, Keys = new List<int>(_keys) };

    /// <param name="vk">A virtual key, mouse button or wheel pseudo key.</param>
    /// <param name="repeat">Auto-repeat: a repeating key that is not held here went down before the capture started (the Enter that
    /// started it) and is not part of the combo.</param>
    public RecorderResult Down(int vk, bool repeat = false)
    {
        if (Result is not null) return RecorderResult.Done;
        vk = Sided(vk);
        if (!ClickerVk.IsKeyCode(vk) || repeat && !_held.Contains(vk)) return RecorderResult.Pending;
        _seen.Add(vk);
        _downAtStart.Remove(vk);
        if (ClickerVk.IsWheel(vk))
        {
            Add(vk);
            return Finish();
        }
        if (Add(vk)) _held.Add(vk);   // a sixth key is not part of the combo and not waited for
        return RecorderResult.Pending;
    }

    public RecorderResult Up(int vk)
    {
        if (Result is not null) return RecorderResult.Done;
        vk = Sided(vk);
        if (!_held.Remove(vk))
        {
            // The release of a press from before the capture, or of a key already handled (a sixth key): not part of the combo.
            if (_downAtStart.Remove(vk) || _seen.Contains(vk) || !ClickerVk.IsKeyCode(vk) || ClickerVk.IsMouseInput(vk))
                return RecorderResult.Pending;
            // Pressed during the capture, but something took the key-down before the window (see the class summary): a full press.
            _seen.Add(vk);
            Add(vk);
            _waitedForUnseenKey = false;
        }
        return _held.Count == 0 ? Finish() : RecorderResult.Pending;
    }

    /// <summary>
    /// Drops held keyboard keys that are no longer down (<paramref name="isDown"/>: the key state of the message being handled).
    /// Windows can leave out a key-up, e.g. of the first Shift when both were held; without this the capture would never finish.
    /// </summary>
    public RecorderResult Reconcile(Func<int, bool> isDown)
    {
        if (Result is not null) return RecorderResult.Done;
        int dropped = _held.RemoveWhere(vk => !ClickerVk.IsMouseInput(vk) && !isDown(vk));
        return dropped > 0 && _held.Count == 0 ? Finish() : RecorderResult.Pending;
    }

    /// <summary>
    /// The field lost the focus. That cancels - unless the combo has a Win key and everything is physically released already
    /// (<paramref name="isDown"/>: the live key state): releasing Win opens the Start menu, which can take the focus before the
    /// key-up reaches the window. With Win still held, or with a key down (or waited for) whose press the window never saw, the focus
    /// went to a Win shortcut whose key the recording does not have: cancel.
    /// </summary>
    public RecorderResult FocusLost(Func<int, bool> isDown)
    {
        if (Result is not null) return RecorderResult.Done;
        bool win = (_mods & (ClickerModifiers.Win | ClickerModifiers.LWin | ClickerModifiers.RWin)) != 0;
        if (!win || _waitedForUnseenKey || UnseenKeyDown(isDown) || _held.Any(vk => ClickerVk.IsMouseInput(vk) || isDown(vk)))
            return RecorderResult.Cancelled;
        _held.Clear();
        return Finish();
    }

    private RecorderResult Finish()
    {
        var hotkey = Preview;
        if (!hotkey.IsValid()) return RecorderResult.Pending;
        if (_liveState is { } live && UnseenKeyDown(live))
        {
            // E.g. Win released before R of Win+R, whose key-down the shell took: R's key-up completes the combo.
            _waitedForUnseenKey = true;
            return RecorderResult.Pending;
        }
        Result = hotkey;
        return RecorderResult.Done;
    }

    /// <summary>A key is physically down that went down during the capture without the window seeing its press.</summary>
    private bool UnseenKeyDown(Func<int, bool> isDown)
        => KeyboardVks.Any(vk => !_seen.Contains(vk) && !_downAtStart.Contains(vk) && isDown(vk));

    /// <summary>Records a modifier as its flag or a main key in press order; false for a key beyond the fifth.</summary>
    private bool Add(int vk)
    {
        if (ModifierFlag(vk) is { } flag) { _mods |= flag; return true; }
        if (_keys.Contains(vk)) return true;
        if (_keys.Count >= ClickerHotkey.MaxKeys) return false;
        _keys.Add(vk);
        return true;
    }

    /// <summary>The generic modifier VKs (some keyboards and remote tools send them) count as the left key.</summary>
    private static int Sided(int vk) => vk switch
    {
        ClickerVk.Shift => ClickerVk.LShift,
        ClickerVk.Control => ClickerVk.LControl,
        ClickerVk.Menu => ClickerVk.LMenu,
        _ => vk
    };

    private ClickerModifiers? ModifierFlag(int vk)
    {
        foreach (var f in ModifierFamily.Families)
        {
            if (vk == f.VkLeft) return _sided ? f.Left : f.Generic;
            if (vk == f.VkRight) return _sided ? f.Right : f.Generic;
        }
        return null;
    }
}

/// <summary>
/// Turns the window messages the hotkey box reads into the codes the clicker host's low-level hooks report: window messages carry the
/// generic VK_SHIFT/VK_CONTROL/VK_MENU, the hook the sided key (AltGr arrives as a left Ctrl plus an extended right Alt in both).
/// </summary>
public static class RecorderKeys
{
    private const int ScanRightShift = 0x36;

    /// <summary>The sided key of a WM_(SYS)KEYDOWN/UP: Ctrl and Alt by the extended-key bit, Shift by its scan code.</summary>
    public static int FromKeyMessage(int vk, long lParam)
    {
        int scan = (int)((lParam >> 16) & 0xFF);
        bool extended = (lParam & (1L << 24)) != 0;
        return vk switch
        {
            ClickerVk.Shift => scan == ScanRightShift ? ClickerVk.RShift : ClickerVk.LShift,
            ClickerVk.Control => extended ? ClickerVk.RControl : ClickerVk.LControl,
            ClickerVk.Menu => extended ? ClickerVk.RMenu : ClickerVk.LMenu,
            _ => vk
        };
    }

    /// <summary>VK_PROCESSKEY: what a window gets instead of the key while an IME processes it.</summary>
    public const int VkProcessKey = 0xE5;

    /// <summary>
    /// The real key of a key message whose VK is <see cref="VkProcessKey"/> (the host's hook sees the real one): the IME's answer
    /// (<paramref name="imeVk"/>, ImmGetVirtualKey), else the key of the message's scan code (<paramref name="vkOfScan"/>,
    /// MapVirtualKey with 0xE0 for an extended key); 0 when neither knows. Any other VK is returned as it is.
    /// </summary>
    public static int UnwrapImeKey(int vk, long lParam, Func<int> imeVk, Func<uint, int> vkOfScan)
    {
        if (vk != VkProcessKey) return vk;
        int ime = imeVk();
        if (ime is > 0 and < 0xFF && ime != VkProcessKey) return ime;
        uint scan = (uint)((lParam >> 16) & 0xFF);
        if (scan == 0) return 0;
        int fromScan = vkOfScan((lParam & (1L << 24)) != 0 ? 0xE000 | scan : scan);
        return fromScan is > 0 and < 0xFF && fromScan != VkProcessKey ? fromScan : 0;
    }

    /// <summary>Bit 30 of a key-down's lParam: the key was already down (auto-repeat).</summary>
    public static bool IsRepeat(long lParam) => (lParam & (1L << 30)) != 0;

    /// <summary>The wheel pseudo key of a WM_MOUSEWHEEL / WM_MOUSEHWHEEL (delta in the high word of wParam); null for a zero delta.</summary>
    public static int? FromWheel(bool horizontal, long wParam)
    {
        short delta = unchecked((short)((wParam >> 16) & 0xFFFF));
        if (delta == 0) return null;
        // Same directions as the host's hook: vertical positive = forward (up), horizontal positive = tilted right.
        return horizontal
            ? delta > 0 ? ClickerVk.WheelRight : ClickerVk.WheelLeft
            : delta > 0 ? ClickerVk.WheelUp : ClickerVk.WheelDown;
    }

    /// <summary>
    /// The key behind a keyboard WM_APPCOMMAND: APPCOMMAND_BROWSER_BACKWARD (1) .. APPCOMMAND_LAUNCH_APP2 (18) are the commands Windows
    /// derives from VK_BROWSER_BACK (0xA6) .. VK_LAUNCH_APP2 (0xB7), in the same order. Null for mouse/OEM devices (a mouse button is
    /// recorded as itself) and for commands without such a key, which the host's hook would never see.
    /// </summary>
    public static int? FromAppCommand(long lParam)
    {
        int high = (int)((lParam >> 16) & 0xFFFF);
        if ((high & 0xF000) != 0) return null;
        int command = high & 0x0FFF;
        return command is >= 1 and <= 18 ? 0xA6 + command - 1 : null;
    }
}

/// <summary>Number fields of the AutoClicker tab: "1,05" and "1.05" both mean 1.05 (never "1.05 becomes 105"), no grouping.</summary>
public static class ClickerNumber
{
    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        var t = (text ?? "").Trim().Replace(" ", "").Replace(',', '.');
        if (t.Length == 0 || t.Count(ch => ch == '.') > 1) return false;
        if (!double.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        return double.IsFinite(value);
    }

    /// <summary>Up to two decimals, the decimal separator of the app language, no thousands separator.</summary>
    public static string Format(double value, bool english)
        => value.ToString("0.##", CultureInfo.GetCultureInfo(english ? "en-US" : "de-DE"));

    public static string Format(double value) => Format(value, L.English);
}

/// <summary>The Start/Stop button of the status row: enabled, and whether it stops a running clicker.</summary>
public readonly record struct ClickerStartButton(bool Enabled, bool IsStop);

/// <summary>Which palette brush the state pill uses.</summary>
public enum ClickerPillTone { Muted, Normal, Active, Danger }

/// <summary>Pure decisions of the status row and the statistics list.</summary>
public static class ClickerStatusRules
{
    /// <summary>Start needs a ready, unlocked helper, an allowing master switch and no blocking conflict; a running clicker can always be stopped.</summary>
    public static ClickerStartButton StartButton(ClickerState state, bool canStart)
    {
        if (state.IsRunning) return new(true, true);
        return new(state.Phase == ClickerPhase.Ready && !state.Locked && state.MasterAllowed && canStart, false);
    }

    public static ClickerPillTone Tone(ClickerState state)
    {
        if (state.Phase == ClickerPhase.Error || state.Locked) return ClickerPillTone.Danger;
        return state.Phase switch
        {
            ClickerPhase.Running => string.IsNullOrEmpty(state.BlockedByElevated) ? ClickerPillTone.Active : ClickerPillTone.Danger,
            ClickerPhase.Ready => ClickerPillTone.Normal,
            _ => ClickerPillTone.Muted
        };
    }

    /// <summary>A run's stored stop reason (a <see cref="StopReason"/> name) in the app language; any other text is shown as it is.</summary>
    public static string RunReason(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        return Enum.TryParse<StopReason>(stored, ignoreCase: false, out var reason) && Enum.IsDefined(reason) && !char.IsDigit(stored[0])
            ? StopReasonText.Describe(reason)
            : stored;
    }

    /// <summary>"0,4 s", "42 s", "2:05 min", "1:02:05 h".</summary>
    public static string Length(double seconds, bool english)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        if (seconds < 10) return ClickerNumber.Format(Math.Round(seconds, 1), english) + " s";
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        if (t.TotalMinutes < 1) return $"{t.Seconds} s";
        if (t.TotalHours < 1) return $"{t.Minutes}:{t.Seconds:00} min";
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00} h";
    }

    public static string Length(double seconds) => Length(seconds, L.English);
}
