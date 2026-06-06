# ShortcutHook.ps1  v4.0
# System-wide low-level hook that maps mouse gestures and keyboard combos to
# configurable outputs: keyboard chords OR shell-execute (open app/file/folder).
#
# Mouse gestures: left+right, left+rightx2, left+rightx3,
#                 double-right, double-right-sel, triple-right,
#                 right-scroll-down, right-scroll-up,
#                 single-wheel, double-wheel, triple-wheel
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
using System.IO;
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
        public byte[] Output;    // keyboard chord; null when OpenPath/CmdLine is set
        public string OpenPath;  // shell-execute target; null when Output/CmdLine is set
        public string CmdLine;   // cmd.exe command; null when Output/OpenPath is set
        public bool   CmdShow;   // true = visible cmd window (/k), false = hidden (/c)
    }

    public class Binding {
        public string      Kind;          // "mouse" | "key"
        public string      MouseGesture;
        public int         Mods;
        public byte[]      Keys;
        public string      Signature;
        public string[]    Apps;          // null/empty = global; list of process names for app-scoped
        public int         OutputDelay;   // ms between chained steps (0 = no delay)
        public ChainStep[] Steps;         // one or more actions to execute in order
    }

    public static List<Binding> MouseBindings = new List<Binding>();
    public static List<Binding> KeyBindings   = new List<Binding>();
    // Each signature maps to an ordered list: app-scoped bindings first, global last.
    public static Dictionary<string, List<Binding>> KeySigIndex = new Dictionary<string, List<Binding>>();

    public static bool altScrollEnabled = false;

    // Per-gesture binding lists: app-scoped entries first, global last (mirrors KeySigIndex ordering).
    public static List<Binding> BLeftRight        = new List<Binding>();
    public static List<Binding> BLeftRightDouble  = new List<Binding>();
    public static List<Binding> BLeftRightTriple  = new List<Binding>();
    public static List<Binding> BDoubleRight      = new List<Binding>();
    public static List<Binding> BDoubleRightSel   = new List<Binding>();
    public static List<Binding> BTripleRight      = new List<Binding>();
    public static List<Binding> BRightScrollDown  = new List<Binding>();
    public static List<Binding> BRightScrollUp    = new List<Binding>();
    public static List<Binding> BSingleWheel      = new List<Binding>();
    public static List<Binding> BDoubleWheel      = new List<Binding>();
    public static List<Binding> BTripleWheel      = new List<Binding>();

    public static string MakeSignature(int mods, byte[] sortedKeys) {
        string[] parts = new string[sortedKeys.Length];
        for (int i = 0; i < sortedKeys.Length; i++) parts[i] = ((int)sortedKeys[i]).ToString();
        return mods + ":" + String.Join(",", parts);
    }

    public static void LoadBindings(Binding[] bindings) {
        MouseBindings.Clear(); KeyBindings.Clear(); KeySigIndex.Clear();
        BLeftRight.Clear(); BLeftRightDouble.Clear(); BLeftRightTriple.Clear();
        BDoubleRight.Clear(); BDoubleRightSel.Clear(); BTripleRight.Clear();
        BRightScrollDown.Clear(); BRightScrollUp.Clear();
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
        addGesture("right-scroll-down", BRightScrollDown);
        addGesture("right-scroll-up",   BRightScrollUp);
        addGesture("single-wheel",      BSingleWheel);
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

    // Injects Ctrl+C, waits CLIP_WAIT_MS, then checks whether clipboard gained data.
    // If no selection found, restores the clipboard to its prior state.
    static bool DetectTextSelection() {
        int explorerSelCount = GetExplorerSelectionCount();
        if (explorerSelCount >= 0) {
            return explorerSelCount > 0;
        }
        
        // Fallback selection detection (text, files, etc. in other apps)
        var saved = BackupClipboard();
        ClearClipboard();
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(0x43, 0, 0, UIntPtr.Zero);
        keybd_event(0x43, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(CLIP_WAIT_MS);
        
        bool hasSel = ClipboardHasData();
        if (hasSel) {
            // Check if it's an Explorer default/invisible selection (e.g. $RECYCLE.BIN)
            byte[] hdropData = GetClipboardFormatData(CF_HDROP);
            if (hdropData != null) {
                var files = GetCopiedFiles(hdropData);
                if (IsExplorerInvisibleSelection(files)) {
                    hasSel = false;
                }
            }
        }

        if (!hasSel) {
            RestoreClipboard(saved);
        } else {
            FreeBackup(saved); // Free GDI copies to prevent leaks when backup is discarded
        }
        return hasSel;
    }
    static void ExecuteDoubleRight() {
        if (BDoubleRightSel.Count == 0) { ExecuteBinding(ResolveMouseBinding(BDoubleRight)); return; }
        bool hasSel = DetectTextSelection();
        ExecuteBinding(ResolveMouseBinding(hasSel ? BDoubleRightSel : BDoubleRight));
    }

    // ---------- Output dispatch ----------
    static void ExecuteStep(ChainStep step) {
        if (step == null) return;
        if (step.OpenPath != null) {
            bool uWinL = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0;
            bool uWinR = (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
            // Don't inject a synthetic Win-up here — that itself triggers Start Menu.
            // Instead mark that we need to release Win at physical Win-up time, where
            // we can sandwich it between Ctrl events to break the clean-tap sequence.
            if (uWinL || uWinR) lock (KLock) { suppressWinUp = true; releaseWinOnSuppress = true; }
            string path = step.OpenPath;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        } else if (step.CmdLine != null) {
            bool uWinL = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0;
            bool uWinR = (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
            if (uWinL || uWinR) lock (KLock) { suppressWinUp = true; releaseWinOnSuppress = true; }
            string cmd = step.CmdLine;
            bool show = step.CmdShow;
            try {
                if (show) {
                    Process.Start(new ProcessStartInfo("cmd.exe") {
                        Arguments = "/k " + cmd,
                        UseShellExecute = true,
                    });
                } else {
                    Process.Start(new ProcessStartInfo("cmd.exe") {
                        Arguments = "/c " + cmd,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                }
            } catch { }
        } else {
            FireOutput(step.Output);
        }
    }

    static void ExecuteBinding(Binding b) {
        if (b == null || b.Steps == null || b.Steps.Length == 0) return;
        // Single-action with no delay: fast path — run inline (FireOutput is safe in hook callback).
        if (b.Steps.Length == 1 && b.OutputDelay == 0 && b.Steps[0].Output != null) {
            FireOutput(b.Steps[0].Output);
            return;
        }
        // Chain or open/cmd step: run on background thread so the callback returns quickly.
        ChainStep[] steps = b.Steps;
        int delay = b.OutputDelay;
        new Thread(() => {
            for (int i = 0; i < steps.Length; i++) {
                if (i > 0 && delay > 0) Thread.Sleep(delay);
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
    static int  lrGen           = 0;
    static int  lrCount         = 0;
    static bool lrPending       = false;

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

    // Picks the right binding for the current foreground process: app-scoped first, global fallback.
    static Binding ResolveMouseBinding(List<Binding> list) {
        if (list == null || list.Count == 0) return null;
        if (list.Count == 1 && (list[0].Apps == null || list[0].Apps.Length == 0)) return list[0]; // fast path: single global
        string fgApp = GetForegroundProcessName();
        foreach (Binding b in list) {
            if (b.Apps != null && b.Apps.Length > 0 && Array.Exists(b.Apps, a => string.Equals(a, fgApp, StringComparison.OrdinalIgnoreCase))) return b;
        }
        foreach (Binding b in list) { if (b.Apps == null || b.Apps.Length == 0) return b; }
        return null;
    }

    // fgApp is passed by ref so FindExact and HasStrictSuperset share the same lazy lookup per keydown.
    static Binding FindExact(int mods, byte[] sk, ref string fgApp) {
        if (sk.Length == 0) return null;
        List<Binding> candidates;
        if (!KeySigIndex.TryGetValue(MakeSignature(mods, sk), out candidates)) return null;
        // Fast path: single global binding (the common case)
        if (candidates.Count == 1 && (candidates[0].Apps == null || candidates[0].Apps.Length == 0)) return candidates[0];
        if (fgApp == null) fgApp = GetForegroundProcessName();
        // Scoped bindings are at the front; check them first
        foreach (Binding b in candidates) {
            if (b.Apps == null || b.Apps.Length == 0) continue;
            if (Array.Exists(b.Apps, a => string.Equals(a, fgApp, StringComparison.OrdinalIgnoreCase))) return b;
        }
        // Fall back to global binding
        foreach (Binding b in candidates) {
            if (b.Apps == null || b.Apps.Length == 0) return b;
        }
        return null;
    }

    static bool HasStrictSuperset(int mods, HashSet<byte> keys, ref string fgApp) {
        if (keys.Count == 0) return false;
        foreach (Binding b in KeyBindings) {
            if (b.Mods != mods || b.Keys.Length <= keys.Count) continue;
            if (b.Apps != null && b.Apps.Length > 0) {
                if (fgApp == null) fgApp = GetForegroundProcessName();
                if (Array.IndexOf(b.Apps, fgApp) < 0) continue;
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
                    string  fgApp = null; // lazily populated; shared between FindExact and HasStrictSuperset
                    Binding exact = FindExact(heldMods, sk, ref fgApp);
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
    if ($mods -eq 1 -and $arr.Length -eq 1 -and $arr[0] -ge 0x41 -and $arr[0] -le 0x5A) {
        throw "Trigger '$combo' is restricted because 'Ctrl + single letter' conflicts with standard system shortcuts. Please use a multi-key combo (e.g. Ctrl+K+C) or add another modifier (e.g. Ctrl+Shift+C)."
    }
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
$validGestures = @('left+right','left+rightx2','left+rightx3','double-right','double-right-sel','triple-right','right-scroll-down','right-scroll-up','single-wheel','double-wheel','triple-wheel')
$built = New-Object System.Collections.Generic.List[ShortcutHook+Binding]

foreach ($b in $rawBindings) {
    try {
        if ($b.PSObject.Properties.Name -contains 'enabled' -and $b.enabled -eq $false) {
            Write-Log "Skip disabled binding: $($b.trigger)"; continue
        }
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

        # Resolve outputs: prefer 'outputs' array, fall back to legacy 'output' string.
        $outputsList = $null
        if ($b.PSObject.Properties.Name -contains 'outputs' -and $b.outputs -and @($b.outputs).Count -gt 0) {
            $outputsList = @($b.outputs)
        } elseif ($b.PSObject.Properties.Name -contains 'output' -and $b.output) {
            $outputsList = @($b.output)
        }
        if (-not $outputsList -or $outputsList.Count -eq 0) { Write-Log "Skip no-output binding: $($b.trigger)"; continue }

        $steps = New-Object System.Collections.Generic.List[ShortcutHook+ChainStep]
        foreach ($outStr in $outputsList) {
            $step = [ShortcutHook+ChainStep]::new()
            if ($outStr -match '^open:(.+)$') {
                $step.OpenPath = $Matches[1].Trim()
            } elseif ($outStr -match '^cmdw:(.+)$') {
                $step.CmdLine = $Matches[1].Trim(); $step.CmdShow = $true
            } elseif ($outStr -match '^cmd:(.+)$') {
                $cmdVal = $Matches[1].Trim()
                if ($cmdVal -match '^cmd:(.+)$') { $cmdVal = $Matches[1].Trim() }
                $step.CmdLine = $cmdVal; $step.CmdShow = $false
            } else {
                $step.Output = Resolve-OutputChord $outStr
            }
            $steps.Add($step)
        }
        $nb.Steps = $steps.ToArray()

        if ($b.PSObject.Properties.Name -contains 'outputDelay' -and $b.outputDelay -gt 0) {
            $nb.OutputDelay = [int]$b.outputDelay
        }
        # apps[] (multi-app) takes precedence; fall back to legacy app string
        if ($b.PSObject.Properties.Name -contains 'apps' -and $b.apps -and @($b.apps).Count -gt 0) {
            $nb.Apps = [string[]]@($b.apps | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        } elseif ($b.PSObject.Properties.Name -contains 'app' -and $b.app) {
            $nb.Apps = [string[]]@($b.app.Trim())
        }
        $built.Add($nb)
    } catch {
        $outRef = if ($b.PSObject.Properties.Name -contains 'outputs') { ($b.outputs -join ', ') } else { $b.output }
        Write-Log "Skip '$($b.trigger)' -> '$outRef': $_"
    }
}

try {
    [ShortcutHook]::LoadBindings($built.ToArray())
    Write-Log ("Loaded {0} binding(s)." -f $built.Count)
    foreach ($x in $built) {
        $stepDescs = @()
        foreach ($s in $x.Steps) {
            if ($s.OpenPath) { $stepDescs += "open:$($s.OpenPath)" }
            elseif ($s.CmdLine) {
                if ($s.CmdShow) { $stepDescs += "cmdw:$($s.CmdLine)" }
                else { $stepDescs += "cmd:$($s.CmdLine)" }
            } else { $stepDescs += ("[{0}]" -f ($s.Output -join ',')) }
        }
        $dest  = [string]::Join(' -> ', $stepDescs)
        $delay = if ($x.OutputDelay -gt 0) { " [delay:$($x.OutputDelay)ms]" } else { '' }
        $scope = if ($x.Apps -and $x.Apps.Length -gt 0) { " [apps:$($x.Apps -join ',')]" } else { '' }
        if ($x.Kind -eq 'mouse') { Write-Log ("  mouse:{0} -> {1}{2}{3}" -f $x.MouseGesture, $dest, $delay, $scope) }
        else { Write-Log ("  key:mods={0} keys=[{1}] -> {2}{3}{4}" -f $x.Mods, ($x.Keys -join ','), $dest, $delay, $scope) }
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
    foreach ($b in $rawBindings) {
        $outDisplay = $b.output
        if ($b.PSObject.Properties.Name -contains 'outputs' -and $b.outputs) {
            $outDisplay = [string]::Join(' -> ', @($b.outputs))
        }
        $appDisplay = ''
        if ($b.PSObject.Properties.Name -contains 'app' -and $b.app) { $appDisplay = "  [app:$($b.app)]" }
        Write-Host ("  {0,-28} ->  {1}{2}" -f $b.trigger, $outDisplay, $appDisplay) -ForegroundColor Cyan
    }
    Write-Host 'Ctrl+C to stop.' -ForegroundColor DarkGray
    [ShortcutHook]::Start()
    Write-Log 'Message loop exited normally.'
} catch {
    Write-Log "ERROR: $_"; Write-Host "ERROR: $_" -ForegroundColor Red; Read-Host 'Press Enter to exit'
} finally {
    $mutex.ReleaseMutex(); $mutex.Dispose()
}
