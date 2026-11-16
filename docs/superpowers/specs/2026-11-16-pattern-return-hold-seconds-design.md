# AutoClicker: return to the start after a mouse pattern, hold time in seconds

Date: 2026-11-16. Base: master `528f370` (0.19.2). Owner request, approved in chat.

## 1. Return to the start (mouse patterns)

**Why.** A pattern whose motion does not end where it began drifts: every run starts where the last one ended, so the mouse
creeps further (owner: "dass die Maus nicht mit jedem neu Ausführen Stück für Stück immer weiter hoch moved").

**What.** A per-pattern choice "Zum Start zurück": **Aus** (default, also for existing patterns) / **Springen** / **Rückwärts**.

- **Springen:** at the end of a pass the mouse goes back in one move.
  - Pointer replay (absolute): exactly to the pointer position where the pass started.
  - Game replay (relative, raw): one relative move of exactly minus what the pass sent. Owner accepts that some games cap a
    huge single move.
- **Rückwärts:** the part of the path that was driven plays backwards at the same tempo and ends exactly at the start (pointer:
  the start coordinate; game: everything sent is undone).
- **Repeat on:** pass, return, next pass (each pass starts at the same point; no seamless continuation while a return mode is set).
- **Stopped with its own hotkey** (Hold released / Toggle pressed again): returns from wherever it is.
- **Pressed again during a return:** the rest of the return is jumped at once, then the new pass starts at the start point.
- **No return** when it stops for any other reason: its program left the front, task switcher, recording, lock, suppression,
  disarm/unbind, Windows refused a move, failure.
- The return belongs to the playback: the UI shows it as playing until the return is done.

**Approach (only sensible one).** The return is a phase of the playback inside `PatternPlayer` (same 1 ms tick, same
absolute-anchor / relative-sum bookkeeping). Rejected: appending a reversed copy to the path (cannot handle an early stop);
sending the return from the coordinator after `Finished` (races with the player's ticks and its anchor).

## 2. Hold time in seconds (clicker)

**Why.** "Haltedauer" is a percentage of the click interval today, which is hard to picture; the owner wants to choose seconds.

**What.** Behind the "Haltedauer" field a unit choice **% / Sekunden** (like the time limit's unit).
- Default and all existing settings and the built-in presets stay in %. A user preset stores the unit and both values.
- Seconds: three decimals (0,001 s .. 3600 s), default 0,050 s.
- The effective hold never exceeds what the cycle allows: at most the interval and the existing rate caps (same caps as for %).
  When the seconds value is cut, the actual value is shown next to the field.
- Keyboard "hold" mode keeps its meaning: Duty Mode "Hold", or duty at 100 % (percent unit only). A seconds value never switches
  the keyboard into hold mode.

## 3. Non-goals

- No change to how patterns are recorded, to Auto/Pointer/Raw selection or to the click engine's timing.
- No smooth "glide" return (offered, not chosen).
