# WKI AutoClicker — free hotkeys (any key, any mouse button, mouse wheel)

Status: requested by Phillip on 2026-10-29 during the live test ("Ich möchte, dass ich für den Hotkey JEDE Taste
einstellen kann von Tastatur und Maus." / mouse wheel: "Auch Halten-Modus. Komplett freie Wahl des Users").
Extends `2026-10-28-native-autoclicker-design.md` section 5 (hotkeys) and section 9 (UI).

## 1. Goal

The clicker hotkey and the master-switch hotkey accept every input the user can produce on keyboard and mouse,
in Toggle and in Hold mode. Only an empty hotkey is impossible. Risky choices are allowed and get a warning, not a
block. What the recorder captures is exactly what later triggers.

Out of scope: the keyboard output key ("Taste" in "Was geklickt wird") keeps its current rules. The Fn key is never
visible to Windows; extra mouse buttons beyond X1/X2 work only when vendor software (e.g. G HUB) maps them to keys.

## 2. What becomes possible (all currently impossible)

1. Modifier keys alone or only modifiers in a combo: Shift, Ctrl, Alt, Win, sided LShift/RCtrl/..., AltGr
   (which Windows reports as LCtrl + RAlt), Ctrl+Shift, ...
2. Left mouse button alone (and with anything else). Right/middle/X1/X2 alone stay possible.
3. Esc and Backspace (today they cancel or clear the recording).
4. Keys the current WPF recorder does not see or mangles: Print Screen (WPF only gets its key-up), F10 and keys
   held with Alt (arrive as Key.System), Pause, Num Lock, Scroll Lock, media/browser/volume keys, F13-F24,
   application/menu key, IME keys - every VK 1..254 the low-level keyboard hook can report.
5. Mouse wheel as keys: wheel up, wheel down, tilt left, tilt right (WM_MOUSEWHEEL / WM_MOUSEHWHEEL).

## 3. Model and matching

- `ClickerHotkey` may have no non-modifier key when it has at least one modifier ("modifier-only hotkey").
  Persisted JSON shape stays `{ Modifiers, Keys }` - no schema bump; every v12 file stays valid as it is.
- Wheel inputs get pseudo key codes outside the VK range (e.g. 0x101..0x104), named in `ClickerVk`, validated,
  displayed ("Mausrad hoch/runter/links/rechts" / "Wheel up/down/left/right"), and treated like mouse inputs
  (the existing "mouse hotkeys do not fire over the clipper's own windows" rule applies to them too).
- Wheel "held" semantics (host): a wheel notch in one direction presses that wheel key; it counts as held while
  notches in the same direction keep arriving and is released after a quiet period (~250 ms, a named constant).
  One continuous scroll is therefore one press: Toggle flips once per scroll gesture, Hold clicks while scrolling.
- Matching for a modifier-only hotkey: match = all demanded modifiers down (sided rules as today) and, with
  StrictModifiers, no undemanded modifier family down. Rising edge of the match = press. Extra non-modifier keys
  being down do not block (same as today).
- Left mouse button as hotkey works because injected clicks carry our tag and are ignored by the hook (Hold mode +
  LMB hotkey + LMB output = "rapid fire while held"; Toggle = next physical LMB stops).
- Conflict rules: everything that is still possible but risky becomes a warning instead of blocking start:
  output key/button equals (part of) the hotkey, bare LMB/RMB, modifier-only, Esc, wheel, hotkey equals master.
  Blocking stays only for: no output key, empty whitelist (as today). Clipper hotkey collisions stay warnings.

## 4. Recorder (UI)

- Starting: click the hotkey field (the click that starts recording never counts).
- While recording, every key/mouse button/wheel input is recorded, including Esc, Backspace, LMB (pressed over
  the field) and modifier keys. The combo is the set of inputs held together; recording finishes when everything
  is released again (a wheel input finishes at once). A modifier-only combo finishes on release too.
- Cancel and clear are explicit buttons next to the field ("Abbrechen", "Leeren"/"Standard" for the required main
  hotkey); clicking outside the field and focus loss still cancel. The prompt text says how to cancel.
- Keys WPF does not deliver as key-downs (Print Screen, System keys, media keys) must still be recorded with their
  real VK, matching what the host's hook reports (sided modifiers when "Links/rechts unterscheiden" is on, generic
  otherwise; AltGr as Ctrl+Alt generic / LCtrl+RAlt sided). Injected input from our own clicker stays ignored.
- The armed helper does not react while a hotkey is being recorded (existing suppression stays).
- After recording, risky choices show the warning text under the field (from the conflict rules).

## 5. Hook watchdog bug (found in the live test, fix in the same change)

`wki_clicker_host.log` shows "Hotkey hooks: no events while input arrived - re-installing the hooks." every 6-30 s
from the moment the helper starts, both while Phillip used the PC by hand and while input was injected, while
hotkeys kept working. Find the root cause from the code (the host cannot be run in automated work) - candidates:
work done inside the hook callbacks that can exceed the system's LowLevelHooksTimeout (locks shared with the
engine/coordinator, pipe writes, file logging, EnumWindows for the over-clipper check) so Windows silently drops the
hook; or input sources that update GetLastInputInfo without passing the LL hooks. Fix the cause (the hook callbacks
must only record the event and return; slow work happens elsewhere), keep the watchdog for genuinely dead hooks,
and make the warning line carry the numbers that explain it (input tick, last hook tick, delta, which hook).

## 6. Testing

Pure logic gets unit tests (model validity, matcher incl. modifier-only and sided AltGr, wheel hold/release
timing with an injectable clock, conflicts/warnings, recorder state machine incl. Esc/Backspace/LMB/modifier-only/
wheel, watchdog decision). UI and hooks are verified in Phillip's live test.
