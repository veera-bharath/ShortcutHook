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
