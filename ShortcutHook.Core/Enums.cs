using System;

namespace ShortcutHookCore;

[Flags]
public enum ModifierFlags : byte
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Win = 8
}

public enum VirtualKeys : byte
{
    Back = 0x08,
    Tab = 0x09,
    Return = 0x0D,
    Enter = 0x0D,
    Escape = 0x1B,
    Esc = 0x1B,
    Space = 0x20,
    PageUp = 0x21,
    PgUp = 0x21,
    PageDown = 0x22,
    PgDn = 0x22,
    End = 0x23,
    Home = 0x24,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    PrintScreen = 0x2C,
    PrtScr = 0x2C,
    Insert = 0x2D,
    Ins = 0x2D,
    Delete = 0x2E,
    Del = 0x2E,
    
    F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75,
    F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B
}
