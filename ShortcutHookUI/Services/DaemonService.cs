using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ShortcutHookUI.Services;

internal static class DaemonService
{
    const string MutexName = @"Global\ShortcutHook";

    public static bool IsRunning()
    {
        try
        {
            using var m = Mutex.OpenExisting(MutexName);
            return true;
        }
        catch { return false; }
    }

    public static void Start()
    {
        var daemon = InstallService.DaemonPath;
        if (!File.Exists(daemon))
            throw new FileNotFoundException("ShortcutHookDaemon.exe is not installed.", daemon);
        var psi = new ProcessStartInfo(daemon)
        {
            WorkingDirectory = InstallService.ScriptRoot,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        Process.Start(psi);
    }

    public static void Stop()
    {
        foreach (var p in Process.GetProcessesByName("ShortcutHookDaemon"))
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch { }
            p.Dispose();
        }
    }
}
