# ShortcutHook.ps1  v4.0
# System-wide low-level hook that maps mouse gestures and keyboard combos to
# configurable outputs: keyboard chords OR shell-execute (open app/file/folder).
#
# Mouse gestures: left+right, double-right, triple-right,
#                 right-scroll-down, right-scroll-up, double-wheel, triple-wheel
# Key triggers:   key:Ctrl+S  |  key:Ctrl+Alt+F5  |  key:Ctrl+S+L
#
# Output formats in shortcuts.json:
#   "Win+Shift+S"           keyboard chord
#   "open:C:\path\to\thing" shell-execute (app .lnk, file, or folder)
#
# Requirements: Windows 10/11, PowerShell 5+, .NET Framework 4.5+

$logPath = Join-Path $PSScriptRoot 'ShortcutHook.log'
function Write-Log([string]$msg) {
    "$(Get-Date -f 'yyyy-MM-dd HH:mm:ss')  $msg" | Out-File $logPath -Append -Encoding UTF8
}
Write-Log "=== Start (PS $($PSVersionTable.PSVersion), PID $PID) ==="

try { Add-Type @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public class ShortcutHook {

    private const int WH_MOUSE_LL    = 14;
    private const int WH_KEYBOARD_LL = 13;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP   = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP   = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP   = 0x0208;
    private const int WM_MOUSEWHEEL  = 0x020A;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;

    private const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
    private const uint MOUSEEVENTF_HWHEEL     = 0x1000;
    private const uint KEYEVENTF_KEYUP        = 0x0002;
    private const uint LLKHF_INJECTED         = 0x10;

    private const byte VK_SHIFT   = 0x10;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU    = 0x12;
    private const byte VK_LWIN    = 0x5B;
    private const byte VK_RWIN    = 0x5C;
    private const byte VK_LSHIFT  = 0xA0;
    private const byte VK_RSHIFT  = 0xA1;
    private const byte VK_LCTRL   = 0xA2;
    private const byte VK_RCTRL   = 0xA3;
    private const byte VK_LMENU   = 0xA4;
    private const byte VK_RMENU   = 0xA5;

    public const int MOD_CTRL  = 1;
    public const int MOD_SHIFT = 2;
    public const int MOD_ALT   = 4;
    public const int MOD_WIN   = 8;

    [DllImport("user32.dll", SetLastError=true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LLProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll", SetLastError=true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")]
    static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")]
    static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG {
        public IntPtr hwnd;  public uint message; public UIntPtr wParam;
        public IntPtr lParam; public uint time;   public int ptX, ptY;
    }
    [DllImport("user32.dll")] static extern int    GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool   TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT {
        public uint vkCode; public uint scanCode; public uint flags;
        public uint time;   public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT {
        public int  ptX, ptY;
        public uint mouseData; public uint flags;
        public uint time;      public IntPtr dwExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr LLProc(int nCode, IntPtr wParam, IntPtr lParam);

    // ---------- Binding ----------
    public class Binding {
        public string Kind;          // "mouse" | "key"
        public string MouseGesture;
        public int    Mods;
        public byte[] Keys;
        public byte[] Output;        // keyboard chord; null when OpenPath is set
        public string OpenPath;      // shell-execute target; null when Output is set
        public string Signature;
    }

    public static List<Binding> MouseBindings = new List<Binding>();
    public static List<Binding> KeyBindings   = new List<Binding>();
    public static Dictionary<string, Binding> KeySigIndex = new Dictionary<string, Binding>();

    public static bool altScrollEnabled = false;

    // One Binding ref per mouse gesture (null = not configured)
    public static Binding BLeftRight, BDoubleRight, BTripleRight;
    public static Binding BRightScrollDown, BRightScrollUp;
    public static Binding BDoubleWheel, BTripleWheel;

    public static string MakeSignature(int mods, byte[] sortedKeys) {
        string[] parts = new string[sortedKeys.Length];
        for (int i = 0; i < sortedKeys.Length; i++) parts[i] = ((int)sortedKeys[i]).ToString();
        return mods + ":" + String.Join(",", parts);
    }

    public static void LoadBindings(Binding[] bindings) {
        MouseBindings.Clear(); KeyBindings.Clear(); KeySigIndex.Clear();
        BLeftRight = BDoubleRight = BTripleRight = null;
        BRightScrollDown = BRightScrollUp = null;
        BDoubleWheel = BTripleWheel = null;
        foreach (Binding b in bindings) {
            if (b.Kind == "mouse") {
                MouseBindings.Add(b);
                switch (b.MouseGesture) {
                    case "left+right":        BLeftRight       = b; break;
                    case "double-right":      BDoubleRight     = b; break;
                    case "triple-right":      BTripleRight     = b; break;
                    case "right-scroll-down": BRightScrollDown = b; break;
                    case "right-scroll-up":   BRightScrollUp   = b; break;
                    case "double-wheel":      BDoubleWheel     = b; break;
                    case "triple-wheel":      BTripleWheel     = b; break;
                }
            } else if (b.Kind == "key") {
                KeyBindings.Add(b);
                KeySigIndex[b.Signature] = b;
            }
        }
    }

    // ---------- Output dispatch ----------
    static void ExecuteBinding(Binding b) {
        if (b == null) return;
        if (b.OpenPath != null) {
            bool uWinL = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0;
            bool uWinR = (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
            // Don't inject a synthetic Win-up here — that itself triggers Start Menu.
            // Instead mark that we need to release Win at physical Win-up time, where
            // we can sandwich it between Ctrl events to break the clean-tap sequence.
            if (uWinL || uWinR) lock (KLock) { suppressWinUp = true; releaseWinOnSuppress = true; }
            string path = b.OpenPath;
            new Thread(() => {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch { }
            }) { IsBackground = true }.Start();
        } else {
            FireOutput(b.Output);
        }
    }

    static void FireOutput(byte[] chord) {
        if (chord == null || chord.Length == 0) return;
        bool uCtrl  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool uShift = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;
        bool uAlt   = (GetAsyncKeyState(VK_MENU)    & 0x8000) != 0;
        bool uWinL  = (GetAsyncKeyState(VK_LWIN)    & 0x8000) != 0;
        bool uWinR  = (GetAsyncKeyState(VK_RWIN)    & 0x8000) != 0;

        if (uCtrl)  keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (uShift) keybd_event(VK_SHIFT,   0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (uAlt)   keybd_event(VK_MENU,    0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (uWinL)  keybd_event(VK_LWIN,    0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (uWinR)  keybd_event(VK_RWIN,    0, KEYEVENTF_KEYUP, UIntPtr.Zero);

        foreach (byte k in chord)                    keybd_event(k, 0, 0, UIntPtr.Zero);
        for (int i = chord.Length - 1; i >= 0; i--) keybd_event(chord[i], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

        // Re-press non-Win modifiers so the user's held keys remain active
        if (uAlt)   keybd_event(VK_MENU,    0, 0, UIntPtr.Zero);
        if (uShift) keybd_event(VK_SHIFT,   0, 0, UIntPtr.Zero);
        if (uCtrl)  keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);

        // Don't re-press Win — instead swallow the physical Win-up in KbdCallback
        // so Explorer never sees a clean Win tap and the Start Menu stays closed.
        if (uWinL || uWinR) lock (KLock) { suppressWinUp = true; }
        else { if (uWinR) keybd_event(VK_RWIN, 0, 0, UIntPtr.Zero); if (uWinL) keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero); }
    }

    // =========================================================================
    // Mouse hook
    // =========================================================================
    static IntPtr mouseHookId;
    static LLProc mouseProc;

    static readonly object MLock = new object();
    static volatile bool leftDown = false;
    static bool rightHeld       = false;
    static bool rightPending    = false;
    static bool rightUpSeen     = false;
    static bool suppressRightUp = false;
    static int  mGeneration     = 0;
    static int  rightClickCount = 0;
    static int  lastRightTick   = 0;
    static int  dblClickMs      = 500;
    static int  reinjDown       = 0;
    static int  reinjUp         = 0;

    static bool wheelHeld       = false;
    static bool wheelPending    = false;
    static bool wheelUpSeen     = false;
    static int  wGeneration     = 0;
    static int  wheelClickCount = 0;
    static int  lastWheelTick   = 0;
    static int  reinjWheelDown  = 0;
    static int  reinjWheelUp    = 0;

    static void Reinject(bool withUp) {
        Interlocked.Increment(ref reinjDown);
        mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
        if (withUp) { Interlocked.Increment(ref reinjUp); mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero); }
    }

    static void ReinjectWheel(bool withUp) {
        Interlocked.Increment(ref reinjWheelDown);
        mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, UIntPtr.Zero);
        if (withUp) { Interlocked.Increment(ref reinjWheelUp); mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero); }
    }

    static IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam) {
        if (nCode < 0) return CallNextHookEx(mouseHookId, nCode, wParam, lParam);
        try {
            int msg = wParam.ToInt32();

            if (msg == WM_RBUTTONDOWN && reinjDown      > 0) { Interlocked.Decrement(ref reinjDown);      return CallNextHookEx(mouseHookId, nCode, wParam, lParam); }
            if (msg == WM_RBUTTONUP   && reinjUp        > 0) { Interlocked.Decrement(ref reinjUp);        return CallNextHookEx(mouseHookId, nCode, wParam, lParam); }
            if (msg == WM_MBUTTONDOWN && reinjWheelDown > 0) { Interlocked.Decrement(ref reinjWheelDown); return CallNextHookEx(mouseHookId, nCode, wParam, lParam); }
            if (msg == WM_MBUTTONUP   && reinjWheelUp   > 0) { Interlocked.Decrement(ref reinjWheelUp);   return CallNextHookEx(mouseHookId, nCode, wParam, lParam); }

            if (msg == WM_LBUTTONDOWN) {
                leftDown = true;
                lock (MLock) {
                    if (rightPending) {
                        rightPending = false; rightClickCount = 0;
                        suppressRightUp = true; leftDown = false;
                        ExecuteBinding(BLeftRight);
                    }
                }
            }
            else if (msg == WM_LBUTTONUP) { leftDown = false; }
            else if (msg == WM_RBUTTONDOWN) {
                lock (MLock) {
                    int myGen = ++mGeneration;
                    rightHeld = true; suppressRightUp = false;

                    if (leftDown) {
                        rightClickCount = 0; rightPending = false; rightUpSeen = false;
                        suppressRightUp = true; leftDown = false;
                        ExecuteBinding(BLeftRight);
                        return new IntPtr(1);
                    }

                    bool hadRightUp = rightUpSeen; rightUpSeen = false;
                    int  nowTick    = Environment.TickCount;
                    bool inSequence = hadRightUp && (nowTick - lastRightTick) <= dblClickMs;
                    lastRightTick   = nowTick;
                    rightClickCount = inSequence ? rightClickCount + 1 : 1;
                    rightPending    = true;

                    new Thread(() => {
                        Thread.Sleep(dblClickMs);
                        lock (MLock) {
                            if (myGen != mGeneration || !rightPending) return;
                            rightPending = false;
                            int count = rightClickCount; rightClickCount = 0;
                            if      (count == 1) Reinject(!rightHeld || rightUpSeen);
                            else if (count == 2) { if (rightHeld) suppressRightUp = true; ExecuteBinding(BDoubleRight); }
                            else                 { if (rightHeld) suppressRightUp = true; ExecuteBinding(BTripleRight); }
                        }
                    }) { IsBackground = true }.Start();
                }
                return new IntPtr(1);
            }
            else if (msg == WM_RBUTTONUP) {
                lock (MLock) {
                    rightHeld = false;
                    if (suppressRightUp) { suppressRightUp = false; return new IntPtr(1); }
                    if (rightPending)    { rightUpSeen = true;       return new IntPtr(1); }
                }
            }
            else if (msg == WM_MOUSEWHEEL) {
                lock (MLock) {
                    if (rightHeld) {
                        MSLLHOOKSTRUCT ms = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        short delta = (short)((ms.mouseData >> 16) & 0xFFFF);
                        Binding b = delta < 0 ? BRightScrollDown : BRightScrollUp;
                        if (b != null) {
                            mGeneration++; rightPending = false; suppressRightUp = true;
                            ExecuteBinding(b);
                            return new IntPtr(1);
                        }
                    } else if (altScrollEnabled && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0) {
                        MSLLHOOKSTRUCT ms = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        short delta = (short)((ms.mouseData >> 16) & 0xFFFF);
                        int d = (int)delta;
                        new Thread(() => {
                            try { mouse_event(MOUSEEVENTF_HWHEEL, 0, 0, (uint)d, UIntPtr.Zero); }
                            catch { }
                        }) { IsBackground = true }.Start();
                        return new IntPtr(1);
                    }
                }
            }
            else if (msg == WM_MBUTTONDOWN) {
                lock (MLock) {
                    int myGen = ++wGeneration;
                    wheelHeld = true; wheelUpSeen = false;
                    int  nowTick    = Environment.TickCount;
                    bool inSequence = (nowTick - lastWheelTick) <= dblClickMs;
                    lastWheelTick   = nowTick;
                    wheelClickCount = inSequence ? wheelClickCount + 1 : 1;
                    wheelPending    = true;

                    new Thread(() => {
                        Thread.Sleep(dblClickMs);
                        lock (MLock) {
                            if (myGen != wGeneration || !wheelPending) return;
                            wheelPending = false;
                            int count = wheelClickCount; wheelClickCount = 0;
                            if      (count == 1) ReinjectWheel(!wheelHeld || wheelUpSeen);
                            else if (count == 2) ExecuteBinding(BDoubleWheel);
                            else                 ExecuteBinding(BTripleWheel);
                        }
                    }) { IsBackground = true }.Start();
                }
                return new IntPtr(1);
            }
            else if (msg == WM_MBUTTONUP) {
                lock (MLock) {
                    wheelHeld = false;
                    if (wheelPending) { wheelUpSeen = true; return new IntPtr(1); }
                }
            }
        }
        catch { }
        return CallNextHookEx(mouseHookId, nCode, wParam, lParam);
    }

    // =========================================================================
    // Keyboard hook
    // =========================================================================
    static IntPtr kbdHookId;
    static LLProc kbdProc;

    static readonly object KLock = new object();
    static int            heldMods      = 0;
    static HashSet<byte>  heldKeys      = new HashSet<byte>();
    static HashSet<byte>  swallowedKeys = new HashSet<byte>();
    static Binding        deferred      = null;
    static int            deferGen      = 0;
    const  int            DEFER_MS      = 80;
    static bool           suppressWinUp        = false;
    static bool           releaseWinOnSuppress = false;
    static HashSet<byte>  prefixSwallowed      = new HashSet<byte>();

    static int ModBit(uint vk) {
        if (vk == VK_LCTRL  || vk == VK_RCTRL  || vk == VK_CONTROL) return MOD_CTRL;
        if (vk == VK_LSHIFT || vk == VK_RSHIFT || vk == VK_SHIFT)   return MOD_SHIFT;
        if (vk == VK_LMENU  || vk == VK_RMENU  || vk == VK_MENU)    return MOD_ALT;
        if (vk == VK_LWIN   || vk == VK_RWIN)                       return MOD_WIN;
        return 0;
    }

    static byte[] SortedHeldKeys() {
        byte[] arr = new byte[heldKeys.Count]; heldKeys.CopyTo(arr); Array.Sort(arr); return arr;
    }

    static Binding FindExact(int mods, byte[] sk) {
        if (sk.Length == 0) return null;
        Binding b; return KeySigIndex.TryGetValue(MakeSignature(mods, sk), out b) ? b : null;
    }

    static bool HasStrictSuperset(int mods, HashSet<byte> keys) {
        if (keys.Count == 0) return false;
        foreach (Binding b in KeyBindings) {
            if (b.Mods != mods || b.Keys.Length <= keys.Count) continue;
            bool ok = true;
            foreach (byte k in keys) {
                bool found = false;
                for (int i = 0; i < b.Keys.Length; i++) if (b.Keys[i] == k) { found = true; break; }
                if (!found) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }

    static void CancelDefer() { deferred = null; deferGen++; }

    static void FireDeferredIfPending() {
        if (deferred != null) {
            Binding b = deferred; deferred = null; deferGen++;
            prefixSwallowed.Clear();
            ExecuteBinding(b);
        }
    }

    static void ScheduleDefer(Binding b) {
        deferred = b;
        int myGen = ++deferGen;
        new Thread(() => {
            Thread.Sleep(DEFER_MS);
            lock (KLock) { if (myGen != deferGen) return; FireDeferredIfPending(); }
        }) { IsBackground = true }.Start();
    }

    static IntPtr KbdCallback(int nCode, IntPtr wParam, IntPtr lParam) {
        if (nCode < 0) return CallNextHookEx(kbdHookId, nCode, wParam, lParam);
        try {
            KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            if ((data.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(kbdHookId, nCode, wParam, lParam);

            int  msg    = wParam.ToInt32();
            bool isDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
            bool isUp   = (msg == WM_KEYUP   || msg == WM_SYSKEYUP);
            byte vk     = (byte)data.vkCode;
            int  modBit = ModBit(data.vkCode);

            if (modBit != 0) {
                bool swallowWinUp = false;
                lock (KLock) {
                    if (isDown) heldMods |= modBit;
                    else if (isUp) {
                        heldMods &= ~modBit;
                        if (deferred != null) FireDeferredIfPending();
                        if ((vk == VK_LWIN || vk == VK_RWIN) && suppressWinUp) {
                            suppressWinUp = false;
                            swallowWinUp = true;
                            if (releaseWinOnSuppress) {
                                releaseWinOnSuppress = false;
                                // Ctrl before Win-up breaks Explorer's clean-tap detection;
                                // Ctrl after cleans up. Injected Win-up restores key state.
                                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                                keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            }
                        }
                    }
                }
                return swallowWinUp ? new IntPtr(1) : CallNextHookEx(kbdHookId, nCode, wParam, lParam);
            }

            lock (KLock) {
                if (isDown) {
                    if (heldKeys.Contains(vk))
                        return swallowedKeys.Contains(vk) ? new IntPtr(1) : CallNextHookEx(kbdHookId, nCode, wParam, lParam);
                    heldKeys.Add(vk);
                    byte[]  sk    = SortedHeldKeys();
                    Binding exact = FindExact(heldMods, sk);
                    bool    longer = HasStrictSuperset(heldMods, heldKeys);
                    if (exact != null) {
                        swallowedKeys.Add(vk); prefixSwallowed.Clear(); CancelDefer();
                        if (longer) ScheduleDefer(exact); else ExecuteBinding(exact);
                        return new IntPtr(1);
                    }
                    if (longer) {
                        swallowedKeys.Add(vk); prefixSwallowed.Add(vk); CancelDefer();
                        return new IntPtr(1);
                    }
                    return CallNextHookEx(kbdHookId, nCode, wParam, lParam);
                }
                if (isUp) {
                    bool wasPrefix = prefixSwallowed.Remove(vk);
                    bool was = swallowedKeys.Remove(vk); heldKeys.Remove(vk);
                    if (was) {
                        bool hadDeferred = deferred != null;
                        if (hadDeferred) FireDeferredIfPending();
                        // Prefix-only swallow with no binding fired: replay the key so
                        // apps receive it (fixes Ctrl+S being lost when Ctrl+S+L is bound).
                        if (wasPrefix && !hadDeferred) {
                            keybd_event(vk, 0, 0, UIntPtr.Zero);
                            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        }
                        return new IntPtr(1);
                    }
                }
            }
        }
        catch { }
        return CallNextHookEx(kbdHookId, nCode, wParam, lParam);
    }

    // =========================================================================
    // Entry point
    // =========================================================================
    public static void Start() {
        dblClickMs = (int)GetDoubleClickTime();
        mouseProc = MouseCallback; kbdProc = KbdCallback;
        IntPtr hMod = GetModuleHandle(null);

        mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, hMod, 0);
        if (mouseHookId == IntPtr.Zero)
            throw new Exception("Mouse hook failed: " + Marshal.GetLastWin32Error());

        kbdHookId = SetWindowsHookEx(WH_KEYBOARD_LL, kbdProc, hMod, 0);
        if (kbdHookId == IntPtr.Zero) {
            int err = Marshal.GetLastWin32Error(); UnhookWindowsHookEx(mouseHookId);
            throw new Exception("Keyboard hook failed: " + err);
        }

        MSG msg; int r;
        while ((r = GetMessage(out msg, IntPtr.Zero, 0, 0)) > 0) {
            TranslateMessage(ref msg); DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(kbdHookId); UnhookWindowsHookEx(mouseHookId);
    }
}
"@
} catch { Write-Log "Add-Type failed: $_"; exit 1 }
Write-Log 'Add-Type OK.'

# ---------------------------------------------------------------------------
# Key parsing helpers
# ---------------------------------------------------------------------------
$vkMap = @{
    'ENTER'=[byte]0x0D; 'RETURN'=[byte]0x0D; 'ESC'=[byte]0x1B; 'ESCAPE'=[byte]0x1B
    'TAB'=[byte]0x09; 'SPACE'=[byte]0x20; 'BACK'=[byte]0x08; 'BACKSPACE'=[byte]0x08
    'DELETE'=[byte]0x2E; 'DEL'=[byte]0x2E; 'INSERT'=[byte]0x2D; 'INS'=[byte]0x2D
    'HOME'=[byte]0x24; 'END'=[byte]0x23; 'PGUP'=[byte]0x21; 'PAGEUP'=[byte]0x21
    'PGDN'=[byte]0x22; 'PAGEDOWN'=[byte]0x22; 'LEFT'=[byte]0x25; 'UP'=[byte]0x26
    'RIGHT'=[byte]0x27; 'DOWN'=[byte]0x28; 'PRTSCR'=[byte]0x2C; 'PRINTSCREEN'=[byte]0x2C
    'F1'=[byte]0x70; 'F2'=[byte]0x71; 'F3'=[byte]0x72; 'F4'=[byte]0x73
    'F5'=[byte]0x74; 'F6'=[byte]0x75; 'F7'=[byte]0x76; 'F8'=[byte]0x77
    'F9'=[byte]0x78; 'F10'=[byte]0x79; 'F11'=[byte]0x7A; 'F12'=[byte]0x7B
}
$outModMap = @{
    'WIN'=[byte]0x5B; 'LWIN'=[byte]0x5B; 'RWIN'=[byte]0x5C
    'SHIFT'=[byte]0x10; 'LSHIFT'=[byte]0xA0; 'RSHIFT'=[byte]0xA1
    'CTRL'=[byte]0x11; 'CONTROL'=[byte]0x11; 'LCTRL'=[byte]0xA2; 'RCTRL'=[byte]0xA3
    'ALT'=[byte]0x12; 'MENU'=[byte]0x12
}

function Resolve-SingleKey([string]$k) {
    $u = $k.Trim().ToUpper()
    if ($vkMap.ContainsKey($u))                   { return $vkMap[$u] }
    if ($u.Length -eq 1 -and $u -match '^[A-Z]$') { return [byte][int][char]$u }
    if ($u.Length -eq 1 -and $u -match '^[0-9]$') { return [byte](0x30 + [int]::Parse($u)) }
    throw "Unknown key '$k'"
}

function Resolve-OutputChord([string]$combo) {
    $out = foreach ($tok in ($combo -split '\+' | ForEach-Object { $_.Trim().ToUpper() })) {
        if ($outModMap.ContainsKey($tok))                          { $outModMap[$tok] }
        elseif ($vkMap.ContainsKey($tok))                          { $vkMap[$tok] }
        elseif ($tok.Length -eq 1 -and $tok -match '^[A-Z]$')     { [byte][int][char]$tok }
        elseif ($tok.Length -eq 1 -and $tok -match '^[0-9]$')     { [byte](0x30 + [int]::Parse($tok)) }
        else { throw "Unknown key '$tok' in '$combo'" }
    }
    ,[byte[]]$out
}

function Resolve-KeyTrigger([string]$combo) {
    $mods = 0; $keys = New-Object System.Collections.Generic.List[byte]
    foreach ($tok in ($combo -split '\+' | ForEach-Object { $_.Trim().ToUpper() })) {
        switch ($tok) {
            'CTRL'    { $mods = $mods -bor 1; continue } 'CONTROL' { $mods = $mods -bor 1; continue }
            'SHIFT'   { $mods = $mods -bor 2; continue } 'ALT'     { $mods = $mods -bor 4; continue }
            'MENU'    { $mods = $mods -bor 4; continue } 'WIN'     { $mods = $mods -bor 8; continue }
            default   { $keys.Add( (Resolve-SingleKey $tok) ) }
        }
    }
    if ($keys.Count -eq 0) { throw "No non-modifier key in '$combo'" }
    $arr = $keys.ToArray(); [Array]::Sort($arr)
    [pscustomobject]@{ Mods = $mods; Keys = [byte[]]$arr }
}

# ---------------------------------------------------------------------------
# Load shortcuts.json
# ---------------------------------------------------------------------------
$defaults = @(
    [pscustomobject]@{ trigger = 'mouse:left+right';   output = 'Win+Shift+S' },
    [pscustomobject]@{ trigger = 'mouse:double-right'; output = 'Ctrl+C'      },
    [pscustomobject]@{ trigger = 'mouse:triple-right'; output = 'Ctrl+V'      }
)

$configPath = Join-Path $PSScriptRoot 'shortcuts.json'
$altHScroll = $false
if (Test-Path $configPath) {
    try {
        $json = Get-Content $configPath -Raw | ConvertFrom-Json
        $rawBindings = @($json.bindings)
        if ($rawBindings.Count -eq 0) { $rawBindings = $defaults; Write-Log 'No bindings -- using defaults.' }
        if ($json.PSObject.Properties.Name -contains 'altHScroll') { $altHScroll = [bool]$json.altHScroll }
    } catch { Write-Log "Bad shortcuts.json -- using defaults. ($_)"; $rawBindings = $defaults }
} else {
    Write-Log 'shortcuts.json not found -- using defaults.'
    $rawBindings = $defaults
}

# ---------------------------------------------------------------------------
# Build Binding objects
# ---------------------------------------------------------------------------
$validGestures = @('left+right','double-right','triple-right','right-scroll-down','right-scroll-up','double-wheel','triple-wheel')
$built = New-Object System.Collections.Generic.List[ShortcutHook+Binding]

foreach ($b in $rawBindings) {
    try {
        $nb = [ShortcutHook+Binding]::new()
        if ($b.trigger -match '^mouse:(.+)$') {
            $g = $Matches[1].ToLower()
            if ($validGestures -notcontains $g) { Write-Log "Skip unknown gesture '$g'"; continue }
            $nb.Kind = 'mouse'; $nb.MouseGesture = $g
        } elseif ($b.trigger -match '^key:(.+)$') {
            $parsed = Resolve-KeyTrigger $Matches[1]
            $nb.Kind = 'key'; $nb.Mods = $parsed.Mods; $nb.Keys = $parsed.Keys
            $nb.Signature = [ShortcutHook]::MakeSignature($parsed.Mods, $parsed.Keys)
        } else {
            Write-Log "Skip unknown trigger prefix: $($b.trigger)"; continue
        }

        if ($b.output -match '^open:(.+)$') {
            $nb.OpenPath = $Matches[1].Trim()
        } else {
            $nb.Output = Resolve-OutputChord $b.output
        }
        $built.Add($nb)
    } catch {
        Write-Log "Skip '$($b.trigger)' -> '$($b.output)': $_"
    }
}

try {
    [ShortcutHook]::LoadBindings($built.ToArray())
    Write-Log ("Loaded {0} binding(s)." -f $built.Count)
    foreach ($x in $built) {
        $dest = if ($x.OpenPath) { "open:$($x.OpenPath)" } else { "[{0}]" -f ($x.Output -join ',') }
        if ($x.Kind -eq 'mouse') { Write-Log ("  mouse:{0} -> {1}" -f $x.MouseGesture, $dest) }
        else { Write-Log ("  key:mods={0} keys=[{1}] -> {2}" -f $x.Mods, ($x.Keys -join ','), $dest) }
    }
} catch { Write-Log "LoadBindings failed: $_"; exit 1 }

[ShortcutHook]::altScrollEnabled = $altHScroll
Write-Log ("Alt+Scroll horizontal: {0}" -f $altHScroll)

# ---------------------------------------------------------------------------
# Single-instance guard
# ---------------------------------------------------------------------------
$mutexCreated = $false
$mutex = New-Object System.Threading.Mutex($true, 'Global\ShortcutHook', [ref]$mutexCreated)
if (-not $mutexCreated) { Write-Log 'Already running.'; exit }

try {
    Write-Host 'ShortcutHook active:' -ForegroundColor Green
    foreach ($b in $rawBindings) { Write-Host ("  {0,-28} ->  {1}" -f $b.trigger, $b.output) -ForegroundColor Cyan }
    Write-Host 'Ctrl+C to stop.' -ForegroundColor DarkGray
    [ShortcutHook]::Start()
    Write-Log 'Message loop exited normally.'
} catch {
    Write-Log "ERROR: $_"; Write-Host "ERROR: $_" -ForegroundColor Red; Read-Host 'Press Enter to exit'
} finally {
    $mutex.ReleaseMutex(); $mutex.Dispose()
}
