using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShortcutHookCore.Models;
using ShortcutHookCore.Parsing;
using ShortcutHookCore.Security;

namespace ShortcutHookDaemon;

public static class ConfigLoader
{
    // Output modifier map — mirrors $outModMap in ShortcutHook.ps1
    static readonly Dictionary<string, byte> OutModMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WIN"]     = 0x5B, ["LWIN"]    = 0x5B, ["RWIN"]    = 0x5C,
        ["SHIFT"]   = 0x10, ["LSHIFT"]  = 0xA0, ["RSHIFT"]  = 0xA1,
        ["CTRL"]    = 0x11, ["CONTROL"] = 0x11, ["LCTRL"]   = 0xA2, ["RCTRL"]   = 0xA3,
        ["ALT"]     = 0x12, ["MENU"]    = 0x12,
    };

    // Resolves an output chord string to byte[] (mirrors Resolve-OutputChord in PS1)
    public static byte[] ResolveOutputChord(string combo)
    {
        var tokens = combo.Split('+');
        var result = new List<byte>();
        foreach (var tokRaw in tokens)
        {
            var tok = tokRaw.Trim().ToUpperInvariant();
            if (tok.Length == 0) continue;
            if (OutModMap.TryGetValue(tok, out var modByte))
            {
                result.Add(modByte);
            }
            else if (KeyResolver.VkMap.TryGetValue(tok, out var vkByte))
            {
                result.Add(vkByte);
            }
            else if (tok.Length == 1 && tok[0] >= 'A' && tok[0] <= 'Z')
            {
                result.Add((byte)tok[0]);
            }
            else if (tok.Length == 1 && tok[0] >= '0' && tok[0] <= '9')
            {
                result.Add((byte)(0x30 + (tok[0] - '0')));
            }
            else
            {
                throw new ArgumentException($"Unknown key '{tok}' in '{combo}'");
            }
        }
        return result.ToArray();
    }

    // Default bindings used when shortcuts.json is absent or invalid
    static ShortcutHook.Binding[] DefaultBindings()
    {
        return new[]
        {
            MakeMouseBinding("left+right",   ResolveOutputChord("Win+Shift+S")),
            MakeMouseBinding("double-right", ResolveOutputChord("Ctrl+C")),
            MakeMouseBinding("triple-right", ResolveOutputChord("Ctrl+V")),
        };
    }

    static ShortcutHook.Binding MakeMouseBinding(string gesture, byte[] chord)
    {
        return new ShortcutHook.Binding
        {
            Kind         = "mouse",
            MouseGesture = gesture,
            Steps        = new[] { new ShortcutHook.ChainStep { Output = chord } },
        };
    }

    // Reads shortcuts.json and returns (bindings, activeProfileName, altHScroll).
    public static (ShortcutHook.Binding[] bindings, string profileName, bool altHScroll) LoadFromConfig(string configPath)
    {
        if (!File.Exists(configPath))
            return (DefaultBindings(), "Default", false);

        ConfigRoot? cfg;
        try
        {
            var json = File.ReadAllText(configPath);
            cfg = JsonSerializer.Deserialize<ConfigRoot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return (DefaultBindings(), "Default", false);
        }

        if (cfg == null)
            return (DefaultBindings(), "Default", false);

        // Load ignoredApps
        if (cfg.ignoredApps != null)
        {
            foreach (var app in cfg.ignoredApps)
                if (!string.IsNullOrWhiteSpace(app))
                    ShortcutHook.IgnoredApps.Add(app);
        }

        // Find active profile
        List<BindingEntry>? rawBindings = null;
        string profileName = "Default";

        if (cfg.profiles != null && cfg.profiles.Count > 0)
        {
            profileName = cfg.activeProfile ?? cfg.profiles[0].name ?? "Default";
            ProfileEntry? active = null;
            foreach (var p in cfg.profiles)
            {
                if (string.Equals(p.name, profileName, StringComparison.Ordinal))
                {
                    active = p;
                    break;
                }
            }
            if (active == null) active = cfg.profiles[0];
            rawBindings = active.bindings;
        }
        else if (cfg.bindings != null && cfg.bindings.Count > 0)
        {
            // Old flat format
            profileName = "Default";
            rawBindings = cfg.bindings;
        }

        if (rawBindings == null || rawBindings.Count == 0)
            return (DefaultBindings(), profileName, cfg.altHScroll);

        var built = new List<ShortcutHook.Binding>();
        foreach (var b in rawBindings)
        {
            try
            {
                if (b.enabled == false) continue;
                if (string.IsNullOrWhiteSpace(b.trigger)) continue;

                var nb = new ShortcutHook.Binding();
                var trigger = b.trigger!.Trim();

                if (trigger.StartsWith("mouse:", StringComparison.Ordinal))
                {
                    var g = trigger.Substring(6).Trim().ToLowerInvariant();
                    if (!TriggerParser.ValidGestures.Contains(g)) continue;
                    nb.Kind = "mouse";
                    nb.MouseGesture = g;
                }
                else if (trigger.StartsWith("key:", StringComparison.Ordinal))
                {
                    var parsed = TriggerParser.ParseKeyTrigger(trigger.Substring(4));
                    nb.Kind      = "key";
                    nb.Mods      = parsed.Mods;
                    nb.Keys      = parsed.Keys.Select(k => (byte)k).ToArray();
                    nb.Signature = ShortcutHook.MakeSignature(parsed.Mods, nb.Keys);
                }
                else if (trigger.StartsWith("launch:", StringComparison.Ordinal))
                {
                    var appNames = ParseAppNames(trigger.Substring(7));
                    if (appNames.Length == 0) continue;
                    nb.Kind     = "launch";
                    nb.AppNames = appNames;
                }
                else if (trigger.StartsWith("exit:", StringComparison.Ordinal))
                {
                    var appNames = ParseAppNames(trigger.Substring(5));
                    if (appNames.Length == 0) continue;
                    nb.Kind     = "exit";
                    nb.AppNames = appNames;
                }
                else if (trigger.StartsWith("focus:", StringComparison.Ordinal))
                {
                    var appNames = ParseAppNames(trigger.Substring(6));
                    if (appNames.Length == 0) continue;
                    nb.Kind     = "focus";
                    nb.AppNames = appNames;
                }
                else if (trigger.StartsWith("blur:", StringComparison.Ordinal))
                {
                    var appNames = ParseAppNames(trigger.Substring(5));
                    if (appNames.Length == 0) continue;
                    nb.Kind     = "blur";
                    nb.AppNames = appNames;
                }
                else
                {
                    continue; // unknown trigger prefix
                }

                // Resolve outputs — prefer 'outputs' array, fall back to legacy 'output'
                List<string>? outputsList = null;
                if (b.outputs != null && b.outputs.Count > 0)
                    outputsList = b.outputs;
                else if (!string.IsNullOrWhiteSpace(b.output))
                    outputsList = new List<string> { b.output! };

                if (outputsList == null || outputsList.Count == 0) continue;

                var steps = new List<ShortcutHook.ChainStep>();
                foreach (var outStr in outputsList)
                {
                    if (string.IsNullOrWhiteSpace(outStr)) continue;
                    var step = new ShortcutHook.ChainStep();

                    if (outStr.StartsWith("open:", StringComparison.Ordinal))
                    {
                        step.OpenPath = outStr.Substring(5).Trim();
                    }
                    else if (outStr.StartsWith("cmdw:", StringComparison.Ordinal))
                    {
                        step.CmdLine = outStr.Substring(5).Trim();
                        step.CmdShow = true;
                    }
                    else if (outStr.StartsWith("cmd:", StringComparison.Ordinal))
                    {
                        // Strip any accidental double "cmd:" prefix (mirrors PS1 quirk)
                        var cmdVal = outStr.Substring(4).Trim();
                        if (cmdVal.StartsWith("cmd:", StringComparison.Ordinal))
                            cmdVal = cmdVal.Substring(4).Trim();
                        step.CmdLine = cmdVal;
                        step.CmdShow = false;
                    }
                    else if (outStr.StartsWith("hscroll:", StringComparison.Ordinal))
                    {
                        var dir = outStr.Substring(8).Trim().ToLowerInvariant();
                        step.IsHScroll    = true;
                        step.HScrollDelta = dir == "left" ? -120 : 120;
                    }
                    else if (string.Equals(outStr.Trim(), "toggle:pause", StringComparison.OrdinalIgnoreCase))
                    {
                        step.IsTogglePause = true;
                    }
                    else if (outStr.StartsWith("profile:", StringComparison.Ordinal))
                    {
                        step.SwitchToProfile = outStr.Substring(8).Trim();
                    }
                    else if (outStr.StartsWith("type-enc:", StringComparison.Ordinal))
                    {
                        step.TypeText = DpapiHelper.Decrypt(outStr.Substring(9));
                    }
                    else if (outStr.StartsWith("type:", StringComparison.Ordinal))
                    {
                        step.TypeText = outStr.Substring(5); // preserve whitespace
                    }
                    else
                    {
                        step.Output = ResolveOutputChord(outStr);
                    }
                    steps.Add(step);
                }

                if (steps.Count == 0) continue;
                nb.Steps = steps.ToArray();

                if (b.outputDelay > 0) nb.OutputDelay = b.outputDelay;

                // apps[] takes precedence over legacy app string
                if (b.apps != null && b.apps.Count > 0)
                    nb.Apps = ParseAppNames(string.Join(",", b.apps));
                else if (!string.IsNullOrWhiteSpace(b.app))
                    nb.Apps = new[] { b.app!.Trim() };

                if (b.exceptApps != null && b.exceptApps.Count > 0)
                    nb.ExceptApps = ParseAppNames(string.Join(",", b.exceptApps));

                if (b.debounce)   nb.Debounce  = true;
                if (b.showToast)
                {
                    nb.ShowToast = true;
                    // Toast text: extract the part after the prefix (mouse: or key:)
                    var trig = b.trigger!;
                    int colon = trig.IndexOf(':');
                    nb.ToastText = colon >= 0 && colon < trig.Length - 1
                        ? trig.Substring(colon + 1)
                        : trig;
                }

                built.Add(nb);
            }
            catch
            {
                // Skip malformed binding, same as PS1
            }
        }

        return (built.ToArray(), profileName, cfg.altHScroll);
    }

    static string[] ParseAppNames(string csv)
    {
        var parts = csv.Split(',');
        var result = new List<string>();
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length > 0) result.Add(t);
        }
        return result.ToArray();
    }
}
