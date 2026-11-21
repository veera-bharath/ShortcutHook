using System;
using System.Collections.Generic;
using System.Linq;
using ShortcutHookCore.Enums;
using ShortcutHookCore.Models;

namespace ShortcutHookCore.Parsing;

public static class TriggerParser
{
    public const int MOD_CTRL  = (int)ModifierFlags.Control;
    public const int MOD_SHIFT = (int)ModifierFlags.Shift;
    public const int MOD_ALT   = (int)ModifierFlags.Alt;
    public const int MOD_WIN   = (int)ModifierFlags.Win;

    public static readonly string[] ValidGestures = {
        "left+right", "left+rightx2", "left+rightx3",
        "double-right", "double-right-sel", "triple-right",
        "right-scroll-down", "right-scroll-up",
        "shift-scroll-down", "shift-scroll-up",
        "ctrl-shift-scroll-down", "ctrl-shift-scroll-up",
        "alt-scroll-down", "alt-scroll-up",
        "single-wheel", "double-wheel", "triple-wheel"
    };

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
            else keys.Add(KeyResolver.ResolveKeyCode(tok));
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

    public static bool IsKeyPrefixOf(ParsedKey a, ParsedKey b)
    {
        if (a.Mods != b.Mods || a.Keys.Length >= b.Keys.Length) return false;
        foreach (var k in a.Keys) if (!b.Keys.Contains(k)) return false;
        return true;
    }
}
