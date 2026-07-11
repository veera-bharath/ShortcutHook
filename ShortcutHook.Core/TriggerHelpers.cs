using System;
using System.Collections.Generic;
using System.Linq;

namespace ShortcutHookCore;

public static class TriggerHelpers
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

    public static readonly Dictionary<string, byte> VkMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTER"]       = (byte)VirtualKeys.Enter,
        ["RETURN"]      = (byte)VirtualKeys.Return,
        ["ESC"]         = (byte)VirtualKeys.Esc,
        ["ESCAPE"]      = (byte)VirtualKeys.Escape,
        ["TAB"]         = (byte)VirtualKeys.Tab,
        ["SPACE"]       = (byte)VirtualKeys.Space,
        ["BACK"]        = (byte)VirtualKeys.Back,
        ["BACKSPACE"]   = (byte)VirtualKeys.Back,
        ["DELETE"]      = (byte)VirtualKeys.Delete,
        ["DEL"]          = (byte)VirtualKeys.Del,
        ["INSERT"]      = (byte)VirtualKeys.Insert,
        ["INS"]         = (byte)VirtualKeys.Ins,
        ["HOME"]        = (byte)VirtualKeys.Home,
        ["END"]         = (byte)VirtualKeys.End,
        ["PGUP"]        = (byte)VirtualKeys.PgUp,
        ["PAGEUP"]      = (byte)VirtualKeys.PageUp,
        ["PGDN"]        = (byte)VirtualKeys.PgDn,
        ["PAGEDOWN"]    = (byte)VirtualKeys.PageDown,
        ["LEFT"]        = (byte)VirtualKeys.Left,
        ["UP"]          = (byte)VirtualKeys.Up,
        ["RIGHT"]       = (byte)VirtualKeys.Right,
        ["DOWN"]        = (byte)VirtualKeys.Down,
        ["PRTSCR"]      = (byte)VirtualKeys.PrtScr,
        ["PRINTSCREEN"] = (byte)VirtualKeys.PrintScreen,
        ["F1"]          = (byte)VirtualKeys.F1,
        ["F2"]          = (byte)VirtualKeys.F2,
        ["F3"]          = (byte)VirtualKeys.F3,
        ["F4"]          = (byte)VirtualKeys.F4,
        ["F5"]          = (byte)VirtualKeys.F5,
        ["F6"]          = (byte)VirtualKeys.F6,
        ["F7"]          = (byte)VirtualKeys.F7,
        ["F8"]          = (byte)VirtualKeys.F8,
        ["F9"]          = (byte)VirtualKeys.F9,
        ["F10"]         = (byte)VirtualKeys.F10,
        ["F11"]         = (byte)VirtualKeys.F11,
        ["F12"]         = (byte)VirtualKeys.F12,
    };

    private static readonly HashSet<string> Mods =
        new(StringComparer.OrdinalIgnoreCase) { "CTRL", "CONTROL", "SHIFT", "ALT", "MENU", "WIN" };

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
