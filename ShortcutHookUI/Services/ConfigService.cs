using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShortcutHookCore.Models;
using ShortcutHookCore.Parsing;
using ShortcutHookCore.Security;

namespace ShortcutHookUI.Services;

internal static class ConfigService
{
    static readonly JsonSerializerOptions JsonOpts = DpapiHelper.SignJsonOpts;

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

        DpapiHelper.SignConfig(config, root);

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

    public static string SerializeBinding(BindingEntry entry) =>
        JsonSerializer.Serialize(entry, JsonOpts);

    public static BindingEntry ParseBinding(string json)
    {
        var entry = JsonSerializer.Deserialize<BindingEntry>(json.Trim())
            ?? throw new FormatException("Could not parse JSON.");
        if (string.IsNullOrWhiteSpace(entry.trigger))
            throw new FormatException("Missing 'trigger' field.");
        if ((entry.outputs == null || entry.outputs.Count == 0) && entry.output != null)
            entry.outputs = new List<string> { entry.output };
        if (entry.outputs == null || entry.outputs.Count == 0)
            entry.outputs = new List<string> { "" };
        return entry;
    }

    public static void AddBindingToActiveProfile(string root, BindingEntry entry)
    {
        var config = ReadConfig(root);
        GetActiveProfile(config).bindings.Add(entry);
        Save(root, config);
    }

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
            var export = new ProfileEntry { name = profile.name ?? "", bindings = profile.bindings ?? new() };
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
