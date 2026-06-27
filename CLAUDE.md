# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows-only shortcut-mapping tool. A background PowerShell process installs a low-level mouse hook (`WH_MOUSE_LL`) *and* a low-level keyboard hook (`WH_KEYBOARD_LL`) and maps user-defined triggers (mouse gestures, keyboard combos, or process lifecycle events) to outputs: keyboard chords, shell-execute targets, shell commands, text expansion, horizontal scroll, pause/resume, or profile switches.

Two-piece design: **daemon** is a PowerShell script; **settings UI** is a compiled .NET 8 WPF app distributed as a single-file self-contained `.exe`. Requires Windows 10/11, PowerShell 5+. The `.exe` has no runtime prerequisite — the .NET 8 runtime is bundled and the PS1 daemon is embedded as a manifest resource.

## Commands

- `build/ShortcutHookUI.exe` — launches the settings UI (WPF dark-mode settings panel).
- `ShortcutHookUI/Publish.bat` — rebuilds the UI from source and drops a fresh `ShortcutHookUI.exe` into `build/`.
- Log: `ShortcutHook.log` in the root directory (appended, UTF-8).
- Config: `C:\Tools\ShortcutHook\shortcuts.json` (fixed path after install).

No test harness. Verification is manual: run the daemon, perform the trigger, confirm the chord fires in a target app.

## Project Structure

- `ShortcutHookScripts/` — Core daemon logic (`ShortcutHook.ps1`). Embedded into the exe as a manifest resource at build time.
- `ShortcutHookUI/` — .NET 8 WPF project for the settings UI.
- `.github/workflows/release.yml` — CI pipeline; triggers on `v*.*.*` tag push, runs `dotnet publish`, signs via SignPath (gated until OSS approval), creates GitHub Release.
- `build/` — Local build output (not tracked by Git; distributed via GitHub Releases).
- `CLAUDE.md` — Project documentation and AI guidance.

## Install layout (runtime)

| What | Where |
|------|-------|
| App (UI exe) | User-chosen folder (default `C:\Tools\ShortcutHook`) |
| Daemon script | Always `C:\Tools\ShortcutHook\ShortcutHook.ps1` (fixed) |
| Config | Always `C:\Tools\ShortcutHook\shortcuts.json` (fixed) |
| Registry | `HKCU\Software\ShortcutHook` — `AppInstallPath`, `SetupComplete` |

## Config schema

```json
{
  "altHScroll": false,
  "activeProfile": "Default",
  "ignoredApps": ["YourGame.exe", "mstsc.exe"],
  "profiles": [
    {
      "name": "Default",
      "bindings": [
        { "trigger": "mouse:left+right",        "outputs": ["Win+Shift+S"] },
        { "trigger": "mouse:double-right",      "outputs": ["Ctrl+V"] },
        { "trigger": "mouse:double-right-sel",  "outputs": ["Ctrl+C"] },
        { "trigger": "mouse:right-scroll-down", "outputs": ["Delete"] },
        { "trigger": "mouse:alt-scroll-up",     "outputs": ["hscroll:left"] },
        { "trigger": "mouse:double-wheel",      "outputs": ["open:C:\\path\\to\\app.lnk"] },
        { "trigger": "key:Ctrl+Alt+C",          "outputs": ["Ctrl+C"] },
        { "trigger": "key:Ctrl+S+L",            "outputs": ["F12"], "apps": ["Code.exe"] },
        { "trigger": "key:Ctrl+Alt+E",          "outputs": ["type:user@example.com"], "showToast": true },
        { "trigger": "key:Ctrl+Alt+P",          "outputs": ["toggle:pause"], "showToast": true },
        { "trigger": "key:Ctrl+Alt+L",          "outputs": ["cmdw:tasklist"], "enabled": false, "label": "list procs" },
        { "trigger": "key:Ctrl+Alt+1",          "outputs": ["profile:Gaming"] },
        { "trigger": "launch:chrome.exe",       "outputs": ["profile:Browser"] },
        { "trigger": "exit:chrome.exe",         "outputs": ["profile:Default"] },
        { "trigger": "app-focus:Code.exe",      "outputs": ["profile:Coding"] },
        { "trigger": "app-blur:Code.exe",       "outputs": ["profile:Default"] }
      ]
    }
  ]
}
```

**Top-level fields:**
- `altHScroll` — when `true`, holding Alt while scrolling fires `MOUSEEVENTF_HWHEEL` instead of vertical scroll. The injection always runs on a background thread — never inline in `MouseCallback`.
- `activeProfile` — name of the profile whose bindings the daemon loads on startup.
- `ignoredApps` — array of process names where all triggers are suppressed while that app is focused. Omit or `null` to disable. Checked in both `MouseCallback` and `KbdCallback` after the paused/injected-event guards.
- `profiles` — array of named binding sets. Older configs with a top-level `bindings` array are auto-migrated into a `"Default"` profile on load.

**Trigger prefixes:**
- `mouse:` — `left+right`, `left+rightx2`, `left+rightx3`, `double-right`, `double-right-sel`, `triple-right`, `single-wheel`, `double-wheel`, `triple-wheel`, `right-scroll-down`, `right-scroll-up`, `shift-scroll-down`, `shift-scroll-up`, `ctrl-shift-scroll-down`, `ctrl-shift-scroll-up`, `alt-scroll-down`, `alt-scroll-up`. Adding a new gesture requires changes in four places — see Gotchas.
- `key:` — any `Mod(+Mod…)+Key(+Key…)` combo. Modifiers: `Ctrl`, `Shift`, `Alt`, `Win`. Non-modifier keys: A-Z, 0-9, F1-F12, and named keys in `$vkMap`. Single-letter global `Ctrl` triggers (e.g. `Ctrl+C`) are blocked by the UI to prevent hijacking standard shortcuts.
- `launch:<processName>` / `exit:<processName>` — fires when the named process starts or exits. Detected by a background `System.Threading.Timer` polling `Process.GetProcesses()` every ~1.5 s and diffing against the previous snapshot (no WMI — that needs elevation). App-scope (`apps`) doesn't apply; the UI's "APP TRIGGERS" section has no scope picker for these rows.
- `app-focus:<processName>` / `app-blur:<processName>` — fires when the named process gains or loses foreground focus. Same polling mechanism as `launch:`/`exit:`.

**Per-binding optional fields:**
- `outputs` — array of one or more output strings executed in order. Use `outputDelay` to pause between steps.
- `outputDelay` — ms to wait between chained `outputs` steps.
- `label` — short user note; UI-only, never sent to daemon. Omitted from JSON when empty.
- `apps` — array of process names to scope the binding to a specific foreground app; `null`/omit for global.
- `enabled` — `false` disables without deleting; shown dimmed in UI.
- `debounce` — `true` on scroll bindings ignores repeated firings within 200 ms.
- `showToast` — `true` shows a brief on-screen toast when this binding fires.

**Output kinds:**
- **Keyboard chord** — `Mod+Key` syntax (e.g. `Win+Shift+S`). Modifiers emitted in string order.
- **Shell-execute** — `open:<path>` — app `.lnk`, exe, file, or folder. `Process.Start` with `UseShellExecute = true` on a background thread.
- **Hidden command** — `cmd:<command>` — runs via `cmd.exe /c`, no window.
- **Visible command** — `cmdw:<command>` — opens `cmd.exe` window, stays open after command finishes.
- **Text expansion** — `type:<text>` — pastes text via clipboard on a background thread.
- **Horizontal scroll** — `hscroll:left` / `hscroll:right` — fires `WM_MOUSEHWHEEL` on a background thread.
- **Pause/resume toggle** — `toggle:pause` — suspends or resumes all hook processing; UI shows "Paused" badge.
- **Profile switch** — `profile:<name>` — validates target profile exists, updates `activeProfile` in `shortcuts.json`, and relaunches the daemon. No-ops if already on that profile. Uses `PostThreadMessage(WM_QUIT)` to exit the `GetMessage` loop, then `ProcessStartInfo` relaunch (no console flash).

Defaults (used if `shortcuts.json` is absent or malformed): Win+Shift+S, Ctrl+C, Ctrl+V on `left+right`, `double-right`, `triple-right`.

## Architecture

### Two-process model coordinated by a named mutex

`ShortcutHook.ps1` is the daemon. On startup it grabs `Global\ShortcutHook` (a `System.Threading.Mutex`) as a single-instance guard. The UI **never talks to the daemon directly** — it only probes for the daemon's existence by calling `Mutex.OpenExisting('Global\ShortcutHook')` (success = running). Stop is implemented by finding the `powershell.exe` whose command line contains `ShortcutHook.ps1` (and not `ShortcutHookUI`) and killing it. Config changes take effect by kill + relaunch — the daemon reads `shortcuts.json` once at startup only.

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

### Keyboard matcher

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

### Win-key suppression

When a binding fires (both keyboard chords and `open:` targets) while the Win key is physically held, `ExecuteBinding` releases Win synthetically before dispatch and sets `suppressWinUp = true`. `KbdCallback` swallows the physical Win-up event when this flag is set, preventing Explorer from seeing a clean Win tap and opening the Start Menu.

### Avoiding feedback from our own injections

The keyboard hook ignores events with `LLKHF_INJECTED` (bit 0x10 on `KBDLLHOOKSTRUCT.flags`). This is how `FireOutput` can safely `keybd_event` the output chord without the hook recursively processing its own synthetic keys. **Don't remove this check** — without it, every output would re-enter the matcher and chaos would follow.

The mouse hook still uses the older `reinjDown`/`reinjUp` counter pattern instead of `LLMHF_INJECTED`. Both approaches work; the counters predate the keyboard hook and weren't worth migrating.

### Output dispatch: FireOutput vs. Process.Start

All fires go through `ExecuteBinding(Binding b)`, which branches on the binding: `b.OpenPath != null` → `Process.Start` on a background thread with `UseShellExecute = true`; otherwise → `FireOutput(b.Output)`. Exactly one of `Output`/`OpenPath` is set per binding (enforced by PS at load time).

When firing a chord, `FireOutput` uses `GetAsyncKeyState` to read which modifiers the user is physically holding, releases them via synthesized `KEYEVENTF_KEYUP`, emits the output chord cleanly, then re-presses the released modifiers (Win excluded — see Win-key suppression above).

`open:` outputs skip `FireOutput` entirely — `Process.Start` runs on a background thread so a slow shell-execute cannot stall the hook callback past `LowLevelHooksTimeout`.

## UI architecture

`ShortcutHookUI/` is a .NET 8 WPF project. Build output: a single-file self-contained `ShortcutHookUI.exe` (68 MB, .NET 8 runtime bundled).

**Project layout:**
- `ShortcutHookUI.csproj` — `net8.0-windows`, `UseWPF=true`, `UseWindowsForms=true` (needed for `FolderBrowserDialog`), `PublishSingleFile=true`, `SelfContained=true`, `RuntimeIdentifier=win-x64`. Depends on `System.Management` NuGet for WMI. Embeds `ShortcutHook.ps1` as a manifest resource (`ShortcutHookUI.Runtime.ShortcutHook.ps1`).
- `App.xaml` / `App.xaml.cs` — minimal WPF app shell.
- `MainWindow.xaml` — all shared styles (`BtnPrimary`, `BtnGhost`, `DarkTB`, `DarkCB`, `Toggle`, `Card`, `SectionLabel`, `DarkCBItem`) and the main window layout. Keyboard/mouse row grids are constructed in code, not via item templates. Contains a profile dropdown + Settings gear in the header; a search bar above the scroll view; and a `SetupRoot` overlay for first-run setup.
- `MainWindow.xaml.cs` — main window behavior. `_appRoot` tracks the user-chosen exe install folder (from registry). All config reads/writes use `InstallService.ScriptRoot` (fixed path). Key state: `_mouseRows`, `_kbdRows`, `_appTriggerRows`, `_setupComplete`, capture fields. `ActionKind` enum + `ActionLabels`/`ActionOrder` arrays drive the action-type picker in each chain item.
- `AboutWindow.xaml` / `AboutWindow.xaml.cs` — small dark modal showing app name, version (read from assembly), author (Veera Bharath), and a GitHub button.
- `LogViewerWindow.xaml` / `LogViewerWindow.xaml.cs` — dark resizable window (680×520) that tails `ShortcutHook.log` (last 2000 lines) via `FileSystemWatcher`. Non-modal — stays open while editing bindings. Provides Open Log File, Open Folder, and Clear Log (with confirmation) actions. Opened from Settings → View Logs.
- `Models.cs` — `AppEntry`, `BindingEntry` (includes `label`, `outputDelay`, `debounce`, `showToast`), `ConfigRoot` (includes `ignoredApps`, `activeProfile`, `profiles`), `MouseGestureDef`, `ParsedKey`, `Profile`.
- `Services.cs` — all service classes (see below).
- `Interop.cs` — `DwmApi` (dark title bar P/Invoke), `HookApi` (low-level keyboard hook for key capture).
- `Publish.bat` — `dotnet publish -c Release` + copy exe to `build/`.

**Services.cs classes:**
- `TriggerHelpers` — parse, canonicalize, validate shortcut, prefix-of detection. Recognizes all trigger prefixes including `launch:`, `exit:`, `app-focus:`, `app-blur:`. `ValidGestures` is the authoritative list of valid `mouse:` gesture names.
- `ConfigService` — read/write `shortcuts.json` at `InstallService.ScriptRoot` via `System.Text.Json`. Key methods: `Read()`, `Write(config)`, `SetIgnoredApps(list)`, `SerializeBinding(entry)`, `ParseBinding(json)`, `AddBindingToActiveProfile(entry)`.
- `InstallService` — install logic with split paths:
  - `ScriptRoot` = `C:\Tools\ShortcutHook` (fixed constant, never changes)
  - `ScriptPath` = `C:\Tools\ShortcutHook\ShortcutHook.ps1` (derived from ScriptRoot)
  - `DefaultAppRoot` = `C:\Tools\ShortcutHook` (default, user can change)
  - `Install(appRoot)` — extracts embedded PS1 to ScriptRoot, copies exe to appRoot, writes default config if absent, saves appRoot to registry
  - Registry key: `HKCU\Software\ShortcutHook`, values: `AppInstallPath`, `SetupComplete`
  - `IsInstalled()` — checks PS1 at ScriptRoot (no arg)
  - `IsAppInstalled(appRoot)` — checks exe at appRoot
  - `CreateStartMenuShortcut` / `CreateDesktopShortcut` — point to exe at appRoot
  - `LaunchInstalledApp` / `IsRunningFromInstalledLocation` — for post-setup relaunch
- `DaemonService` — `Start()` / `Stop()` / `IsRunning()`. Start uses `InstallService.ScriptPath` directly (no arg).
- `StartupService` — `Set(bool)` writes/removes Startup folder `.lnk` pointing to the daemon script at `InstallService.ScriptPath`.
- `AppScanner` — enumerates Start Menu `.lnk` files for the Open App picker.

**First-run setup flow:**
- On launch, `UpdateSetupState()` checks `_setupComplete` + `IsInstalled()` + `IsAppInstalled(_appRoot)`.
- If not fully set up, `SetupRoot` overlay is shown (covers the main UI).
- Step 1: folder picker → `InstallService.Install(appRoot)`. Shows both paths read-only: App (chosen) and Script (`C:\Tools\ShortcutHook`).
- Steps 2 & 3: optional Start Menu / Desktop shortcuts.
- Finish: `MarkSetupComplete()`, create shortcuts, relaunch from installed exe location if needed.

**Key capture:**
- Window-level `PreviewKeyDown` / `PreviewKeyUp` handlers plus a `WH_KEYBOARD_LL` hook installed during capture to swallow system hotkeys (Win+X etc.).
- State machine: modifier bitmask + ordered non-modifier `Key` list. Non-mod key-down accumulates; any key-up when non-mods are present finalizes. A bare Escape cancels.
- `e.Key == Key.System` (when Alt is held) resolves to `e.SystemKey` for the actual key.

**Binding search/filter:**
- A `TextBox` above the scroll view filters `_mouseGestureStacks` and `_kbdCards` by hiding non-matching rows on text change. Matches case-insensitively against gesture/trigger label, all chain output values, and app names. Auto-expands accordion sections while a filter is active; restores previous state on clear. Esc while focused clears; reset on profile switch or config reload.

**Per-row binding export/import:**
- Each binding row has a `⬆` button that serializes the row to `BindingEntry` JSON via `ConfigService.SerializeBinding` and copies it to the clipboard.
- An `↓ Import` button reads JSON from the clipboard, validates via `TriggerHelpers`, dedup-checks against the active profile, and appends via `ConfigService.AddBindingToActiveProfile`. Exact duplicates are blocked; prefix pairs produce an amber warning but are allowed.

**Save flow:**
- Walks the 7 fixed mouse rows + any keyboard rows + app-trigger rows, runs `TriggerHelpers.CanonicalizeTrigger` for dedup, blocks exact duplicates, validates shortcut outputs. Prefix pairs are allowed but shown as an amber toast (~80 ms latency warning). Writes `shortcuts.json` to `InstallService.ScriptRoot`, then kill + relaunch the daemon if it was running.

**Daemon interop:**
- Liveness: `Mutex.OpenExisting(@"Global\ShortcutHook")`.
- Stop: WMI `Win32_Process` query filtered by `CommandLine` containing `ShortcutHook.ps1` and not `ShortcutHookUI`, then `Process.Kill()`.
- Start: `Process.Start("powershell.exe", "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File <ScriptPath>")`.

**Startup-on-login toggle:**
- Writes/removes `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\ShortcutHook.lnk` via `WScript.Shell` COM. The shortcut targets `powershell.exe` with args pointing to `InstallService.ScriptPath`.

## Gotchas when editing

- The C# block is a PowerShell here-string (`@"..."@`) so `$` expansion applies — there are no literal `$`s in the C# today; be careful if adding any.
- Hook callbacks **must not throw** across the native boundary. The `try { ... } catch { /* swallow */ }` blocks in both callbacks are load-bearing.
- Never do slow work inside either callback — Windows silently unhooks a callback that exceeds `LowLevelHooksTimeout`. The defer timer and the mouse gesture timer run on separate threads for this reason. **Critically: never call `mouse_event` or `SendInput` inline inside `MouseCallback`** — the injected event is dispatched synchronously through the full hook chain before returning, which easily exceeds the timeout and permanently kills the hook for the session. Always fire injections on a background thread (see the `altHScroll` handler as the pattern).
- PS → C# type bridging: when assigning `byte[]` fields, the value must actually be `[byte[]]`. A bare pipeline output is `object[]`. The `Resolve-OutputChord` and `Resolve-KeyTrigger` helpers return already-cast `byte[]`; preserve this when editing.
- Adding a new mouse gesture type requires changes in four places: a new detector branch in `MouseCallback`, a new `Binding` field on `ShortcutHook` + assignment in `LoadBindings`, a new entry in `$validGestures` in `ShortcutHook.ps1` and `TriggerHelpers.ValidGestures` in `Services.cs`, and a new entry in `MainWindow.MouseDefs` so the gesture gets a row in the UI.
- Adding a new output kind requires: a new nullable field on `ShortcutHook.Binding` (or a new `ChainStep` field), a new branch in `ExecuteBinding`/`ExecuteStep`, PS-side parsing in `ShortcutHook.ps1`'s binding-build loop, and UI-side support in `MainWindow.xaml.cs` (`ActionKind` enum + `ActionLabels`/`ActionOrder` arrays, a `SetChainItemOutput` branch, a `GetChainItemOutput` branch, and `DetectAction` disambiguation). `profile:` outputs skip all `ValidateShortcutOutput` guard blocks in the save flow.
- The `ignoredApps` check sits in both `MouseCallback` and `KbdCallback`, after the `IsPaused` check and (in kbd) after the injected-event guard. It calls `GetForegroundWindow` → `GetWindowThreadProcessId` → `Process.GetProcessById`. This is safe inside a LL hook only because it's a fast in-process lookup; do not add any blocking I/O around it.
- The UI's "Stop" kills the daemon process. Because `Start()` blocks in `GetMessage`, there is no graceful shutdown path — kill is the design. The `finally` releases the mutex but won't run on kill; the OS reclaims the mutex on process exit.
- `InstallService.ScriptRoot` is a hardcoded constant (`C:\Tools\ShortcutHook`). The daemon script and config always live here. The app exe lives at `_appRoot` (user-chosen, stored in registry). Don't conflate the two.
- When a keyboard combo is a strict prefix of a registered trigger but not itself registered (e.g. only `Ctrl+S+L` is bound, user types `Ctrl+S`), the shorter combo is swallowed while waiting for the longer one. If the longer key never comes, the swallowed key is **replayed** via `prefixSwallowed` tracking — so `Ctrl+S` still works. The replay fires on key-up and only if no binding fired during the wait.
- Win-key bindings (`key:Win+X → open:...`): the synthetic Win-up that would normally clean up key state is deferred until the physical Win-up is swallowed. At that point a Ctrl-down/Win-up/Ctrl-up sequence is injected to release Win without triggering Start Menu (`releaseWinOnSuppress` flag). Never inject synthetic Win-up eagerly for `open:` bindings — it causes Start Menu to open.
