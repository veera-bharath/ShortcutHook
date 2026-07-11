using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

using ShortcutHookCore.Models;
using ShortcutHookCore.Parsing;
using ShortcutHookCore.Security;

namespace ShortcutHookUI;

internal static class ConfigService
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static readonly List<BindingEntry> Defaults = new()
    {
        new() { trigger = "mouse:left+right",   outputs = new List<string> { "Win+Shift+S" } },
        new() { trigger = "mouse:double-right", outputs = new List<string> { "Ctrl+C" } },
        new() { trigger = "mouse:triple-right", outputs = new List<string> { "Ctrl+V" } },
    };

    public static string ConfigPath(string root) => Path.Combine(root, "shortcuts.json");

    static void NormalizeOutputs(List<BindingEntry> bindings)
    {
        foreach (var b in bindings)
        {
            if ((b.outputs == null || b.outputs.Count == 0) && b.output != null)
                b.outputs = new List<string> { b.output };
            b.output = null;
            if (b.outputs == null || b.outputs.Count == 0)
                b.outputs = new List<string> { "" };

            // Normalize legacy `app` → `apps`
            if ((b.apps == null || b.apps.Count == 0) && !string.IsNullOrWhiteSpace(b.app))
                b.apps = new List<string> { b.app };
            b.app = null;
        }
    }

    static void DecryptConfig(ConfigRoot doc)
    {
        if (doc.bindings != null)
        {
            foreach (var b in doc.bindings)
            {
                if (b.outputs != null)
                {
                    for (int i = 0; i < b.outputs.Count; i++)
                    {
                        if (b.outputs[i].StartsWith("type-enc:", StringComparison.Ordinal))
                        {
                            b.outputs[i] = "type:" + DpapiHelper.Decrypt(b.outputs[i].Substring(9));
                        }
                    }
                }
            }
        }
        if (doc.profiles != null)
        {
            foreach (var profile in doc.profiles)
            {
                if (profile.bindings != null)
                {
                    foreach (var b in profile.bindings)
                    {
                        if (b.outputs != null)
                        {
                            for (int i = 0; i < b.outputs.Count; i++)
                            {
                                if (b.outputs[i].StartsWith("type-enc:", StringComparison.Ordinal))
                                {
                                    b.outputs[i] = "type:" + DpapiHelper.Decrypt(b.outputs[i].Substring(9));
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    public static ConfigRoot ReadConfig(string root)
    {
        var p = ConfigPath(root);
        if (File.Exists(p))
        {
            try
            {
                var txt = File.ReadAllText(p);
                var doc = JsonSerializer.Deserialize<ConfigRoot>(txt);
                if (doc is not null)
                {
                    DecryptConfig(doc);
                    return Migrate(doc, root);
                }
            }
            catch { }
        }
        return DefaultConfig();
    }

    // Migrates a freshly-deserialized ConfigRoot to the profiles format, persisting the
    // migration if the on-disk file was in the old (top-level `bindings`) or empty format.
    static ConfigRoot Migrate(ConfigRoot doc, string root)
    {
        if (doc.profiles is { Count: > 0 })
        {
            doc.bindings = null;
            foreach (var profile in doc.profiles)
                NormalizeOutputs(profile.bindings);
            if (!doc.profiles.Any(pr => string.Equals(pr.name, doc.activeProfile, StringComparison.Ordinal)))
                doc.activeProfile = doc.profiles[0].name;
            return doc;
        }

        // Old format (top-level `bindings`) or an empty config — wrap into a "Default" profile.
        var bindings = doc.bindings is { Count: > 0 } ? doc.bindings : new List<BindingEntry>();
        NormalizeOutputs(bindings);

        doc.bindings = null;
        doc.profiles = new List<ProfileEntry> { new() { name = "Default", bindings = bindings } };
        doc.activeProfile = "Default";
        Save(root, doc);
        return doc;
    }

    public static ProfileEntry GetActiveProfile(ConfigRoot config) =>
        config.profiles.FirstOrDefault(p => string.Equals(p.name, config.activeProfile, StringComparison.Ordinal))
        ?? config.profiles[0];

    public static List<BindingEntry> Read(string root) => GetActiveProfile(ReadConfig(root)).bindings;

    public static ConfigRoot DefaultConfig() => new()
    {
        activeProfile = "Default",
        profiles = new List<ProfileEntry> { new() { name = "Default", bindings = new List<BindingEntry>(Defaults) } },
    };

    public static void Save(string root, ConfigRoot config)
    {
        config.bindings = null;
        Directory.CreateDirectory(root);

        // Encrypt type outputs in memory before serialization
        if (config.profiles != null)
        {
            foreach (var profile in config.profiles)
            {
                if (profile.bindings != null)
                {
                    foreach (var b in profile.bindings)
                    {
                        if (b.outputs != null)
                        {
                            for (int i = 0; i < b.outputs.Count; i++)
                            {
                                if (b.outputs[i].StartsWith("type:", StringComparison.Ordinal))
                                {
                                    b.outputs[i] = "type-enc:" + DpapiHelper.Encrypt(b.outputs[i].Substring(5));
                                }
                            }
                        }
                    }
                }
            }
        }

        try
        {
            File.WriteAllText(ConfigPath(root), JsonSerializer.Serialize(config, JsonOpts));
        }
        finally
        {
            // Decrypt back to plaintext in memory
            if (config.profiles != null)
            {
                foreach (var profile in config.profiles)
                {
                    if (profile.bindings != null)
                    {
                        foreach (var b in profile.bindings)
                        {
                            if (b.outputs != null)
                            {
                                for (int i = 0; i < b.outputs.Count; i++)
                                {
                                    if (b.outputs[i].StartsWith("type-enc:", StringComparison.Ordinal))
                                    {
                                        b.outputs[i] = "type:" + DpapiHelper.Decrypt(b.outputs[i].Substring(9));
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // Replaces the active profile's bindings, preserving other profiles and settings.
    public static void SaveActiveProfileBindings(string root, IEnumerable<BindingEntry> bindings)
    {
        var config = ReadConfig(root);
        GetActiveProfile(config).bindings = bindings.ToList();
        Save(root, config);
    }

    public static void AddProfile(string root, string name)
    {
        var config = ReadConfig(root);
        config.profiles.Add(new ProfileEntry { name = name, bindings = new List<BindingEntry>() });
        Save(root, config);
    }

    public static void RenameProfile(string root, string oldName, string newName)
    {
        var config  = ReadConfig(root);
        var profile = config.profiles.FirstOrDefault(p => string.Equals(p.name, oldName, StringComparison.Ordinal));
        if (profile == null) return;
        profile.name = newName;
        if (string.Equals(config.activeProfile, oldName, StringComparison.Ordinal))
            config.activeProfile = newName;
        Save(root, config);
    }

    public static void DeleteProfile(string root, string name)
    {
        var config = ReadConfig(root);
        config.profiles.RemoveAll(p => string.Equals(p.name, name, StringComparison.Ordinal));
        Save(root, config);
    }

    public static void SetActiveProfile(string root, string name)
    {
        var config = ReadConfig(root);
        config.activeProfile = name;
        Save(root, config);
    }

    public static void SetIgnoredApps(string root, List<string> apps)
    {
        var config = ReadConfig(root);
        config.ignoredApps = apps.Count > 0 ? apps : null;
        Save(root, config);
    }

    // Serializes a single BindingEntry to a compact JSON string suitable for clipboard.
    public static string SerializeBinding(BindingEntry entry) =>
        JsonSerializer.Serialize(entry, JsonOpts);

    // Parses a clipboard JSON snippet into a BindingEntry. Throws on bad JSON or missing trigger.
    public static BindingEntry ParseBinding(string json)
    {
        var entry = JsonSerializer.Deserialize<BindingEntry>(json.Trim())
            ?? throw new FormatException("Could not parse JSON.");
        if (string.IsNullOrWhiteSpace(entry.trigger))
            throw new FormatException("Missing 'trigger' field.");
        // Normalize legacy single-output field.
        if ((entry.outputs == null || entry.outputs.Count == 0) && entry.output != null)
            entry.outputs = new List<string> { entry.output };
        if (entry.outputs == null || entry.outputs.Count == 0)
            entry.outputs = new List<string> { "" };
        return entry;
    }

    // Appends a single binding to the active profile and saves.
    public static void AddBindingToActiveProfile(string root, BindingEntry entry)
    {
        var config = ReadConfig(root);
        GetActiveProfile(config).bindings.Add(entry);
        Save(root, config);
    }

    // Serializes a single profile as a standalone JSON document: { "name": ..., "bindings": [...] }.
    public static void ExportProfile(string path, ProfileEntry profile)
    {
        if (profile.bindings != null)
        {
            foreach (var b in profile.bindings)
            {
                if (b.outputs != null)
                {
                    for (int i = 0; i < b.outputs.Count; i++)
                    {
                        if (b.outputs[i].StartsWith("type:", StringComparison.Ordinal))
                        {
                            b.outputs[i] = "type-enc:" + DpapiHelper.Encrypt(b.outputs[i].Substring(5));
                        }
                    }
                }
            }
        }

        try
        {
            var export = new ProfileEntry { name = profile.name, bindings = profile.bindings };
            File.WriteAllText(path, JsonSerializer.Serialize(export, JsonOpts));
        }
        finally
        {
            if (profile.bindings != null)
            {
                foreach (var b in profile.bindings)
                {
                    if (b.outputs != null)
                    {
                        for (int i = 0; i < b.outputs.Count; i++)
                        {
                            if (b.outputs[i].StartsWith("type-enc:", StringComparison.Ordinal))
                            {
                                b.outputs[i] = "type:" + DpapiHelper.Decrypt(b.outputs[i].Substring(9));
                            }
                        }
                    }
                }
            }
        }
    }

    // Parses a standalone profile export file. Throws FormatException/JsonException if invalid.
    public static ProfileEntry ImportProfile(string path)
    {
        var txt = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(txt);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("bindings", out var bindingsEl) || bindingsEl.ValueKind != JsonValueKind.Array)
            throw new FormatException("File must contain a 'name' string and a 'bindings' array.");

        var name     = nameEl.GetString()!;
        var bindings = JsonSerializer.Deserialize<List<BindingEntry>>(bindingsEl.GetRawText()) ?? new();

        foreach (var b in bindings)
        {
            if (b.outputs != null)
            {
                for (int i = 0; i < b.outputs.Count; i++)
                {
                    if (b.outputs[i].StartsWith("type-enc:", StringComparison.Ordinal))
                    {
                        b.outputs[i] = "type:" + DpapiHelper.Decrypt(b.outputs[i].Substring(9));
                    }
                }
            }
        }

        return new ProfileEntry { name = name, bindings = bindings };
    }

    // Normalizes and validates bindings imported from a profile export, dropping
    // any entry with an unrecognized trigger. Returns the surviving bindings and
    // the number that were skipped.
    public static List<BindingEntry> SanitizeImportedBindings(List<BindingEntry> bindings, out int skipped)
    {
        NormalizeOutputs(bindings);

        var result = new List<BindingEntry>();
        skipped = 0;
        foreach (var b in bindings)
        {
            try { TriggerParser.CanonicalizeTrigger(b.trigger); }
            catch { skipped++; continue; }
            result.Add(b);
        }
        return result;
    }
}

internal static class ProfileHelpers
{
    public const int MaxProfiles  = 10;
    public const int MaxNameLength = 32;

    // Next "Profile-N" default name, skipping any numbers already in use.
    public static string NextDefaultName(IEnumerable<ProfileEntry> profiles)
    {
        var existing = new HashSet<string>(profiles.Select(p => p.name), StringComparer.OrdinalIgnoreCase);
        var n = profiles.Count() + 1;
        while (existing.Contains($"Profile-{n}")) n++;
        return $"Profile-{n}";
    }

    // Returns an error message, or null if the name is valid.
    public static string? ValidateName(string name, IEnumerable<ProfileEntry> profiles, string? excludeName = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name cannot be empty.";
        if (name.Length > MaxNameLength) return $"Name must be {MaxNameLength} characters or fewer.";
        foreach (var c in name)
            if (c < 0x20 || c > 0x7E || c is '/' or '\\' or '"' or '\'')
                return "Name can only contain printable characters (no slashes or quotes).";
        if (profiles.Any(p => !string.Equals(p.name, excludeName, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)))
            return "A profile with this name already exists.";
        return null;
    }
}

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

internal static class DaemonService
{
    const string MutexName = @"Global\ShortcutHook";

    public static bool IsRunning()
    {
        try
        {
            using var m = Mutex.OpenExisting(MutexName);
            return true;
        }
        catch { return false; }
    }

    public static void Start()
    {
        var daemon = InstallService.DaemonPath;
        if (!File.Exists(daemon))
            throw new FileNotFoundException("ShortcutHookDaemon.exe is not installed.", daemon);
        var psi = new ProcessStartInfo(daemon)
        {
            WorkingDirectory = InstallService.ScriptRoot,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        Process.Start(psi);
    }

    public static void Stop()
    {
        foreach (var p in Process.GetProcessesByName("ShortcutHookDaemon"))
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch { }
            p.Dispose();
        }
    }
}

internal static class StartupService
{
    static string LnkPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "ShortcutHook.lnk");

    public static bool IsEnabled() => File.Exists(LnkPath());

    public static void Set(bool enable)
    {
        var lnk = LnkPath();
        if (enable)
        {
            var daemon = InstallService.DaemonPath;
            if (!File.Exists(daemon))
                throw new FileNotFoundException("ShortcutHookDaemon.exe is not installed.", daemon);
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell unavailable");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic s = shell.CreateShortcut(lnk);
            s.TargetPath       = daemon;
            s.Arguments        = "";
            s.WorkingDirectory = InstallService.ScriptRoot;
            s.WindowStyle      = 0; // hidden — no console window on startup
            s.Save();
        }
        else
        {
            if (File.Exists(lnk)) File.Delete(lnk);
        }
    }
}

internal static class AppScanner
{
    // Resolves a .lnk shortcut to the process executable name it launches (e.g. "chrome.exe").
    // Returns null for shortcuts with no resolvable file target (e.g. UWP "shell:AppsFolder\..." links).
    public static string? ResolveProcessName(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath as string ?? "";
            if (string.IsNullOrWhiteSpace(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return null;
            return Path.GetFileName(target);
        }
        catch { return null; }
    }

    public static List<AppEntry> Scan()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<AppEntry>();
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Microsoft", "Windows", "Start Menu", "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                         "Microsoft", "Windows", "Start Menu", "Programs"),
        };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (name.IndexOf("uninst",    StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (name.IndexOf("remove",    StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!seen.Add(name)) continue;
                list.Add(new AppEntry { Name = name, Path = f });
            }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }
}

internal static class UpdateCheckService
{
    const string ReleasesApiUrl = "https://api.github.com/repos/veera-bharath/ShortcutHook/releases/latest";

    public readonly record struct UpdateInfo(string Tag, Version Version, string HtmlUrl);

    // Queries the GitHub Releases API for the latest release and returns it if its
    // version is newer than currentVersion. Returns null on any failure (offline,
    // rate-limited, malformed response) or if already up to date — never throws,
    // so callers can fire-and-forget this without delaying startup.
    public static async Task<UpdateInfo?> CheckForUpdateAsync(Version currentVersion)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ShortcutHookUI");

            using var resp = await http.GetAsync(ReleasesApiUrl);
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            var url = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url)) return null;

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;

            return latest > currentVersion ? new UpdateInfo(tag, latest, url) : null;
        }
        catch
        {
            return null;
        }
    }
}
