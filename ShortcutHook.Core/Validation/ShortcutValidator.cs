using System;
using System.Collections.Generic;
using System.Linq;
using ShortcutHookCore.Parsing;

namespace ShortcutHookCore.Validation;

public static class ShortcutValidator
{
    private static readonly HashSet<string> Mods =
        new(StringComparer.OrdinalIgnoreCase) { "CTRL", "CONTROL", "SHIFT", "ALT", "MENU", "WIN" };

    public static void ValidateShortcutOutput(string combo)
    {
        if (combo.StartsWith("hscroll:", StringComparison.OrdinalIgnoreCase)) return;
        if (combo.StartsWith("volume:", StringComparison.OrdinalIgnoreCase))
        {
            var act = combo.Substring(7).Trim();
            if (string.Equals(act, "up", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act, "down", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act, "mute", StringComparison.OrdinalIgnoreCase)) return;
            throw new ArgumentException($"Unknown volume action '{act}'");
        }
        if (combo.StartsWith("brightness:", StringComparison.OrdinalIgnoreCase))
        {
            var act = combo.Substring(11).Trim();
            if (string.Equals(act, "up", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act, "down", StringComparison.OrdinalIgnoreCase)) return;
            throw new ArgumentException($"Unknown brightness action '{act}'");
        }
        if (combo.StartsWith("media:", StringComparison.OrdinalIgnoreCase))
        {
            var act = combo.Substring(6).Trim();
            if (string.Equals(act, "next", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act, "prev", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act, "previous", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act, "playpause", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act, "play", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act, "pause", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act, "stop", StringComparison.OrdinalIgnoreCase)) return;
            throw new ArgumentException($"Unknown media action '{act}'");
        }
        var tokens = combo.Trim().Split('+').Select(t => t.Trim().ToUpperInvariant()).ToArray();
        if (tokens.All(string.IsNullOrEmpty)) throw new ArgumentException("Output cannot be empty");
        foreach (var tok in tokens)
        {
            if (string.IsNullOrEmpty(tok)) throw new ArgumentException("Empty token");
            if (Mods.Contains(tok)) continue;
            if (KeyResolver.VkMap.ContainsKey(tok)) continue;
            if (tok.Length == 1 && ((tok[0] >= 'A' && tok[0] <= 'Z') || (tok[0] >= '0' && tok[0] <= '9'))) continue;
            throw new ArgumentException($"Unknown key '{tok}'");
        }
    }
}
