using System;
using System.Collections.Generic;
using System.Linq;
using ShortcutHookCore.Models;

namespace ShortcutHookUI.Services;

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
