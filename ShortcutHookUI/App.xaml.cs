using System.Windows;
using System.IO;
using System;

namespace ShortcutHookUI;

public partial class App : Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            File.WriteAllText(@"D:\Projects\ShortcutHook\crash.log", e.ExceptionObject.ToString());
        };
        this.DispatcherUnhandledException += (s, e) =>
        {
            File.WriteAllText(@"D:\Projects\ShortcutHook\crash.log", e.Exception.ToString());
            e.Handled = true;
        };
    }
}
