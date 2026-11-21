using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShortcutHookDaemon;

public static class BrightnessController
{
    private static readonly object _syncLock = new();
    private static int _pendingDelta = 0;
    private static bool _isProcessing = false;

    public static void Adjust(int delta)
    {
        lock (_syncLock)
        {
            _pendingDelta += delta;
            if (!_isProcessing)
            {
                _isProcessing = true;
                ThreadPool.QueueUserWorkItem(_ => ProcessQueue());
            }
        }
    }

    private static void ProcessQueue()
    {
        while (true)
        {
            int delta;
            lock (_syncLock)
            {
                if (_pendingDelta == 0)
                {
                    _isProcessing = false;
                    return;
                }
                delta = _pendingDelta;
                _pendingDelta = 0;
            }

            try
            {
                ApplyBrightnessDelta(delta);
            }
            catch
            {
                // Hook/daemon safety: never throw out
            }

            Thread.Sleep(40);
        }
    }

    private static void ApplyBrightnessDelta(int delta)
    {
        if (delta == 0) return;

        // 1. Try DDC/CI on the active monitor under the cursor / focused window
        if (TryDdcCiActive(delta)) return;

        // 2. Try DDC/CI across all enumerated monitors
        if (TryDdcCiAll(delta)) return;

        // 3. Fallback to WMI for internal laptop displays
        TryWmi(delta);
    }

    private static bool TryDdcCiActive(int delta)
    {
        try
        {
            IntPtr hMon = IntPtr.Zero;
            if (GetCursorPos(out POINT pt))
            {
                hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            }
            if (hMon == IntPtr.Zero)
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    hMon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
                }
            }
            if (hMon == IntPtr.Zero) return false;

            return AdjustHMonitorBrightness(hMon, delta);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDdcCiAll(int delta)
    {
        try
        {
            var monitors = new List<IntPtr>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
            {
                monitors.Add(hMon);
                return true;
            }, IntPtr.Zero);

            bool success = false;
            foreach (var hMon in monitors)
            {
                if (AdjustHMonitorBrightness(hMon, delta))
                {
                    success = true;
                }
            }
            return success;
        }
        catch
        {
            return false;
        }
    }

    private static bool AdjustHMonitorBrightness(IntPtr hMon, int delta)
    {
        if (hMon == IntPtr.Zero) return false;
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint numPhys) || numPhys == 0)
            return false;

        var phys = new PHYSICAL_MONITOR[numPhys];
        if (!GetPhysicalMonitorsFromHMONITOR(hMon, numPhys, phys))
            return false;

        bool anySuccess = false;
        try
        {
            for (int i = 0; i < numPhys; i++)
            {
                var hPhys = phys[i].hPhysicalMonitor;
                if (hPhys == IntPtr.Zero) continue;

                if (GetMonitorBrightness(hPhys, out uint min, out uint cur, out uint max))
                {
                    int target = Math.Clamp((int)cur + delta, (int)min, (int)max);
                    if (SetMonitorBrightness(hPhys, (uint)target))
                    {
                        anySuccess = true;
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }
        finally
        {
            DestroyPhysicalMonitors(numPhys, phys);
        }

        return anySuccess;
    }

    private static bool TryWmi(int delta)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness, Active FROM WmiMonitorBrightness");
            using var collection = searcher.Get();
            int currentBrightness = -1;
            foreach (ManagementObject mo in collection)
            {
                if (mo["Active"] is bool active && !active) continue;
                if (mo["CurrentBrightness"] != null)
                {
                    currentBrightness = Convert.ToInt32(mo["CurrentBrightness"]);
                    break;
                }
            }

            if (currentBrightness < 0) return false;

            int target = Math.Clamp(currentBrightness + delta, 0, 100);

            using var methodSearcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var methodCollection = methodSearcher.Get();
            bool updated = false;
            foreach (ManagementObject mo in methodCollection)
            {
                if (mo["Active"] is bool active && !active) continue;
                using var inParams = mo.GetMethodParameters("WmiSetBrightness");
                inParams["Timeout"] = (uint)1;
                inParams["Brightness"] = (byte)target;
                mo.InvokeMethod("WmiSetBrightness", inParams, null);
                updated = true;
            }
            return updated;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
}
