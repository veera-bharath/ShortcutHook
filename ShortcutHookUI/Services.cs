using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ShortcutHookUI;

internal static class TriggerHelpers
{
    public const int MOD_CTRL  = 1;
    public const int MOD_SHIFT = 2;
    public const int MOD_ALT   = 4;
    public const int MOD_WIN   = 8;

    public static readonly string[] ValidGestures = {
        "left+right","left+rightx2","left+rightx3",
        "double-right","double-right-sel","triple-right",
        "right-scroll-down","right-scroll-up",
        "shift-scroll-down","shift-scroll-up",
        "single-wheel","double-wheel","triple-wheel"
    };

    public static readonly Dictionary<string,int> VkMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTER"]=0x0D, ["RETURN"]=0x0D, ["ESC"]=0x1B, ["ESCAPE"]=0x1B,
        ["TAB"]=0x09, ["SPACE"]=0x20, ["BACK"]=0x08, ["BACKSPACE"]=0x08,
        ["DELETE"]=0x2E, ["DEL"]=0x2E, ["INSERT"]=0x2D, ["INS"]=0x2D,
        ["HOME"]=0x24, ["END"]=0x23, ["PGUP"]=0x21, ["PAGEUP"]=0x21,
        ["PGDN"]=0x22, ["PAGEDOWN"]=0x22,
        ["LEFT"]=0x25, ["UP"]=0x26, ["RIGHT"]=0x27, ["DOWN"]=0x28,
        ["PRTSCR"]=0x2C, ["PRINTSCREEN"]=0x2C,
        ["F1"]=0x70, ["F2"]=0x71, ["F3"]=0x72, ["F4"]=0x73, ["F5"]=0x74, ["F6"]=0x75,
        ["F7"]=0x76, ["F8"]=0x77, ["F9"]=0x78, ["F10"]=0x79, ["F11"]=0x7A, ["F12"]=0x7B,
    };

    static readonly HashSet<string> Mods =
        new(StringComparer.OrdinalIgnoreCase) { "CTRL","CONTROL","SHIFT","ALT","MENU","WIN" };

    public static int ResolveKeyCode(string k)
    {
        var u = k.Trim().ToUpperInvariant();
        if (VkMap.TryGetValue(u, out var v)) return v;
        if (u.Length == 1 && u[0] >= 'A' && u[0] <= 'Z') return u[0];
        if (u.Length == 1 && u[0] >= '0' && u[0] <= '9') return u[0];
        throw new ArgumentException($"Unknown key '{k}'");
    }

    public static ParsedKey ParseKeyTrigger(string combo)
    {
        var p = new ParsedKey();
        var keys = new List<int>();
        foreach (var tokRaw in combo.Split('+'))
        {
            var tok = tokRaw.Trim().ToUpperInvariant();
            if (tok.Length == 0) continue;
            if (tok is "CTRL" or "CONTROL") p.Mods |= MOD_CTRL;
            else if (tok is "SHIFT")        p.Mods |= MOD_SHIFT;
            else if (tok is "ALT" or "MENU") p.Mods |= MOD_ALT;
            else if (tok is "WIN")          p.Mods |= MOD_WIN;
            else keys.Add(ResolveKeyCode(tok));
        }
        if (keys.Count == 0) throw new ArgumentException($"No non-modifier key in '{combo}'");
        keys.Sort();
        p.Keys = keys.ToArray();
        return p;
    }

    public static string CanonicalizeTrigger(string trigger)
    {
        var t = trigger.Trim();
        if (t.StartsWith("mouse:", StringComparison.Ordinal))
        {
            var g = t.Substring(6).Trim().ToLowerInvariant();
            if (!ValidGestures.Contains(g)) throw new ArgumentException($"Unknown mouse gesture '{g}'");
            return "mouse:" + g;
        }
        if (t.StartsWith("key:", StringComparison.Ordinal))
        {
            var p = ParseKeyTrigger(t.Substring(4));
            // Restrict single-letter Ctrl triggers (Ctrl + A-Z) to prevent conflicts with standard shortcuts
            if (p.Mods == MOD_CTRL && p.Keys.Length == 1 && p.Keys[0] >= 0x41 && p.Keys[0] <= 0x5A)
            {
                throw new ArgumentException("Triggers like 'Ctrl+Letter' are restricted because they conflict with standard shortcuts. Please use a multi-key combo (e.g. Ctrl+K+C) or add another modifier (e.g. Ctrl+Shift+C).");
            }
            return "key:" + p.Mods + ":" + string.Join(",", p.Keys);
        }
        if (t.StartsWith("launch:", StringComparison.Ordinal) || t.StartsWith("exit:", StringComparison.Ordinal) ||
            t.StartsWith("focus:", StringComparison.Ordinal)  || t.StartsWith("blur:", StringComparison.Ordinal))
        {
            string prefix =
                t.StartsWith("launch:", StringComparison.Ordinal) ? "launch:" :
                t.StartsWith("exit:",   StringComparison.Ordinal) ? "exit:"   :
                t.StartsWith("focus:",  StringComparison.Ordinal) ? "focus:"  : "blur:";
            var apps = t.Substring(prefix.Length).Split(',')
                .Select(a => a.Trim().ToLowerInvariant())
                .Where(a => a.Length > 0)
                .Distinct()
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToArray();
            if (apps.Length == 0) throw new ArgumentException("App name cannot be empty");
            return prefix + string.Join(",", apps);
        }
        throw new ArgumentException("Trigger must start with 'mouse:', 'key:', 'launch:', 'exit:', 'focus:', or 'blur:'");
    }

    public static void ValidateShortcutOutput(string combo)
    {
        var tokens = combo.Trim().Split('+').Select(t => t.Trim().ToUpperInvariant()).ToArray();
        if (tokens.All(string.IsNullOrEmpty)) throw new ArgumentException("Output cannot be empty");
        foreach (var tok in tokens)
        {
            if (string.IsNullOrEmpty(tok)) throw new ArgumentException("Empty token");
            if (Mods.Contains(tok)) continue;
            if (VkMap.ContainsKey(tok)) continue;
            if (tok.Length == 1 && ((tok[0] >= 'A' && tok[0] <= 'Z') || (tok[0] >= '0' && tok[0] <= '9'))) continue;
            throw new ArgumentException($"Unknown key '{tok}'");
        }
    }

    public static bool IsKeyPrefixOf(ParsedKey a, ParsedKey b)
    {
        if (a.Mods != b.Mods || a.Keys.Length >= b.Keys.Length) return false;
        foreach (var k in a.Keys) if (!b.Keys.Contains(k)) return false;
        return true;
    }
}

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
                    return Migrate(doc, root);
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
        File.WriteAllText(ConfigPath(root), JsonSerializer.Serialize(config, JsonOpts));
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
        var export = new ProfileEntry { name = profile.name, bindings = profile.bindings };
        File.WriteAllText(path, JsonSerializer.Serialize(export, JsonOpts));
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
            try { TriggerHelpers.CanonicalizeTrigger(b.trigger); }
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
    const string RegistryKeyPath      = @"Software\ShortcutHook";
    const string AppInstallPathValue  = "AppInstallPath";
    const string SetupCompleteValue   = "SetupComplete";
    const string DismissedUpdateValue = "DismissedUpdateVersion";
    const string ScriptResourceName   = "ShortcutHookUI.Runtime.ShortcutHook.ps1";
    const string ScriptFileName       = "ShortcutHook.ps1";
    const string UiExeFileName        = "ShortcutHookUI.exe";

    // Script + config always live here — fixed, never changes.
    public static readonly string ScriptRoot = @"C:\Tools\ShortcutHook";
    public static string ScriptPath => Path.Combine(ScriptRoot, ScriptFileName);

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

    // Script check uses fixed ScriptRoot — no arg needed.
    public static bool IsInstalled() => File.Exists(ScriptPath);

    // Exe check uses the user-chosen app folder.
    public static bool IsAppInstalled(string appRoot) => File.Exists(UiExePath(appRoot));

    public static void Install(string appRoot)
    {
        // 1. Extract embedded PS1 to fixed script location.
        Directory.CreateDirectory(ScriptRoot);
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ScriptResourceName)
               ?? throw new InvalidOperationException("Embedded daemon script not found."))
        using (var file = File.Create(ScriptPath))
        {
            stream.CopyTo(file);
        }

        // 2. Copy this exe to the chosen app folder (skip if already there).
        Directory.CreateDirectory(appRoot);
        var sourceExe = CurrentExecutablePath();
        var targetExe = UiExePath(appRoot);
        if (!string.Equals(Path.GetFullPath(sourceExe), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourceExe, targetExe, overwrite: true);

        // 3. Write default config if absent.
        if (!File.Exists(ConfigService.ConfigPath(ScriptRoot)))
            ConfigService.Save(ScriptRoot, ConfigService.DefaultConfig());

        SaveAppRoot(appRoot);
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
        var ps = InstallService.ScriptPath;
        if (!File.Exists(ps))
            throw new FileNotFoundException("ShortcutHook.ps1 is not installed.", ps);
        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{ps}\"",
            WorkingDirectory = InstallService.ScriptRoot,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        Process.Start(psi);
    }

    public static void Stop()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'powershell.exe'");
            foreach (var obj in searcher.Get())
            {
                var cmd = obj["CommandLine"] as string ?? "";
                if (cmd.IndexOf("ShortcutHook.ps1", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    cmd.IndexOf("ShortcutHookUI",   StringComparison.OrdinalIgnoreCase) < 0)
                {
                    var pid = Convert.ToInt32(obj["ProcessId"]);
                    try { Process.GetProcessById(pid).Kill(); } catch { }
                }
            }
        }
        catch { }
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
            var ps = InstallService.ScriptPath;
            if (!File.Exists(ps))
                throw new FileNotFoundException("ShortcutHook.ps1 is not installed.", ps);
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell unavailable");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic s = shell.CreateShortcut(lnk);
            s.TargetPath       = "powershell.exe";
            s.Arguments        = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{ps}\"";
            s.WorkingDirectory = InstallService.ScriptRoot;
            s.WindowStyle      = 7;
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
