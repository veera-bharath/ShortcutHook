<p align="center">
  <img src="ShortcutHookUI/Assets/ShortcutHook.png" alt="ShortcutHook" width="96"/>
</p>

# ShortcutHook

A Windows tool that maps mouse gestures and keyboard combos to keyboard chords, shell-execute targets, or shell commands. Runs as a lightweight background daemon with a modern dark-mode WPF settings UI.

**[⬇️ Go to Download Section](#download)**

## Features

- **Mouse gestures** — Left+Right, Left+Right×2, Double/Triple Right-click, Right-hold+Scroll, Double/Triple Wheel-click
- **Selection-aware double-right** — configure separate triggers for selected vs. unselected states. Works seamlessly for text, files, folders, and images in Explorer, web browsers, and other applications
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

Grab the latest **ShortcutHookUI.exe** directly or browse all available versions:

- 🚀 [Download Latest EXE](https://github.com/veera-bharath/ShortcutHook/releases/latest/download/ShortcutHookUI.exe)
- 📦 [Browse Releases](https://github.com/veera-bharath/ShortcutHook/releases)

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

Configure both `mouse:double-right` (runs when nothing is selected, e.g. for Paste) and `mouse:double-right-sel` (runs when something is selected, e.g. for Copy). 

The daemon dynamically uses two advanced detection strategies depending on the active application:
- **File Explorer Native Query**: If the active foreground window is File Explorer or the Desktop, the daemon uses dynamic COM Automation Reflection to query `SelectedItems.Count` natively. If there is no selection, it executes Paste instantly with **zero clipboard clearing and zero simulated keystrokes**, completely avoiding any recursive system folder copy prompts (like `$RECYCLE.BIN`).
- **High-Fidelity Clipboard Backup & Restoration**: For all other applications, the daemon performs a simulated `Ctrl+C` check. It utilizes unmanaged Win32 APIs (`EnumClipboardFormats`, `GetClipboardData`, `GlobalAlloc`) to create a format-preserving binary-level backup of the clipboard (supporting text, copied files, HTML, rich text, and unmanaged GDI bitmap handles like `CF_BITMAP` and `CF_ENHMETAFILE` using `CopyImage`). If no selection is detected, the clipboard state is fully and losslessly restored, enabling copied images to be pasted into browser chats (like ChatGPT or Gemini) exactly like a physical `Ctrl+V` keypress.

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
