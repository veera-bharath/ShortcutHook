using System;
using System.Collections.Generic;
using System.IO;
using ShortcutHookCore.Models;

namespace ShortcutHookUI.Services;

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
