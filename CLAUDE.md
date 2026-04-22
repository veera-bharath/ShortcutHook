# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows-only shortcut-mapping tool. A background PowerShell process installs a low-level mouse hook (`WH_MOUSE_LL`) *and* a low-level keyboard hook (`WH_KEYBOARD_LL`) and maps user-defined triggers (mouse gestures or keyboard combos) to either keyboard chord outputs (VS Code-keybindings style) or shell-execute targets (open an app/file/folder).

Two-piece design: **daemon** is a PowerShell script; **settings UI** is a compiled .NET 8 WPF app distributed as a single-file self-contained `.exe`. Requires Windows 10/11, PowerShell 5+, .NET Framework 4.5+ (for the daemon's `Add-Type`). The `.exe` has no runtime prerequisite — the .NET 8 runtime is bundled.

## Commands

- `build/ShortcutHookUI.exe` — launches the settings UI (WPF dark-mode settings panel).
- `ShortcutHookUI/Publish.bat` — rebuilds the UI from source and drops a fresh `ShortcutHookUI.exe` into the `build/` folder.
- Log: `ShortcutHook.log` in the root directory (appended, UTF-8).
- Config: `build/shortcuts.json`.

No test harness. Verification is manual: run the daemon, perform the trigger, confirm the chord fires in a target app.

## Project Structure

- `ShortcutHookScripts/` — Contains the core daemon logic (`ShortcutHook.ps1`).
- `ShortcutHookUI/` — .NET 8 WPF project for the settings UI.
- `build/` — Distribution folder containing the compiled UI and the daemon script.
- `CLAUDE.md` — Project documentation and AI guidance.
- `.gitignore` — Git exclusion rules for build artifacts and logs.

## Config schema

```json
{
  "bindings": [
    { "trigger": "mouse:left+right",        "output": "Win+Shift+S" },
    { "trigger": "mouse:right-scroll-down", "output": "Win+D" },
    { "trigger": "key:Ctrl+Alt+C",          "output": "Ctrl+C" },
    { "trigger": "key:Ctrl+S+L",            "output": "F12" },
    { "trigger": "mouse:double-wheel",      "output": "open:C:\\path\\to\\Notepad.lnk" }
  ]
}
```

Trigger prefixes:
- `mouse:` — one of the seven fixed gesture detectors: `left+right`, `double-right`, `triple-right`, `right-scroll-down`, `right-scroll-up`, `double-wheel`, `triple-wheel`. The last two are double-/triple-click of the middle (wheel) button; the scroll gestures fire while the right button is held. Adding more requires new detection code in `MouseCallback`.
- `key:` — any `Mod(+Mod…)+Key(+Key…)` combo. Modifiers: `Ctrl`, `Shift`, `Alt`, `Win`. Non-modifier keys: A-Z, 0-9, F1-F12, and the named keys in `$vkMap` (Enter, Esc, Tab, Space, Back, Delete, Insert, Home, End, PgUp, PgDn, Left, Right, Up, Down, PrtScr).

Outputs come in two forms:
- **Keyboard chord** — `Mod+Key+Key` syntax. Modifiers are emitted in the order they appear in the string (matters for some shortcuts).
- **Shell-execute** — `open:<path>` where `<path>` is an app `.lnk`, an executable, a file, or a folder. Launched via `Process.Start` with `UseShellExecute = true` on a background thread, so the hook callback never blocks on process startup.

Defaults (used if `shortcuts.json` is absent or malformed): Win+Shift+S, Ctrl+C, Ctrl+V on `left+right`, `double-right`, `triple-right`.

## Architecture

### Two-process model coordinated by a named mutex

`ShortcutHook.ps1` is the daemon. On startup it grabs `Global\ShortcutHook` (a `System.Threading.Mutex`) as a single-instance guard. `ShortcutHookUI.ps1` **never talks to the daemon directly** — it only probes for the daemon's existence by calling `Mutex.OpenExisting('Global\ShortcutHook')` (success = running). Stop is implemented by finding the `powershell.exe` whose command line contains `ShortcutHook.ps1` (and not `ShortcutHookUI`) and killing it. Config changes take effect by kill + relaunch — the daemon reads `shortcuts.json` once at startup only.

### The C# hook compiled via Add-Type

The hook is a C# class `ShortcutHook` embedded as a here-string and compiled with `Add-Type` inside `ShortcutHook.ps1`. PowerShell parses `shortcuts.json`, builds `ShortcutHook.Binding` instances (PS uses the `[ShortcutHook+Binding]::new()` nested-class syntax), and calls `[ShortcutHook]::LoadBindings(...)` before `[ShortcutHook]::Start()`.

`Start()` installs both hooks, then runs a raw Win32 `GetMessage` loop (no WinForms — avoids STA/apartment requirements). Both low-level hook callbacks run on this same thread, so callbacks are serialized relative to each other; only the defer timer and mouse gesture timer run on worker threads.

### Binding storage and signatures

Key bindings are indexed by a canonical signature: `"{mods}:{sortedKeyVKs}"` (e.g. `"1:83"` for `Ctrl+S`, `"1:76,83"` for `Ctrl+S+L`). The signature is computed by `ShortcutHook.MakeSignature(mods, sortedKeys)` and stored on each `Binding`. Both the daemon and the UI compute identical signatures so that dedup validation and runtime lookup agree.

Modifier collapsing: L/R variants (`LCtrl`/`RCtrl`, `LShift`/`RShift`, etc.) plus the generic form all map to the same canonical bit — `MOD_CTRL`, `MOD_SHIFT`, `MOD_ALT`, `MOD_WIN`. The L/R distinction is not preserved in triggers.

### Mouse gesture detection

Two parallel timer-thread + generation-counter state machines live in `MouseCallback`:

- **Right-button machine** — counts right-clicks within `dblClickMs`; fires `BDoubleRight` / `BTripleRight` on 2×/3×, reinjects a real right-click on 1×. `reinjDown`/`reinjUp` counters swallow the reinjected events so they don't recurse. `BLeftRight` fires immediately when left is pressed while right is held (or vice versa). While right is held, `WM_MOUSEWHEEL` fires `BRightScrollDown` / `BRightScrollUp` based on the sign of `mouseData >> 16`.
- **Middle-button (wheel-click) machine** — mirror of the right machine with its own generation counter (`wGeneration`), state (`wheelHeld`, `wheelPending`, `wheelClickCount`), and reinjection counters (`reinjWheelDown`/`reinjWheelUp`). Fires `BDoubleWheel` / `BTripleWheel` on 2×/3×; a single middle-click is reinjected.

Each gesture maps to a single `Binding` field on the `ShortcutHook` class (`BLeftRight`, `BDoubleRight`, `BTripleRight`, `BRightScrollDown`, `BRightScrollUp`, `BDoubleWheel`, `BTripleWheel`), populated by `LoadBindings` from matching `mouse:` bindings. When a detector fires, `ExecuteBinding` dispatches either `FireOutput` (keyboard chord) or a background-thread `Process.Start` (`open:` path).

### Keyboard matcher (the interesting part)

`KbdCallback` runs a small state machine:

- `heldMods` is a bitmask of currently held modifiers. `heldKeys` is a `HashSet<byte>` of held non-modifier VKs. Modifier key events always pass through (apps need them for modifier state); they're just tracked.
- On each non-modifier key-down, the current `(heldMods, sorted heldKeys)` is computed. The matcher checks:
  1. **Exact match** of that signature in `KeySigIndex`.
  2. **Strict superset** — is there any registered binding with the same mods and a strictly larger key set that includes every currently-held key? (i.e. is a longer combo still possible?)
- Decision tree:
  - exact + no superset → fire, swallow the down, mark the VK as swallowed.
  - exact + superset exists → **defer** the fire by `DEFER_MS` (80 ms). Swallow the down, mark swallowed. If a subsequent deeper key progresses the combo, the defer is cancelled.
  - no exact + superset exists → swallow (we're building toward a longer combo). Cancel any pending short-combo defer.
  - neither → pass through.
- On key-up, if the VK was in `swallowedKeys`, swallow the up. If a deferred binding is armed and a swallowed key's release arrives, the defer resolves immediately (fires the short combo).

Defer-timer cancellation uses `deferGen` (a monotonically-incrementing int); the timer thread checks `myGen != deferGen` under `KLock` and no-ops if stale.

### Avoiding feedback from our own injections

The keyboard hook ignores events with `LLKHF_INJECTED` (bit 0x10 on `KBDLLHOOKSTRUCT.flags`). This is how `FireOutput` can safely `keybd_event` the output chord without the hook recursively processing its own synthetic keys. **Don't remove this check** — without it, every output would re-enter the matcher and chaos would follow.

The mouse hook still uses the older `reinjDown`/`reinjUp` counter pattern instead of `LLMHF_INJECTED`. Both approaches work; the counters predate the keyboard hook and weren't worth migrating.

### Output dispatch: FireOutput vs. Process.Start

All fires go through `ExecuteBinding(Binding b)`, which branches on the binding: `b.OpenPath != null` → `Process.Start` on a background thread with `UseShellExecute = true`; otherwise → `FireOutput(b.Output)`. Exactly one of `Output`/`OpenPath` is set per binding (enforced by PS at load time).

When firing a chord, `FireOutput` uses `GetAsyncKeyState` to read which modifiers the user is physically holding, releases them via synthesized `KEYEVENTF_KEYUP`, emits the output chord cleanly, then re-presses the released modifiers. This gives a clean modifier state for the output at the cost of a microseconds-scale modifier blip. Example: user holds Ctrl+S bound to Win+Tab — we release Ctrl, press/release Win+Tab, re-press Ctrl; the app sees a momentary Ctrl release followed by the chord followed by Ctrl down again.

`open:` outputs skip `FireOutput` entirely — `Process.Start` runs on a background thread so a slow shell-execute (e.g. cold-starting an app) cannot stall the hook callback past `LowLevelHooksTimeout`.

## UI architecture

`ui/` is a .NET 8 WPF project. Build output: a single-file self-contained `ShortcutHookUI.exe` copied to the repo root.

**Project layout:**
- `ShortcutHookUI.csproj` — `net8.0-windows`, `UseWPF=true`, `UseWindowsForms=true` (needed for `FolderBrowserDialog`), `PublishSingleFile=true`, `SelfContained=true`, `RuntimeIdentifier=win-x64`. Depends on `System.Management` NuGet for WMI (used to find the daemon process by command line).
- `App.xaml` / `App.xaml.cs` — minimal WPF app shell.
- `MainWindow.xaml` — all styles (`BtnPrimary`, `BtnGhost`, `DarkTB`, `DarkCB`, `Toggle`, `Card`, `SectionLabel`, `DarkCBItem`) and the window layout. The keyboard/mouse row grids are constructed in code, not via item templates.
- `MainWindow.xaml.cs` — the behavior. A single `Row` record holds the shared row state (Grid, OutputPanel, Action enum, OutputValue/Ctrl). Mouse rows are keyed by gesture in `_mouseRows`; keyboard rows live in `_kbdRows`. The capture state (`_captureActive`, `_captureRow`, `_captureBtn`, `_captureMods`, `_captureNonMods`) is private fields on `MainWindow` — no scope drama.
- `Models.cs` — `AppEntry`, `BindingEntry`, `ConfigRoot`, `MouseGestureDef`, `ParsedKey`.
- `Services.cs` — `TriggerHelpers` (parse, canonicalize, validate shortcut, prefix-of), `ConfigService` (read/write shortcuts.json via `System.Text.Json`), `DaemonService` (mutex probe + Start/Stop, WMI to find PS process with `ShortcutHook.ps1` in cmdline), `StartupService` (create/delete Startup-folder .lnk via WScript.Shell COM), `AppScanner` (enumerate Start Menu `.lnk` files).
- `Interop.cs` — `DwmSetWindowAttribute` P/Invoke (dark title bar + caption color).
- `Publish.bat` — `dotnet publish -c Release` + copy the exe to the repo root.

**Key capture:**
- Window-level `PreviewKeyDown` / `PreviewKeyUp` handlers. The capture button's `Click` handler schedules `Keyboard.Focus(captureButton)` via `Dispatcher.BeginInvoke(DispatcherPriority.Input, ...)` — deferring is necessary because WPF's mouse-click focus transition races with a same-frame `Focus()` call.
- State machine mirrors the PS version: modifier bitmask + ordered non-modifier `Key` list. Non-mod key-down accumulates; any key-up when non-mods are present finalizes. A bare Escape (no mods, no keys) cancels.
- `e.Key == Key.System` (when Alt is held) resolves to `e.SystemKey` for the actual key.

**Save flow:**
- Walks the 7 fixed mouse rows + any keyboard rows, runs `TriggerHelpers.CanonicalizeTrigger` for dedup (same signature form as the daemon: `key:{mods}:{sortedKeyVKs}` / `mouse:{gesture}`), blocks exact duplicates, validates `shortcut`-action outputs with `ValidateShortcutOutput`, then writes `shortcuts.json`. Prefix pairs (e.g. `Ctrl+S` bound alongside `Ctrl+S+L`) are **allowed** — they're the whole point of the defer logic in the daemon — but shown as an amber toast so the user knows the shorter combo will have a ~80 ms latency.
- If the daemon is running at save time, it's killed and relaunched (the daemon reads `shortcuts.json` once at startup).

**Daemon interop (from .exe to .ps1):**
- Liveness: `Mutex.OpenExisting(@"Global\ShortcutHook")` — matches the daemon's single-instance guard.
- Stop: WMI query `Win32_Process WHERE Name='powershell.exe'`, filter by `CommandLine` containing `ShortcutHook.ps1` and not `ShortcutHookUI`, `Process.Kill()`.
- Start: `Process.Start("powershell.exe", "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File <daemon>")`.

**Startup-on-login toggle:**
- Writes/removes `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\ShortcutHook.lnk` via `WScript.Shell` COM (`Type.GetTypeFromProgID` + dynamic dispatch). The shortcut launches the daemon hidden.

## Gotchas when editing

- The C# block is a PowerShell here-string (`@"..."@`) so `$` expansion applies — there are no literal `$`s in the C# today; be careful if adding any.
- Hook callbacks **must not throw** across the native boundary. The `try { ... } catch { /* swallow */ }` blocks in both callbacks are load-bearing.
- Never do slow work inside either callback — Windows silently unhooks a callback that exceeds `LowLevelHooksTimeout`. The defer timer and the mouse gesture timer run on separate threads for this reason.
- PS → C# type bridging: when assigning `byte[]` fields, the value must actually be `[byte[]]`. A bare pipeline output is `object[]`. The `Resolve-OutputChord` and `Resolve-KeyTrigger` helpers return already-cast `byte[]`; preserve this when editing.
- Adding a new mouse gesture type requires changes in four places: a new detector branch in `MouseCallback`, a new `Binding` field on `ShortcutHook` + assignment in `LoadBindings`, a new entry in `$validGestures` in `ShortcutHook.ps1` and `TriggerHelpers.ValidGestures` in `ui/Services.cs`, and a new entry in `MainWindow.MouseDefs` (`ui/MainWindow.xaml.cs`) so the gesture gets a row in the UI.
- Adding a new output kind (beyond keyboard chords and `open:`) requires: a new nullable field on `ShortcutHook.Binding`, a new branch in `ExecuteBinding`, PS-side parsing in `ShortcutHook.ps1`'s binding-build loop, and UI-side support in `MainWindow.xaml.cs` (`ActionKind` enum + `ActionLabels`/`ActionOrder` arrays, a `SetRowOutput` branch, a `GetRowOutput` branch, and `DetectAction` disambiguation).
- The UI's "Stop" kills the daemon process. Because `Start()` blocks in `GetMessage`, there is no graceful shutdown path — kill is the design. The `finally` releases the mutex but won't run on kill; the OS reclaims the mutex on process exit.
- Known v1 limitation: if a user presses a keyboard combo that is a strict prefix of a registered trigger but not itself a registered trigger (e.g. only `Ctrl+S+L` is bound, user types `Ctrl+S`), the shorter combo is **swallowed and discarded** rather than replayed. No current need to fix, but note it if a user reports "Ctrl+S stopped working after I added Ctrl+S+L".
