using System;
using System.IO;

namespace ShortcutHookUI.Services;

internal static class StartupService
{
    static string LnkPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "ShortcutHook.lnk");

    public static bool IsEnabled() => File.Exists(LnkPath());

    public static void Set(bool enable)
    {
        var lnk = LnkPath();
        if (enable)
        {
            var daemon = InstallService.DaemonPath;
            if (!File.Exists(daemon))
                throw new FileNotFoundException("ShortcutHookDaemon.exe is not installed.", daemon);
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell unavailable");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic s = shell.CreateShortcut(lnk);
            s.TargetPath       = daemon;
            s.Arguments        = "";
            s.WorkingDirectory = InstallService.ScriptRoot;
            s.WindowStyle      = 0; // hidden — no console window on startup
            s.Save();
        }
        else
        {
            if (File.Exists(lnk)) File.Delete(lnk);
        }
    }
}
