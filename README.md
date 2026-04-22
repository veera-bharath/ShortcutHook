# ShortcutHook 🚀

ShortcutHook is a Windows-only tool that maps user-defined triggers (mouse gestures or keyboard combos) to keyboard chords or shell-execute targets (opening apps, files, or folders). 

It consists of a background PowerShell daemon that installs low-level hooks and a modern WPF settings UI for easy configuration.

## Features

- **Mouse Gestures**: Map actions to gestures like `Left + Right click`, `Double Right Click`, `Right-Scroll`, and more.
- **Keyboard Chords**: Create complex keyboard triggers (e.g., `Ctrl+S+L`) that map to other keys or actions.
- **Shell Execute**: Launch any application or folder using the `open:` prefix.
- **Smart Prefix Handling**: Support for multi-key combos with intelligent defer logic.
- **Clean UI**: Dark-mode WPF settings panel with real-time capture and configuration.

## Repository Structure

- `ShortcutHookScripts/`: The core PowerShell daemon (`ShortcutHook.ps1`).
- `ShortcutHookUI/`: Source code for the .NET 8 WPF settings UI.
- `build/`: Local distribution folder (ignored by Git, but available in Releases).

## How to Run

For the best experience, download the latest version from the **Releases** page.

1.  Extract the `.zip` file.
2.  Run `ShortcutHookUI.exe` to configure your shortcuts and start the background service.

## Building from Source

Requirements:
- Windows 10/11
- PowerShell 5.1+
- .NET 8.0 SDK (for building the UI)

To build the UI:
1.  Navigate to `ShortcutHookUI/`.
2.  Run `Publish.bat`.
3.  The compiled executable will be placed in the `build/` directory.

## License
MIT
