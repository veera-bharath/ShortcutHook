using System;
using System.Runtime.InteropServices;

using ShortcutHookCore.Parsing;

namespace ShortcutHookUI;

internal static class DwmApi
{
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_CAPTION_COLOR          = 19;
}

internal static class HookApi
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN     = 0x0100;
    public const int WM_KEYUP       = 0x0101;
    public const int WM_SYSKEYDOWN  = 0x0104;
    public const int WM_SYSKEYUP    = 0x0105;
    public const uint LLKHF_INJECTED = 0x10;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);
}

internal static class HotkeyProbe
{
    const uint FS_ALT     = 0x0001;
    const uint FS_CONTROL = 0x0002;
    const uint FS_SHIFT   = 0x0004;
    const uint FS_WIN     = 0x0008;
    const int  PROBE_ID   = 0xBEEF;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    static uint ToFsMods(int mods)
    {
        uint fs = 0;
        if ((mods & TriggerParser.MOD_CTRL)  != 0) fs |= FS_CONTROL;
        if ((mods & TriggerParser.MOD_SHIFT) != 0) fs |= FS_SHIFT;
        if ((mods & TriggerParser.MOD_ALT)   != 0) fs |= FS_ALT;
        if ((mods & TriggerParser.MOD_WIN)   != 0) fs |= FS_WIN;
        return fs;
    }

    // Probes {mods, vk} via RegisterHotKey dry-run. Must be called from the UI thread.
    // Returns true if the combo is already claimed by Windows or another app.
    public static bool IsConflicted(int mods, int vk)
    {
        try
        {
            bool ok = RegisterHotKey(IntPtr.Zero, PROBE_ID, ToFsMods(mods), (uint)vk);
            if (ok) UnregisterHotKey(IntPtr.Zero, PROBE_ID);
            return !ok;
        }
        catch { return false; }
    }
}
