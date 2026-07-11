using System;
using System.Collections.Generic;
using ShortcutHookCore.Enums;

namespace ShortcutHookCore.Parsing;

public static class KeyResolver
{
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

    public static int ResolveKeyCode(string k)
    {
        var u = k.Trim().ToUpperInvariant();
        if (VkMap.TryGetValue(u, out var v)) return v;
        if (u.Length == 1 && u[0] >= 'A' && u[0] <= 'Z') return u[0];
        if (u.Length == 1 && u[0] >= '0' && u[0] <= '9') return u[0];
        throw new ArgumentException($"Unknown key '{k}'");
    }
}
