using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ShortcutHookCore.Enums;
using System.Windows.Automation;

namespace ShortcutHookDaemon;

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
    private const uint CF_UNICODETEXT         = 13;
    private const uint CF_HDROP               = 15;
    private const uint GMEM_MOVEABLE          = 2;
    private const int  CLIP_WAIT_MS           = 80;

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

    public const int MOD_CTRL  = (int)ModifierFlags.Control;
    public const int MOD_SHIFT = (int)ModifierFlags.Shift;
    public const int MOD_ALT   = (int)ModifierFlags.Alt;
    public const int MOD_WIN   = (int)ModifierFlags.Win;

    // Global pause: while true, both hooks pass events through untouched except
    // for keyboard bindings that toggle this flag back off (see KbdCallback).
    public static volatile bool IsPaused = false;
    public static string PauseStatePath = Path.Combine(AppContext.BaseDirectory, "pause.state");

    public static volatile string SwitchProfileRequest = null;
    public static string CurrentProfileName = "";
    public static bool IsConfigAuthentic = false;
    public static uint MainThreadId = 0;

    public static HashSet<string> IgnoredApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static void WritePauseState() {
        bool paused = IsPaused;
        new Thread(() => {
            try { File.WriteAllText(PauseStatePath, paused ? "paused" : "running"); } catch { }
        }) { IsBackground = true }.Start();
    }

    [DllImport("user32.dll", SetLastError=true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LLProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll", SetLastError=true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_MOUSE    = 0;
    private const uint INPUT_KEYBOARD = 1;

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUT_UNION union;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    static void SendKey(byte vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new INPUT_UNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }

    static void SendMouse(uint flags, int dx, int dy, int data)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            union = new INPUT_UNION
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = (uint)data,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }
    [DllImport("user32.dll")]
    static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool   OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool   CloseClipboard();
    [DllImport("user32.dll")] static extern bool   EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] static extern bool   GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] static extern int  CountClipboardFormats();
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CopyImage(IntPtr hImage, uint uType, int cx, int cy, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)]  static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll", SetLastError = true)]  static extern IntPtr CopyEnhMetaFile(IntPtr hemfSrc, string lpszFile);
    [DllImport("gdi32.dll", SetLastError = true)]  static extern bool DeleteEnhMetaFile(IntPtr hemf);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool   GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Auto)]
    static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool CloseHandle(IntPtr hObject);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG {
        public IntPtr hwnd;  public uint message; public UIntPtr wParam;
        public IntPtr lParam; public uint time;   public int ptX, ptY;
    }
    [DllImport("user32.dll")] static extern int    GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool   TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr GetParent(IntPtr hWnd);

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    delegate void WinEventProc(IntPtr hWinEventHook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_CREATE     = 0x8000;
    private const uint EVENT_OBJECT_DESTROY    = 0x8001;
    private const int  OBJID_WINDOW            = 0;
    private const uint WINEVENT_OUTOFCONTEXT   = 0;

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

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO {
        public int    cbSize;
        public uint   flags;
        public IntPtr hCursor;
        public int    ptX, ptY;
    }

    // IDC_HAND — standard system hand cursor (hovering over a hyperlink).
    static readonly IntPtr IDC_HAND     = new IntPtr(32649);
    static readonly IntPtr HandCursorH  = LoadCursor(IntPtr.Zero, IDC_HAND);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr LLProc(int nCode, IntPtr wParam, IntPtr lParam);

    // ---------- Binding ----------
    public class ChainStep {
        public byte[] Output;    // keyboard chord; null when OpenPath/CmdLine/TypeText/IsHScroll/IsTogglePause is set
        public string OpenPath;  // shell-execute target; null when Output/CmdLine/TypeText/IsHScroll/IsTogglePause is set
        public string CmdLine;   // cmd.exe command; null when Output/OpenPath/TypeText/IsHScroll/IsTogglePause is set
        public bool   CmdShow;   // true = visible cmd window (/k), false = hidden (/c)
        public string TypeText;  // literal text typed via SendInput; null when Output/OpenPath/CmdLine/IsHScroll/IsTogglePause is set
        public bool   IsHScroll; // horizontal scroll; negative HScrollDelta = left, positive = right
        public int    HScrollDelta;
        public bool   IsTogglePause; // flips IsPaused; null for all other fields when set
        public string SwitchToProfile; // switches active profile and relaunches daemon; null for all other fields when set
        public int    Delay; // delay after executing this step
    }

    public class Binding {
        public string      Kind;          // "mouse" | "key" | "launch" | "exit" | "focus" | "blur"
        public string      MouseGesture;
        public int         Mods;
        public byte[]      Keys;
        public string      Signature;
        public string[]    AppNames;      // process names (e.g. "chrome.exe") for "launch"/"exit"/"focus" kind
        public string[]    Apps;          // null/empty = global; list of process names for app-scoped
        public string[]    ExceptApps;   // global binding suppressed in these process names
        public int         OutputDelay;   // ms between chained steps (0 = no delay)
        public ChainStep[] Steps;         // one or more actions to execute in order
        public bool        Debounce;      // ignore repeated scroll fires within SCROLL_DEBOUNCE_MS
        public bool        ShowToast;     // show a brief on-screen toast when this binding fires
        public string      ToastText;     // text shown in the toast (trigger description)
    }

    public static List<Binding> MouseBindings = new List<Binding>();
    public static List<Binding> KeyBindings   = new List<Binding>();
    static readonly Dictionary<Binding, int> ScrollDebounce = new Dictionary<Binding, int>();
    const int SCROLL_DEBOUNCE_MS = 200;
    // Each signature maps to an ordered list: app-scoped bindings first, global last.
    public static Dictionary<string, List<Binding>> KeySigIndex = new Dictionary<string, List<Binding>>();

    // App-launch / app-exit / app-focus / app-blur triggers, keyed by process name (e.g. "chrome.exe").
    public static Dictionary<string, List<Binding>> LaunchBindings = new Dictionary<string, List<Binding>>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, List<Binding>> ExitBindings   = new Dictionary<string, List<Binding>>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, List<Binding>> FocusBindings  = new Dictionary<string, List<Binding>>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, List<Binding>> BlurBindings   = new Dictionary<string, List<Binding>>(StringComparer.OrdinalIgnoreCase);
    static HashSet<string> KnownRunningApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static string LastFocusedApp = "";
    static readonly Dictionary<IntPtr, string> ActiveWindows = new Dictionary<IntPtr, string>();

    static IntPtr hFocusEventHook;
    static IntPtr hCreateDestroyEventHook;
    static WinEventProc winEventProcDelegate;

    // Per-gesture binding lists: app-scoped entries first, global last (mirrors KeySigIndex ordering).
    public static List<Binding> BLeftRight        = new List<Binding>();
    public static List<Binding> BLeftRightDouble  = new List<Binding>();
    public static List<Binding> BLeftRightTriple  = new List<Binding>();
    public static List<Binding> BDoubleRight      = new List<Binding>();
    public static List<Binding> BDoubleRightSel   = new List<Binding>();
    public static List<Binding> BTripleRight      = new List<Binding>();
    public static List<Binding> BRightScrollDown  = new List<Binding>();
    public static List<Binding> BRightScrollUp    = new List<Binding>();
    public static List<Binding> BShiftScrollDown       = new List<Binding>();
    public static List<Binding> BShiftScrollUp         = new List<Binding>();
    public static List<Binding> BCtrlShiftScrollDown   = new List<Binding>();
    public static List<Binding> BCtrlShiftScrollUp     = new List<Binding>();
    public static List<Binding> BAltScrollDown         = new List<Binding>();
    public static List<Binding> BAltScrollUp           = new List<Binding>();
    public static List<Binding> BSingleWheel           = new List<Binding>();
    public static List<Binding> BDoubleWheel      = new List<Binding>();
    public static List<Binding> BTripleWheel      = new List<Binding>();

    public static string MakeSignature(int mods, byte[] sortedKeys) {
        string[] parts = new string[sortedKeys.Length];
        for (int i = 0; i < sortedKeys.Length; i++) parts[i] = ((int)sortedKeys[i]).ToString();
        return mods + ":" + String.Join(",", parts);
    }

    public static void LoadBindings(Binding[] bindings) {
        MouseBindings.Clear(); KeyBindings.Clear(); KeySigIndex.Clear(); ScrollDebounce.Clear();
        LaunchBindings.Clear(); ExitBindings.Clear(); FocusBindings.Clear(); BlurBindings.Clear();
        BLeftRight.Clear(); BLeftRightDouble.Clear(); BLeftRightTriple.Clear();
        BDoubleRight.Clear(); BDoubleRightSel.Clear(); BTripleRight.Clear();
        BRightScrollDown.Clear(); BRightScrollUp.Clear();
        BShiftScrollDown.Clear(); BShiftScrollUp.Clear();
        BCtrlShiftScrollDown.Clear(); BCtrlShiftScrollUp.Clear();
        BAltScrollDown.Clear(); BAltScrollUp.Clear();
        BSingleWheel.Clear(); BDoubleWheel.Clear(); BTripleWheel.Clear();
        lrPending = false; lrCount = 0;
        // Separate app-scoped and global mouse bindings so scoped are tried first per gesture.
        var scopedMouse = new Dictionary<string, List<Binding>>();
        var globalMouse = new Dictionary<string, List<Binding>>();
        var scopedKbd   = new List<Binding>();
        var globalKbd   = new List<Binding>();
        foreach (Binding b in bindings) {
            bool isScoped = b.Apps != null && b.Apps.Length > 0;
            if (b.Kind == "mouse") {
                MouseBindings.Add(b);
                var dict = isScoped ? scopedMouse : globalMouse;
                List<Binding> mlist;
                if (!dict.TryGetValue(b.MouseGesture, out mlist)) { mlist = new List<Binding>(); dict[b.MouseGesture] = mlist; }
                mlist.Add(b);
            } else if (b.Kind == "key") {
                KeyBindings.Add(b);
                if (isScoped) scopedKbd.Add(b); else globalKbd.Add(b);
            } else if (b.Kind == "launch") {
                AddAppNameBindings(LaunchBindings, b);
            } else if (b.Kind == "exit") {
                AddAppNameBindings(ExitBindings, b);
            } else if (b.Kind == "focus") {
                AddAppNameBindings(FocusBindings, b);
            } else if (b.Kind == "blur") {
                AddAppNameBindings(BlurBindings, b);
            }
        }
        // Populate gesture lists: scoped first so ResolveMouseBinding checks them before global.
        System.Action<string, List<Binding>> addGesture = (gesture, list) => {
            List<Binding> tmp;
            if (scopedMouse.TryGetValue(gesture, out tmp)) list.AddRange(tmp);
            if (globalMouse.TryGetValue(gesture, out tmp)) list.AddRange(tmp);
        };
        addGesture("left+right",        BLeftRight);
        addGesture("left+rightx2",      BLeftRightDouble);
        addGesture("left+rightx3",      BLeftRightTriple);
        addGesture("double-right",      BDoubleRight);
        addGesture("double-right-sel",  BDoubleRightSel);
        addGesture("triple-right",      BTripleRight);
        addGesture("right-scroll-down",  BRightScrollDown);
        addGesture("right-scroll-up",    BRightScrollUp);
        addGesture("shift-scroll-down",       BShiftScrollDown);
        addGesture("shift-scroll-up",         BShiftScrollUp);
        addGesture("ctrl-shift-scroll-down",  BCtrlShiftScrollDown);
        addGesture("ctrl-shift-scroll-up",    BCtrlShiftScrollUp);
        addGesture("alt-scroll-down",         BAltScrollDown);
        addGesture("alt-scroll-up",           BAltScrollUp);
        addGesture("single-wheel",            BSingleWheel);
        addGesture("double-wheel",      BDoubleWheel);
        addGesture("triple-wheel",      BTripleWheel);
        // Index keyboard bindings: scoped first so FindExact checks them before global ones.
        foreach (Binding b in scopedKbd) {
            List<Binding> list;
            if (!KeySigIndex.TryGetValue(b.Signature, out list)) { list = new List<Binding>(); KeySigIndex[b.Signature] = list; }
            list.Add(b);
        }
        foreach (Binding b in globalKbd) {
            List<Binding> list;
            if (!KeySigIndex.TryGetValue(b.Signature, out list)) { list = new List<Binding>(); KeySigIndex[b.Signature] = list; }
            list.Add(b);
        }
    }

    // ---------- App-launch / app-exit / app-focus polling ----------
    // No WMI process-start trace here (Win32_ProcessStartTrace needs elevation) —
    // a lightweight poll on a background timer is simpler and good enough at this resolution.
    static void AddAppNameBindings(Dictionary<string, List<Binding>> dict, Binding b) {
        if (b.AppNames == null) return;
        foreach (string name in b.AppNames) {
            List<Binding> list;
            if (!dict.TryGetValue(name, out list)) { list = new List<Binding>(); dict[name] = list; }
            list.Add(b);
        }
    }

    static string GetProcessNameFromWindow(IntPtr hwnd) {
        try {
            if (hwnd == IntPtr.Zero) return "";
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return "";
            return GetProcessNameFromPid(pid);
        } catch { return ""; }
    }

    static string GetProcessNameFromPid(uint pid) {
        try {
            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return "";
            try {
                var sb = new System.Text.StringBuilder(1024);
                uint size = 1024;
                if (!QueryFullProcessImageName(hProc, 0, sb, ref size)) return "";
                return System.IO.Path.GetFileName(sb.ToString(0, (int)size));
            } finally { CloseHandle(hProc); }
        } catch { return ""; }
    }

    static bool PopulateExistingWindows(IntPtr hwnd, IntPtr lParam) {
        if (GetParent(hwnd) == IntPtr.Zero) {
            string name = GetProcessNameFromWindow(hwnd);
            if (!string.IsNullOrEmpty(name)) {
                ActiveWindows[hwnd] = name;
                KnownRunningApps.Add(name);
            }
        }
        return true;
    }

    static void WinEventCallback(IntPtr hWinEventHook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime) {
        try {
            if (eventId == EVENT_SYSTEM_FOREGROUND) {
                string fgApp = GetForegroundProcessName();
                if (!string.IsNullOrEmpty(fgApp) && !string.Equals(fgApp, LastFocusedApp, StringComparison.OrdinalIgnoreCase)) {
                    List<Binding> blist;
                    if (!string.IsNullOrEmpty(LastFocusedApp) && BlurBindings.TryGetValue(LastFocusedApp, out blist)) {
                        foreach (Binding b in blist) ExecuteBinding(b);
                    }
                    List<Binding> flist;
                    if (FocusBindings.TryGetValue(fgApp, out flist)) {
                        foreach (Binding b in flist) ExecuteBinding(b);
                    }
                    LastFocusedApp = fgApp;
                }
                return;
            }

            if (idObject != OBJID_WINDOW) return;

            if (eventId == EVENT_OBJECT_CREATE) {
                if (GetParent(hwnd) == IntPtr.Zero) {
                    string name = GetProcessNameFromWindow(hwnd);
                    if (!string.IsNullOrEmpty(name)) {
                        ActiveWindows[hwnd] = name;
                        if (!KnownRunningApps.Contains(name)) {
                            KnownRunningApps.Add(name);
                            List<Binding> list;
                            if (LaunchBindings.TryGetValue(name, out list)) {
                                foreach (Binding b in list) ExecuteBinding(b);
                            }
                        }
                    }
                }
            }
            else if (eventId == EVENT_OBJECT_DESTROY) {
                if (ActiveWindows.TryGetValue(hwnd, out string name)) {
                    ActiveWindows.Remove(hwnd);
                    bool anyLeft = false;
                    foreach (var pair in ActiveWindows) {
                        if (string.Equals(pair.Value, name, StringComparison.OrdinalIgnoreCase)) {
                            anyLeft = true;
                            break;
                        }
                    }
                    if (!anyLeft) {
                        string procNameOnly = name;
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
                            procNameOnly = name.Substring(0, name.Length - 4);
                        }
                        var processes = Process.GetProcessesByName(procNameOnly);
                        if (processes.Length == 0) {
                            KnownRunningApps.Remove(name);
                            List<Binding> list;
                            if (ExitBindings.TryGetValue(name, out list)) {
                                foreach (Binding b in list) ExecuteBinding(b);
                            }
                        } else {
                            foreach (var p in processes) p.Dispose();
                        }
                    }
                }
            }
        } catch { }
    }

    // ---------- Clipboard helpers (selection detection) ----------
    public class ClipboardBackupItem {
        public uint Format;
        public byte[] Data;
        public IntPtr GdiHandle;
    }

    static List<ClipboardBackupItem> BackupClipboard() {
        var backup = new List<ClipboardBackupItem>();
        try {
            if (!OpenClipboard(IntPtr.Zero)) return backup;
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0) {
                IntPtr hData = GetClipboardData(format);
                if (hData == IntPtr.Zero) continue;

                if (format == 2) { // CF_BITMAP
                    IntPtr hCopy = CopyImage(hData, 0, 0, 0, 0); // IMAGE_BITMAP = 0
                    if (hCopy != IntPtr.Zero) {
                        backup.Add(new ClipboardBackupItem { Format = format, GdiHandle = hCopy });
                    }
                } else if (format == 14) { // CF_ENHMETAFILE
                    IntPtr hCopy = CopyEnhMetaFile(hData, null);
                    if (hCopy != IntPtr.Zero) {
                        backup.Add(new ClipboardBackupItem { Format = format, GdiHandle = hCopy });
                    }
                } else { // Standard HGLOBAL formats
                    UIntPtr size = GlobalSize(hData);
                    if (size == UIntPtr.Zero) continue;
                    int len = (int)size.ToUInt64();
                    byte[] data = new byte[len];
                    IntPtr pSrc = GlobalLock(hData);
                    if (pSrc != IntPtr.Zero) {
                        Marshal.Copy(pSrc, data, 0, len);
                        GlobalUnlock(hData);
                        backup.Add(new ClipboardBackupItem { Format = format, Data = data });
                    }
                }
            }
            CloseClipboard();
        } catch {
            try { CloseClipboard(); } catch {}
        }
        return backup;
    }

    static void ClearClipboard() {
        try { if (OpenClipboard(IntPtr.Zero)) { EmptyClipboard(); CloseClipboard(); } } catch { }
    }

    static void FreeGdiHandle(uint format, IntPtr h) {
        try {
            if (format == 2) DeleteObject(h);
            else if (format == 14) DeleteEnhMetaFile(h);
        } catch {}
    }

    static void FreeBackup(List<ClipboardBackupItem> backup) {
        if (backup == null) return;
        foreach (var item in backup) {
            if (item.GdiHandle != IntPtr.Zero) {
                FreeGdiHandle(item.Format, item.GdiHandle);
            }
        }
    }

    static void RestoreClipboard(List<ClipboardBackupItem> backup) {
        if (backup == null) return;
        try {
            if (!OpenClipboard(IntPtr.Zero)) return;
            EmptyClipboard();
            foreach (var item in backup) {
                if (item.GdiHandle != IntPtr.Zero) {
                    if (SetClipboardData(item.Format, item.GdiHandle) == IntPtr.Zero) {
                        FreeGdiHandle(item.Format, item.GdiHandle);
                    }
                } else if (item.Data != null) {
                    IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)item.Data.Length);
                    if (hMem == IntPtr.Zero) continue;
                    IntPtr pDst = GlobalLock(hMem);
                    if (pDst == IntPtr.Zero) {
                        GlobalFree(hMem);
                        continue;
                    }
                    Marshal.Copy(item.Data, 0, pDst, item.Data.Length);
                    GlobalUnlock(hMem);
                    if (SetClipboardData(item.Format, hMem) == IntPtr.Zero) {
                        GlobalFree(hMem);
                    }
                }
            }
            CloseClipboard();
        } catch {
            try { CloseClipboard(); } catch {}
        }
    }

    static bool ClipboardHasData() {
        return CountClipboardFormats() > 0;
    }

    static byte[] GetClipboardFormatData(uint format) {
        try {
            if (!OpenClipboard(IntPtr.Zero)) return null;
            IntPtr hData = GetClipboardData(format);
            if (hData == IntPtr.Zero) { CloseClipboard(); return null; }
            UIntPtr size = GlobalSize(hData);
            if (size == UIntPtr.Zero) { CloseClipboard(); return null; }
            int len = (int)size.ToUInt64();
            byte[] data = new byte[len];
            IntPtr pSrc = GlobalLock(hData);
            if (pSrc != IntPtr.Zero) {
                Marshal.Copy(pSrc, data, 0, len);
                GlobalUnlock(hData);
            }
            CloseClipboard();
            return data;
        } catch {
            try { CloseClipboard(); } catch {}
        }
        return null;
    }

    static List<string> GetCopiedFiles(byte[] dropFilesData) {
        var files = new List<string>();
        if (dropFilesData == null || dropFilesData.Length < 20) return files;
        try {
            int pFiles = BitConverter.ToInt32(dropFilesData, 0);
            bool fWide = dropFilesData[16] != 0;
            if (pFiles < 0 || pFiles >= dropFilesData.Length) return files;

            if (fWide) {
                int i = pFiles;
                while (i < dropFilesData.Length - 1) {
                    if (dropFilesData[i] == 0 && dropFilesData[i + 1] == 0) break;
                    int start = i;
                    while (i < dropFilesData.Length - 1 && (dropFilesData[i] != 0 || dropFilesData[i + 1] != 0)) {
                        i += 2;
                    }
                    int byteLen = i - start;
                    if (byteLen > 0) {
                        string path = System.Text.Encoding.Unicode.GetString(dropFilesData, start, byteLen);
                        files.Add(path);
                    }
                    i += 2;
                }
            } else {
                int i = pFiles;
                while (i < dropFilesData.Length) {
                    if (dropFilesData[i] == 0) break;
                    int start = i;
                    while (i < dropFilesData.Length && dropFilesData[i] != 0) {
                        i++;
                    }
                    int byteLen = i - start;
                    if (byteLen > 0) {
                        string path = System.Text.Encoding.Default.GetString(dropFilesData, start, byteLen);
                        files.Add(path);
                    }
                    i++;
                }
            }
        } catch {}
        return files;
    }

    static bool IsSystemOrHidden(string path) {
        try {
            string u = path.ToUpper();
            if (u.Contains("$RECYCLE.BIN") || u.Contains("SYSTEM VOLUME INFORMATION")) return true;
            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) return true;
        } catch {}
        return false;
    }

    static bool IsExplorerInvisibleSelection(List<string> files) {
        if (files.Count == 0) return false;
        foreach (var file in files) {
            if (!IsSystemOrHidden(file)) return false;
        }
        return true;
    }

    static int GetExplorerSelectionCount() {
        try {
            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero) return -1;

            Type shellAppType = Type.GetTypeFromProgID("Shell.Application");
            if (shellAppType == null) return -1;

            object shell = Activator.CreateInstance(shellAppType);
            if (shell == null) return -1;

            object windows = shellAppType.InvokeMember("Windows",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
            if (windows == null) return -1;

            int count = (int)windows.GetType().InvokeMember("Count",
                System.Reflection.BindingFlags.GetProperty, null, windows, null);

            for (int i = 0; i < count; i++) {
                object window = windows.GetType().InvokeMember("Item",
                    System.Reflection.BindingFlags.InvokeMethod, null, windows, new object[] { i });
                if (window == null) continue;

                object hwndObj = window.GetType().InvokeMember("HWND",
                    System.Reflection.BindingFlags.GetProperty, null, window, null);

                IntPtr hwnd = IntPtr.Zero;
                if (hwndObj is int) hwnd = new IntPtr((int)hwndObj);
                else if (hwndObj is long) hwnd = new IntPtr((long)hwndObj);

                if (hwnd == fgHwnd) {
                    string name = (string)window.GetType().InvokeMember("Name",
                        System.Reflection.BindingFlags.GetProperty, null, window, null);
                    if (name != null && name.ToLower().Contains("explorer")) {
                        object document = window.GetType().InvokeMember("Document",
                            System.Reflection.BindingFlags.GetProperty, null, window, null);
                        if (document != null) {
                            object selectedItems = document.GetType().InvokeMember("SelectedItems",
                                System.Reflection.BindingFlags.InvokeMethod, null, document, null);
                            if (selectedItems != null) {
                                int selCount = (int)selectedItems.GetType().InvokeMember("Count",
                                    System.Reflection.BindingFlags.GetProperty, null, selectedItems, null);
                                return selCount;
                            }
                        }
                    }
                }
            }
        } catch {}
        return -1;
    }

    static bool DetectTextSelection() {
        int explorerSelCount = GetExplorerSelectionCount();
        if (explorerSelCount >= 0) {
            return explorerSelCount > 0;
        }

        try {
            AutomationElement focusedElement = AutomationElement.FocusedElement;
            if (focusedElement != null && focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out object patternObj)) {
                var textPattern = (TextPattern)patternObj;
                var selection = textPattern.GetSelection();
                if (selection != null && selection.Length > 0) {
                    string selectedText = selection[0].GetText(-1);
                    return !string.IsNullOrEmpty(selectedText);
                }
            }
        } catch { }

        return false;
    }
    static void ExecuteDoubleRight() {
        if (BDoubleRightSel.Count == 0) { ExecuteBinding(ResolveMouseBinding(BDoubleRight)); return; }
        bool hasSel = DetectTextSelection();
        ExecuteBinding(ResolveMouseBinding(hasSel ? BDoubleRightSel : BDoubleRight));
    }

    // ---------- Output dispatch ----------
    static void ExecuteStep(ChainStep step) {
        if (step == null) return;
        if (step.IsTogglePause) {
            IsPaused = !IsPaused;
            WritePauseState();
            return;
        }
        if (step.SwitchToProfile != null) {
            string target = step.SwitchToProfile;
            if (string.Equals(target, CurrentProfileName, StringComparison.Ordinal)) return;
            SwitchProfileRequest = target;
            PostThreadMessage(MainThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        if (step.IsHScroll) {
            int d = step.HScrollDelta;
            new Thread(() => {
                try { SendMouse(MOUSEEVENTF_HWHEEL, 0, 0, d); }
                catch { }
            }) { IsBackground = true }.Start();
            return;
        }
        if (step.OpenPath != null) {
            bool uWinL = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0;
            bool uWinR = (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
            // Don't inject a synthetic Win-up here — that itself triggers Start Menu.
            // Instead mark that we need to release Win at physical Win-up time, where
            // we can sandwich it between Ctrl events to break the clean-tap sequence.
            if (uWinL || uWinR) lock (KLock) { suppressWinUp = true; releaseWinOnSuppress = true; }
            string path = step.OpenPath.Trim();
            if (!IsConfigAuthentic &&
                !path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) {
                try {
                    string fullPath = Path.GetFullPath(path);
                    if (!IsPathSafe(fullPath)) return;
                } catch { return; }
            }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        } else if (step.CmdLine != null) {
            bool uWinL = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0;
            bool uWinR = (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
            if (uWinL || uWinR) lock (KLock) { suppressWinUp = true; releaseWinOnSuppress = true; }
            string cmd = step.CmdLine;
            if (!IsConfigAuthentic) {
                try {
                    string exePath = ResolveCommandPath(cmd);
                    if (!string.IsNullOrEmpty(exePath) && Path.IsPathRooted(exePath)) {
                        if (!IsPathSafe(exePath)) return;
                    }
                } catch { return; }
            }
            bool show = step.CmdShow;
            try {
                var (exe, args) = SplitCommandLine(cmd);
                if (!string.IsNullOrEmpty(exe)) {
                    string targetExe = exe;
                    if (!Path.IsPathRooted(targetExe)) {
                        string resolved = ResolveCommandPath(cmd);
                        if (Path.IsPathRooted(resolved)) {
                            targetExe = resolved;
                        }
                    }
                    Process.Start(new ProcessStartInfo(targetExe) {
                        Arguments = args,
                        UseShellExecute = show,
                        CreateNoWindow = !show,
                    });
                }
            } catch { }
        } else if (step.TypeText != null) {
            TypeText(step.TypeText);
        } else {
            FireOutput(step.Output);
        }
    }

    static bool IsPathSafe(string path) {
        try {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string fullPath = Path.GetFullPath(path);
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            if (fullPath.StartsWith(winDir, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(progFiles, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(progFilesX86, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(appData, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        } catch { }
        return false;
    }

    static string ResolveCommandPath(string cmd) {
        if (string.IsNullOrWhiteSpace(cmd)) return "";
        string exe = "";
        cmd = cmd.Trim();
        if (cmd.StartsWith("\"")) {
            int end = cmd.IndexOf("\"", 1);
            if (end > 0) exe = cmd.Substring(1, end - 1);
        } else {
            int space = cmd.IndexOf(' ');
            exe = space > 0 ? cmd.Substring(0, space) : cmd;
        }

        if (Path.IsPathRooted(exe)) {
            return exe;
        }

        if (File.Exists(exe)) return Path.GetFullPath(exe);

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null) {
            foreach (var p in pathEnv.Split(Path.PathSeparator)) {
                try {
                    var full = Path.Combine(p, exe);
                    if (File.Exists(full)) return Path.GetFullPath(full);
                    foreach (var ext in new[] { ".exe", ".cmd", ".bat" }) {
                        var fullExt = full + ext;
                        if (File.Exists(fullExt)) return Path.GetFullPath(fullExt);
                    }
                } catch { }
            }
        }
        return exe;
    }

    static (string exe, string args) SplitCommandLine(string cmd) {
        if (string.IsNullOrWhiteSpace(cmd)) return ("", "");
        cmd = cmd.Trim();
        string exe = "";
        string args = "";
        if (cmd.StartsWith("\"")) {
            int end = cmd.IndexOf("\"", 1);
            if (end > 0) {
                exe = cmd.Substring(1, end - 1);
                args = cmd.Substring(end + 1).Trim();
            } else {
                exe = cmd.Substring(1);
            }
        } else {
            int space = cmd.IndexOf(' ');
            if (space > 0) {
                exe = cmd.Substring(0, space);
                args = cmd.Substring(space + 1).Trim();
            } else {
                exe = cmd;
            }
        }
        return (exe, args);
    }

    static bool HasTogglePause(Binding b) {
        foreach (ChainStep s in b.Steps) if (s.IsTogglePause) return true;
        return false;
    }

    // Spawns a small borderless topmost popup near the bottom-right corner of the
    // screen showing the binding's trigger, auto-dismissing after ~1s. Runs on its
    // own STA thread with its own message loop (Application.Run), so it never
    // touches the hook callback's thread.
    static void ShowToast(string text) {
        if (string.IsNullOrEmpty(text)) return;
        Thread t = new Thread(() => {
            try {
                Form f = new Form();
                f.FormBorderStyle = FormBorderStyle.None;
                f.TopMost = true;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.Manual;
                f.BackColor = Color.FromArgb(32, 32, 32);
                f.Opacity = 0.92;
                f.AutoSize = true;
                f.AutoSizeMode = AutoSizeMode.GrowAndShrink;

                Label lbl = new Label();
                lbl.Text = text;
                lbl.ForeColor = Color.White;
                lbl.Font = new Font("Segoe UI", 10F);
                lbl.AutoSize = true;
                lbl.Padding = new Padding(14, 8, 14, 8);
                f.Controls.Add(lbl);

                f.Shown += (s, e) => {
                    Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
                    f.Location = new Point(area.Right - f.Width - 16, area.Bottom - f.Height - 16);
                    // Form.TopMost alone doesn't always place us above the
                    // current foreground window's z-order band when we're a
                    // background process. Force it explicitly via SetWindowPos
                    // (SWP_NOACTIVATE so focus stays with the foreground app).
                    const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
                    SetWindowPos(f.Handle, new IntPtr(-1), f.Left, f.Top, f.Width, f.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                };

                System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                timer.Interval = 1000;
                timer.Tick += (s, e) => { timer.Stop(); f.Close(); };
                f.Shown += (s, e) => timer.Start();

                Application.Run(f);
            } catch { }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static void ExecuteBinding(Binding b) {
        if (b == null || b.Steps == null || b.Steps.Length == 0) return;
        // While paused, every binding except a toggle:pause one is a no-op.
        if (IsPaused && !HasTogglePause(b)) return;
        if (b.ShowToast) ShowToast(b.ToastText);
        // Single-action with no delay: fast path — run inline (FireOutput is safe in hook callback).
        if (b.Steps.Length == 1 && b.Steps[0].Delay == 0 && b.Steps[0].Output != null) {
            FireOutput(b.Steps[0].Output);
            return;
        }
        // Chain or open/cmd step: run on background thread so the callback returns quickly.
        ChainStep[] steps = b.Steps;
        new Thread(() => {
            for (int i = 0; i < steps.Length; i++) {
                if (steps[i].Delay > 0) Thread.Sleep(steps[i].Delay);
                ExecuteStep(steps[i]);
            }
        }) { IsBackground = true }.Start();
    }

    static void FireOutput(byte[] chord) {
        if (chord == null || chord.Length == 0) return;
        bool uCtrl  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool uShift = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;
        bool uAlt   = (GetAsyncKeyState(VK_MENU)    & 0x8000) != 0;
        bool uWinL  = (GetAsyncKeyState(VK_LWIN)    & 0x8000) != 0;
        bool uWinR  = (GetAsyncKeyState(VK_RWIN)    & 0x8000) != 0;

        // Which modifiers does the chord explicitly include?
        bool cShift = false; bool cCtrl = false; bool cAlt = false;
        foreach (byte k in chord) {
            if      (k == VK_SHIFT   || k == VK_LSHIFT || k == VK_RSHIFT) cShift = true;
            else if (k == VK_CONTROL || k == VK_LCTRL  || k == VK_RCTRL)  cCtrl  = true;
            else if (k == VK_MENU    || k == VK_LMENU  || k == VK_RMENU)  cAlt   = true;
        }

        // Release held modifiers that the chord does NOT want (prevent interference).
        // For modifiers that the chord DOES include, skip the pre-release — the chord
        // re-fires them itself and the app must see the modifier held continuously.
        if (uCtrl  && !cCtrl)  SendKey(VK_CONTROL, true);
        if (uShift && !cShift) SendKey(VK_SHIFT,   true);
        if (uAlt   && !cAlt)   SendKey(VK_MENU,    true);
        if (uWinL)  SendKey(VK_LWIN,    true);
        if (uWinR)  SendKey(VK_RWIN,    true);

        // Fire the full chord — including any modifier keys it contains.
        // If a modifier was already physically held, injecting its key-down again fires
        // it as a synthetic repeat (WM_KEYDOWN with repeat count), which the target app
        // processes as "modifier still held" — guaranteeing it sees the modifier when
        // the action key arrives, regardless of any physical-key timing.
        foreach (byte k in chord)                    SendKey(k, false);
        for (int i = chord.Length - 1; i >= 0; i--) SendKey(chord[i], true);

        // Re-press non-Win modifiers so the user's held keys remain active.
        // Group 1: modifiers that were released in the pre-release step above.
        if (uAlt   && !cAlt)   SendKey(VK_MENU,    false);
        if (uShift && !cShift) SendKey(VK_SHIFT,   false);
        if (uCtrl  && !cCtrl)  SendKey(VK_CONTROL, false);
        // Group 2: chord modifiers that were physically held — the chord's own key-up
        // released them, so re-press to restore the user's hold state.
        if (uAlt   && cAlt)    SendKey(VK_MENU,    false);
        if (uShift && cShift)  SendKey(VK_SHIFT,   false);
        if (uCtrl  && cCtrl)   SendKey(VK_CONTROL, false);

        if (uWinL || uWinR) lock (KLock) { suppressWinUp = true; }
        else { if (uWinR) SendKey(VK_RWIN, false); if (uWinL) SendKey(VK_LWIN, false); }
    }

    // Types a literal string in one shot via the clipboard: stash the current
    // clipboard, drop the text in as CF_UNICODETEXT, send Ctrl+V, then restore
    // the clipboard the caller had. Multi-line text pastes as real line breaks
    // without needing per-key Enter presses.
    static void TypeText(string text) {
        if (string.IsNullOrEmpty(text)) return;
        bool uCtrl  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool uShift = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;
        bool uAlt   = (GetAsyncKeyState(VK_MENU)    & 0x8000) != 0;
        bool uWinL  = (GetAsyncKeyState(VK_LWIN)    & 0x8000) != 0;
        bool uWinR  = (GetAsyncKeyState(VK_RWIN)    & 0x8000) != 0;

        // Release held modifiers so they don't interfere with our own Ctrl+V.
        if (uCtrl)  SendKey(VK_CONTROL, true);
        if (uShift) SendKey(VK_SHIFT,   true);
        if (uAlt)   SendKey(VK_MENU,    true);
        if (uWinL)  SendKey(VK_LWIN,    true);
        if (uWinR)  SendKey(VK_RWIN,    true);

        var saved = BackupClipboard();
        SetClipboardText(text);

        SendKey(VK_CONTROL, false);
        SendKey(0x56 /* V */, false);
        SendKey(0x56, true);
        SendKey(VK_CONTROL, true);

        // Give the target app time to read the clipboard before we restore it.
        Thread.Sleep(CLIP_WAIT_MS);
        RestoreClipboard(saved);
        // Modifiers are NOT re-pressed here. TypeText runs on a background thread and
        // CLIP_WAIT_MS is long enough that the user has already released the physical
        // trigger keys. Synthetically re-pressing them creates ghost key-down events
        // with no matching key-up, leaving Ctrl/Shift stuck for all subsequent input.
    }

    static void SetClipboardText(string text) {
        try {
            if (!OpenClipboard(IntPtr.Zero)) return;
            EmptyClipboard();
            int bytes = (text.Length + 1) * 2; // UTF-16 chars + null terminator
            IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero) { CloseClipboard(); return; }
            IntPtr p = GlobalLock(hMem);
            if (p == IntPtr.Zero) { GlobalFree(hMem); CloseClipboard(); return; }
            for (int i = 0; i < text.Length; i++) Marshal.WriteInt16(p, i * 2, (short)text[i]);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(hMem);
            if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) GlobalFree(hMem);
            CloseClipboard();
        } catch {
            try { CloseClipboard(); } catch {}
        }
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
    static int  lrGen           = 0;
    static int  lrCount         = 0;
    static bool lrPending       = false;

    static void Reinject(bool withUp) {
        Interlocked.Increment(ref reinjDown);
        SendMouse(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0);
        if (withUp) { Interlocked.Increment(ref reinjUp); SendMouse(MOUSEEVENTF_RIGHTUP, 0, 0, 0); }
    }

    static void ReinjectWheel(bool withUp) {
        Interlocked.Increment(ref reinjWheelDown);
        SendMouse(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0);
        if (withUp) { Interlocked.Increment(ref reinjWheelUp); SendMouse(MOUSEEVENTF_MIDDLEUP, 0, 0, 0); }
    }

    static IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam) {
        if (nCode < 0) return CallNextHookEx(mouseHookId, nCode, wParam, lParam);
        // While paused, mouse input passes through untouched. Resuming is keyboard-only
        // (mouse gesture detection requires timers we don't want running while paused).
        if (IsPaused) return CallNextHookEx(mouseHookId, nCode, wParam, lParam);
        if (IgnoredApps.Count > 0 && IgnoredApps.Contains(GetForegroundProcessName()))
            return CallNextHookEx(mouseHookId, nCode, wParam, lParam);
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
                        ExecuteBinding(ResolveMouseBinding(BLeftRight));
                    }
                }
            }
            else if (msg == WM_LBUTTONUP) { leftDown = false; }
            else if (msg == WM_RBUTTONDOWN) {
                lock (MLock) {
                    int myGen = ++mGeneration;
                    rightHeld = true; suppressRightUp = false;

                    if (leftDown || lrPending) {
                        int capturedLrGen = ++lrGen;
                        rightClickCount = 0; rightPending = false; rightUpSeen = false;
                        suppressRightUp = true; leftDown = false;
                        if (!lrPending) lrCount = 1; else lrCount++;

                        bool hasDouble = BLeftRightDouble.Count > 0;
                        bool hasTriple = BLeftRightTriple.Count > 0;

                        // 3+ presses: fire best matching binding immediately
                        if (lrCount >= 3) {
                            lrPending = false; lrCount = 0;
                            if      (hasTriple) ExecuteBinding(ResolveMouseBinding(BLeftRightTriple));
                            else if (hasDouble) ExecuteBinding(ResolveMouseBinding(BLeftRightDouble));
                            else                ExecuteBinding(ResolveMouseBinding(BLeftRight));
                            return new IntPtr(1);
                        }
                        // Exactly 2 and no triple bound: fire double immediately
                        if (lrCount == 2 && hasDouble && !hasTriple) {
                            lrPending = false; lrCount = 0;
                            ExecuteBinding(ResolveMouseBinding(BLeftRightDouble));
                            return new IntPtr(1);
                        }
                        // No multi-click bindings at all: fire single immediately
                        if (!hasDouble && !hasTriple) {
                            lrPending = false; lrCount = 0;
                            ExecuteBinding(ResolveMouseBinding(BLeftRight));
                            return new IntPtr(1);
                        }
                        // Still within a possible longer combo: wait for more presses
                        lrPending = true;
                        new Thread(() => {
                            Thread.Sleep(dblClickMs);
                            lock (MLock) {
                                if (capturedLrGen != lrGen || !lrPending) return;
                                lrPending = false;
                                int c = lrCount; lrCount = 0;
                                if      (c >= 3 && hasTriple) ExecuteBinding(ResolveMouseBinding(BLeftRightTriple));
                                else if (c >= 2 && hasDouble) ExecuteBinding(ResolveMouseBinding(BLeftRightDouble));
                                else                          ExecuteBinding(ResolveMouseBinding(BLeftRight));
                            }
                        }) { IsBackground = true }.Start();
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
                            else if (count == 2) { if (rightHeld) suppressRightUp = true; ExecuteDoubleRight(); }
                            else                 { if (rightHeld) suppressRightUp = true; ExecuteBinding(ResolveMouseBinding(BTripleRight)); }
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
                        Binding b = ResolveMouseBinding(delta < 0 ? BRightScrollDown : BRightScrollUp);
                        if (b != null) {
                            mGeneration++; rightPending = false; suppressRightUp = true;
                            bool shouldFire = true;
                            if (b.Debounce) {
                                int now = Environment.TickCount; int last;
                                if (ScrollDebounce.TryGetValue(b, out last) && (now - last) < SCROLL_DEBOUNCE_MS) shouldFire = false;
                                else ScrollDebounce[b] = now;
                            }
                            if (shouldFire) ExecuteBinding(b);
                            return new IntPtr(1);
                        }
                    } else {
                        MSLLHOOKSTRUCT ms = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        short delta = (short)((ms.mouseData >> 16) & 0xFFFF);
                        bool shiftHeld = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;
                        bool ctrlHeld  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                        bool altHeld   = (GetAsyncKeyState(VK_MENU)    & 0x8000) != 0;
                        Binding b = null;
                        if      (ctrlHeld && shiftHeld) b = ResolveMouseBinding(delta < 0 ? BCtrlShiftScrollDown : BCtrlShiftScrollUp);
                        else if (shiftHeld)             b = ResolveMouseBinding(delta < 0 ? BShiftScrollDown     : BShiftScrollUp);
                        else if (altHeld)               b = ResolveMouseBinding(delta < 0 ? BAltScrollDown       : BAltScrollUp);
                        if (b != null) {
                            bool shouldFire = true;
                            if (b.Debounce) {
                                int now = Environment.TickCount; int last;
                                if (ScrollDebounce.TryGetValue(b, out last) && (now - last) < SCROLL_DEBOUNCE_MS) shouldFire = false;
                                else ScrollDebounce[b] = now;
                            }
                            if (shouldFire) ExecuteBinding(b);
                            return new IntPtr(1);
                        }
                    }
                }
            }
            else if (msg == WM_MBUTTONDOWN) {
                // Capture cursor shape now, before the timer fires, to detect link hovers.
                // Browsers set IDC_HAND when the cursor is over a hyperlink; we reinject
                // the native click in that case so the link opens instead of the binding firing.
                CURSORINFO ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                GetCursorInfo(ref ci);
                bool onLink = (ci.hCursor == HandCursorH);

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
                            if (count == 1) {
                                Binding bSingle = ResolveMouseBinding(BSingleWheel);
                                // Reinject if the cursor was over a link at click time OR no binding is set.
                                if (bSingle != null && !onLink) ExecuteBinding(bSingle);
                                else ReinjectWheel(!wheelHeld || wheelUpSeen);
                            }
                            else if (count == 2) ExecuteBinding(ResolveMouseBinding(BDoubleWheel));
                            else                 ExecuteBinding(ResolveMouseBinding(BTripleWheel));
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

    static string GetForegroundProcessName() {
        try {
            IntPtr fgWnd = GetForegroundWindow();
            if (fgWnd == IntPtr.Zero) return "";
            uint pid;
            GetWindowThreadProcessId(fgWnd, out pid);
            if (pid == 0) return "";
            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return "";
            try {
                var sb = new System.Text.StringBuilder(1024);
                uint size = 1024;
                if (!QueryFullProcessImageName(hProc, 0, sb, ref size)) return "";
                return System.IO.Path.GetFileName(sb.ToString(0, (int)size));
            } finally { CloseHandle(hProc); }
        } catch { return ""; }
    }

    // Case-insensitive search — avoids lambdas so ref parameters can safely call this.
    static bool AppsContain(string[] apps, string fgApp) {
        if (apps == null) return false;
        for (int i = 0; i < apps.Length; i++)
            if (string.Equals(apps[i], fgApp, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Picks the right binding for the current foreground process: app-scoped first, global fallback.
    static Binding ResolveMouseBinding(List<Binding> list) {
        if (list == null || list.Count == 0) return null;
        if (list.Count == 1 && (list[0].Apps == null || list[0].Apps.Length == 0)) {
            var b0 = list[0];
            if (b0.ExceptApps != null && b0.ExceptApps.Length > 0 && AppsContain(b0.ExceptApps, GetForegroundProcessName())) return null;
            return b0;
        }
        string fgApp = GetForegroundProcessName();
        foreach (Binding b in list) {
            if (b.Apps != null && b.Apps.Length > 0 && AppsContain(b.Apps, fgApp)) return b;
        }
        foreach (Binding b in list) {
            if (b.Apps == null || b.Apps.Length == 0) {
                if (b.ExceptApps != null && b.ExceptApps.Length > 0 && AppsContain(b.ExceptApps, fgApp)) continue;
                return b;
            }
        }
        return null;
    }

    // fgApp is passed by ref so FindExact and HasStrictSuperset share the same lazy lookup per keydown.
    static Binding FindExact(int mods, byte[] sk, ref string fgApp) {
        if (sk.Length == 0) return null;
        List<Binding> candidates;
        if (!KeySigIndex.TryGetValue(MakeSignature(mods, sk), out candidates)) return null;
        // Fast path: single global binding (the common case)
        if (candidates.Count == 1 && (candidates[0].Apps == null || candidates[0].Apps.Length == 0)) {
            var c = candidates[0];
            if (c.ExceptApps != null && c.ExceptApps.Length > 0) {
                if (fgApp == null) fgApp = GetForegroundProcessName();
                if (AppsContain(c.ExceptApps, fgApp)) return null;
            }
            return c;
        }
        if (fgApp == null) fgApp = GetForegroundProcessName();
        // Scoped bindings are at the front; check them first
        foreach (Binding b in candidates) {
            if (b.Apps == null || b.Apps.Length == 0) continue;
            if (AppsContain(b.Apps, fgApp)) return b;
        }
        // Fall back to global binding, respecting ExceptApps
        foreach (Binding b in candidates) {
            if (b.Apps == null || b.Apps.Length == 0) {
                if (b.ExceptApps != null && b.ExceptApps.Length > 0 && AppsContain(b.ExceptApps, fgApp)) continue;
                return b;
            }
        }
        return null;
    }

    static bool HasStrictSuperset(int mods, HashSet<byte> keys, ref string fgApp) {
        if (keys.Count == 0) return false;
        foreach (Binding b in KeyBindings) {
            if (b.Mods != mods || b.Keys.Length <= keys.Count) continue;
            if (fgApp == null) fgApp = GetForegroundProcessName();
            if (b.Apps != null && b.Apps.Length > 0) {
                if (!AppsContain(b.Apps, fgApp)) continue;
            } else if (b.ExceptApps != null && b.ExceptApps.Length > 0 && AppsContain(b.ExceptApps, fgApp)) {
                continue;
            }
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
            if (IgnoredApps.Count > 0 && IgnoredApps.Contains(GetForegroundProcessName()))
                return CallNextHookEx(kbdHookId, nCode, wParam, lParam);

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
                                SendKey(VK_CONTROL, false);
                                SendKey(vk, true);
                                SendKey(VK_CONTROL, true);
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
                    string  fgApp = null; // lazily populated; shared between FindExact and HasStrictSuperset
                    Binding exact = FindExact(heldMods, sk, ref fgApp);
                    if (IsPaused) {
                        // Pass everything through except the binding(s) that resume us.
                        if (exact != null && HasTogglePause(exact)) {
                            swallowedKeys.Add(vk); prefixSwallowed.Clear(); CancelDefer();
                            ExecuteBinding(exact);
                            return new IntPtr(1);
                        }
                        return CallNextHookEx(kbdHookId, nCode, wParam, lParam);
                    }
                    bool    longer = HasStrictSuperset(heldMods, heldKeys, ref fgApp);
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
                            SendKey(vk, false);
                            SendKey(vk, true);
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
        IsPaused = false;
        SwitchProfileRequest = null;
        MainThreadId = GetCurrentThreadId();
        try { File.WriteAllText(PauseStatePath, "running"); } catch { }
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

        if (LaunchBindings.Count > 0 || ExitBindings.Count > 0 || FocusBindings.Count > 0 || BlurBindings.Count > 0) {
            ActiveWindows.Clear();
            KnownRunningApps.Clear();
            EnumWindows(PopulateExistingWindows, IntPtr.Zero);
            LastFocusedApp = GetForegroundProcessName();

            winEventProcDelegate = new WinEventProc(WinEventCallback);
            hFocusEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, winEventProcDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            hCreateDestroyEventHook = SetWinEventHook(
                EVENT_OBJECT_CREATE, EVENT_OBJECT_DESTROY,
                IntPtr.Zero, winEventProcDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        MSG msg; int r;
        while ((r = GetMessage(out msg, IntPtr.Zero, 0, 0)) > 0) {
            TranslateMessage(ref msg); DispatchMessage(ref msg);
        }

        if (hFocusEventHook != IntPtr.Zero) { UnhookWinEvent(hFocusEventHook); hFocusEventHook = IntPtr.Zero; }
        if (hCreateDestroyEventHook != IntPtr.Zero) { UnhookWinEvent(hCreateDestroyEventHook); hCreateDestroyEventHook = IntPtr.Zero; }
        UnhookWindowsHookEx(kbdHookId); UnhookWindowsHookEx(mouseHookId);
    }
}
