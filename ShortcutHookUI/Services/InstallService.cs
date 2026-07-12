using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace ShortcutHookUI.Services;

internal static class InstallService
{
    const string RegistryKeyPath       = @"Software\ShortcutHook";
    const string AppInstallPathValue   = "AppInstallPath";
    const string SetupCompleteValue    = "SetupComplete";
    const string DismissedUpdateValue  = "DismissedUpdateVersion";
    const string InstalledVersionValue = "InstalledVersion";
    const string ScriptResourceName   = "ShortcutHookUI.Runtime.ShortcutHookDaemon.exe";
    const string ScriptFileName       = "ShortcutHookDaemon.exe";
    const string UiExeFileName        = "ShortcutHookUI.exe";

    // Script + config always live here — fixed, never changes.
    public static string ScriptRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShortcutHook"
    );

    // Primary name for the daemon executable path.
    public static string DaemonPath => Path.Combine(ScriptRoot, ScriptFileName);

    // Deprecated alias — kept for any callers that haven't been updated yet.
    public static string ScriptPath => DaemonPath;

    // Written by the daemon whenever the global pause toggle fires.
    public static string PauseStatePath => Path.Combine(ScriptRoot, "pause.state");

    // App (exe) default: same folder as script, so a default install is self-contained.
    public static string DefaultAppRoot => ScriptRoot;

    public static string UiExePath(string appRoot) => Path.Combine(appRoot, UiExeFileName);

    public static bool TryGetConfiguredAppRoot(out string root)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
        var configured = key?.GetValue(AppInstallPathValue) as string;
        if (string.IsNullOrWhiteSpace(configured)) { root = DefaultAppRoot; return false; }
        root = configured;
        return true;
    }

    static void SaveAppRoot(string root)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
        key.SetValue(AppInstallPathValue, root);
    }

    public static bool IsSetupComplete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
        return (key?.GetValue(SetupCompleteValue) as int?) == 1 ||
               string.Equals(key?.GetValue(SetupCompleteValue)?.ToString(), "1", StringComparison.Ordinal);
    }

    public static void MarkSetupComplete()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
        key.SetValue(SetupCompleteValue, 1, RegistryValueKind.DWord);
    }

    // Tag of the release the user dismissed the update banner for (e.g. "v1.6.0").
    // Null if no dismissal has been recorded.
    public static string? GetDismissedUpdateVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
        return key?.GetValue(DismissedUpdateValue) as string;
    }

    public static void SetDismissedUpdateVersion(string tag)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
        key.SetValue(DismissedUpdateValue, tag);
    }

    public static Version? GetInstalledVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
        var val = key?.GetValue(InstalledVersionValue) as string;
        if (Version.TryParse(val, out var version))
            return version;
        return null;
    }

    public static void SaveInstalledVersion(Version version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
        key.SetValue(InstalledVersionValue, version.ToString());
    }

    // Script check uses fixed ScriptRoot — no arg needed.
    public static bool IsInstalled() => File.Exists(ScriptPath);

    // Exe check uses the user-chosen app folder.
    public static bool IsAppInstalled(string appRoot) => File.Exists(UiExePath(appRoot));

    public static void Install(string appRoot)
    {
        // 1. Extract embedded daemon exe to fixed script location.
        Directory.CreateDirectory(ScriptRoot);
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ScriptResourceName)
               ?? throw new InvalidOperationException("Embedded daemon executable not found."))
        using (var file = File.Create(DaemonPath))
        {
            stream.CopyTo(file);
        }

        // 2. Copy this exe to the chosen app folder (skip if already there).
        Directory.CreateDirectory(appRoot);
        var sourceExe = CurrentExecutablePath();
        var targetExe = UiExePath(appRoot);
        if (!string.Equals(Path.GetFullPath(sourceExe), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourceExe, targetExe, overwrite: true);

        // 3. Write default config if absent (migrating from old path if available).
        var newConfigPath = ConfigService.ConfigPath(ScriptRoot);
        if (!File.Exists(newConfigPath))
        {
            var oldConfigPath = Path.Combine(@"C:\Tools\ShortcutHook", "shortcuts.json");
            if (File.Exists(oldConfigPath))
            {
                try
                {
                    File.Copy(oldConfigPath, newConfigPath, overwrite: true);
                }
                catch
                {
                    ConfigService.Save(ScriptRoot, ConfigService.DefaultConfig());
                }
            }
            else
            {
                ConfigService.Save(ScriptRoot, ConfigService.DefaultConfig());
            }
        }

        // Migrate startup shortcut if it exists to point to the new daemon path.
        try
        {
            if (StartupService.IsEnabled())
            {
                StartupService.Set(true);
            }
        }
        catch { }

        // Clean up old daemon to avoid confusion.
        try
        {
            var oldDaemonPath = Path.Combine(@"C:\Tools\ShortcutHook", "ShortcutHookDaemon.exe");
            if (File.Exists(oldDaemonPath))
            {
                File.Delete(oldDaemonPath);
            }
        }
        catch { }

        SaveAppRoot(appRoot);

        // 4. Save installed version to registry.
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        SaveInstalledVersion(version);
    }

    public static void OpenScriptFolder()
    {
        Directory.CreateDirectory(ScriptRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ScriptRoot}\"") { UseShellExecute = true });
    }

    public static bool IsRunningFromInstalledLocation(string appRoot) =>
        string.Equals(Path.GetFullPath(CurrentExecutablePath()), Path.GetFullPath(UiExePath(appRoot)),
                      StringComparison.OrdinalIgnoreCase);

    public static void LaunchInstalledApp(string appRoot)
    {
        var installedExe = UiExePath(appRoot);
        if (!File.Exists(installedExe))
            throw new FileNotFoundException("Installed UI executable not found.", installedExe);
        Process.Start(new ProcessStartInfo(installedExe) { WorkingDirectory = appRoot, UseShellExecute = true });
    }

    static string CurrentExecutablePath() =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Unable to resolve the UI executable path.");

    public static void CreateStartMenuShortcut(string appRoot)
    {
        var exePath = UiExePath(appRoot);
        var shortcutDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "ShortcutHook");
        Directory.CreateDirectory(shortcutDir);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(Path.Combine(shortcutDir, "ShortcutHook UI.lnk"));
        shortcut.TargetPath = exePath;
        shortcut.WorkingDirectory = appRoot;
        shortcut.IconLocation = exePath;
        shortcut.Save();
    }

    public static void CreateDesktopShortcut(string appRoot)
    {
        var exePath = UiExePath(appRoot);
        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(Path.Combine(desktopDir, "ShortcutHook UI.lnk"));
        shortcut.TargetPath = exePath;
        shortcut.WorkingDirectory = appRoot;
        shortcut.IconLocation = exePath;
        shortcut.Save();
    }
}
