<p align="center">
  <img src="ShortcutHookUI/Assets/ShortcutHook.png" alt="ShortcutHook" width="96"/>
</p>

# ShortcutHook

A Windows tool that maps mouse gestures and keyboard combos to keyboard chords, shell-execute targets, or shell commands. Runs as a lightweight background daemon with a modern dark-mode WPF settings UI.

## Features

- **Mouse gestures** — Left+Right, Left+Right×2, Double/Triple Right-click, Right-hold+Scroll, Double/Triple Wheel-click
- **Selection-aware double-right** — configure two actions for double-right click: one fires when text is selected, another when nothing is selected
- **Keyboard chords** — multi-key combos like `Ctrl+S+L` with smart defer logic for prefix pairs
- **Open anything** — launch apps, files, or folders via `open:` bindings
- **Run commands** — execute any shell command via `cmd:` (hidden) or `cmdw:` (visible window)
- **Alt+Scroll → Horizontal Scroll** — optional toggle; holding Alt while scrolling fires a horizontal scroll event
- **First-run setup wizard** — choose where to install the app; daemon script always goes to `C:\Tools\ShortcutHook`
- **Startup on login** — optional toggle to launch the daemon automatically
- **Self-contained** — single `.exe`, no installer or runtime prerequisites

## Preview

![ShortcutHook UI](ShortcutHookUI/Assets/ShortcutHook%20UI%20window.png)

## Download

Grab the latest `ShortcutHookUI.exe` from the [Releases](https://github.com/veera-bharath/ShortcutHook/releases) page.

1. Run `ShortcutHookUI.exe`
2. The setup wizard appears on first launch — choose an app folder (default `C:\Tools\ShortcutHook`) and click **Finish Setup**
3. Configure your shortcuts and hit **Save Changes**

That's it. The daemon starts automatically whenever you save.

## Install layout

| What | Where |
|------|-------|
| App (UI exe) | Your chosen folder (default `C:\Tools\ShortcutHook`) |
| Daemon script | Always `C:\Tools\ShortcutHook\ShortcutHook.ps1` |
| Config | `C:\Tools\ShortcutHook\shortcuts.json` |

## Config schema

```json
{
  "altHScroll": false,
  "bindings": [
    { "trigger": "mouse:left+right",        "output": "Win+Shift+S" },
    { "trigger": "mouse:left+rightx2",      "output": "Ctrl+Z" },
    { "trigger": "mouse:double-right",      "output": "Ctrl+C" },
    { "trigger": "mouse:double-right-sel",  "output": "Win+Shift+S" },
    { "trigger": "mouse:right-scroll-down", "output": "Win+D" },
    { "trigger": "key:Ctrl+Alt+C",          "output": "Ctrl+C" },
    { "trigger": "key:Ctrl+S+L",            "output": "F12" },
    { "trigger": "mouse:double-wheel",      "output": "open:C:\\path\\to\\app.lnk" },
    { "trigger": "key:Ctrl+Alt+T",          "output": "cmd:start wt.exe" },
    { "trigger": "key:Ctrl+Alt+L",          "output": "cmdw:tasklist" }
  ]
}
```

**Trigger prefixes**
- `mouse:` — `left+right`, `left+rightx2`, `double-right`, `double-right-sel`, `triple-right`, `right-scroll-down`, `right-scroll-up`, `double-wheel`, `triple-wheel`
- `key:` — any `Mod+Key` combo. Modifiers: `Ctrl`, `Shift`, `Alt`, `Win`

**Top-level fields**
- `altHScroll` — when `true`, holding Alt while scrolling fires a horizontal scroll instead of vertical (toggleable from the UI)

**Outputs**
- Keyboard chord — `Mod+Key` syntax (e.g. `Win+Shift+S`)
- Shell execute — `open:<path>` to launch an app, file, or folder
- Hidden command — `cmd:<command>` runs via `cmd.exe /c`, no window shown
- Visible command — `cmdw:<command>` opens a `cmd.exe` window and keeps it open after the command finishes

**Selection-aware double-right**

Configure both `mouse:double-right` and `mouse:double-right-sel`. When double-right fires, the daemon injects a silent Ctrl+C and checks the clipboard — if text was copied the `double-right-sel` action fires, otherwise `double-right` fires and the clipboard is restored.

## Building from source

Requirements: Windows 10/11 · PowerShell 5.1+ · .NET 8 SDK

```
cd ShortcutHookUI
Publish.bat
```

Output: `build\ShortcutHookUI.exe`

## Repository structure

```
ShortcutHookScripts/   PowerShell daemon (ShortcutHook.ps1)
ShortcutHookUI/        .NET 8 WPF settings UI source
build/                 Local build output (not tracked by Git)
```

## License

MIT — © 2025 Veera Bharath
