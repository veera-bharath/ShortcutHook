using System;

namespace ShortcutHookCore.Enums;

[Flags]
public enum ModifierFlags : byte
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Win = 8
}
