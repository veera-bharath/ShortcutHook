# ShortcutHook

A Windows tool that maps mouse gestures and keyboard combos to keyboard chords or shell-execute targets (open apps, files, or folders). Runs as a lightweight background daemon with a modern dark-mode WPF settings UI.

## Features

- **Mouse gestures** — Left+Right, Double/Triple Right-click, Right-hold+Scroll, Double/Triple Wheel-click
- **Keyboard chords** — multi-key combos like `Ctrl+S+L` with smart defer logic for prefix pairs
- **Open anything** — launch apps, files, or folders via `open:` bindings
- **Alt+Scroll → Horizontal Scroll** — optional toggle; holding Alt while scrolling fires a horizontal scroll event
- **First-run setup wizard** — choose where to install the app; daemon script always goes to `C:\Tools\ShortcutHook`
- **Startup on login** — optional toggle to launch the daemon automatically
- **Self-contained** — single `.exe`, no installer or runtime prerequisites

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
    { "trigger": "mouse:right-scroll-down", "output": "Win+D" },
    { "trigger": "key:Ctrl+Alt+C",          "output": "Ctrl+C" },
    { "trigger": "key:Ctrl+S+L",            "output": "F12" },
    { "trigger": "mouse:double-wheel",      "output": "open:C:\\path\\to\\app.lnk" }
  ]
}
```

**Trigger prefixes**
- `mouse:` — `left+right`, `double-right`, `triple-right`, `right-scroll-down`, `right-scroll-up`, `double-wheel`, `triple-wheel`
- `key:` — any `Mod+Key` combo. Modifiers: `Ctrl`, `Shift`, `Alt`, `Win`

**Top-level fields**
- `altHScroll` — when `true`, holding Alt while scrolling fires a horizontal scroll instead of vertical (toggleable from the UI)

**Outputs**
- Keyboard chord — `Mod+Key` syntax (e.g. `Win+Shift+S`)
- Shell execute — `open:<path>` to launch an app, file, or folder

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
