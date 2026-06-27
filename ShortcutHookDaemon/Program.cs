using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace ShortcutHookDaemon;

class Program
{
    static string LogPath = Path.Combine(AppContext.BaseDirectory, "ShortcutHook.log");

    static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }

    static void Main(string[] args)
    {
        Log($"=== Start (PID {Environment.ProcessId}) ===");

        var configPath = Path.Combine(AppContext.BaseDirectory, "shortcuts.json");
        ShortcutHook.PauseStatePath = Path.Combine(AppContext.BaseDirectory, "pause.state");

        // Load bindings
        ShortcutHook.Binding[] bindings;
        string profileName;
        bool altHScroll;
        try
        {
            (bindings, profileName, altHScroll) = ConfigLoader.LoadFromConfig(configPath);
            Log($"Loaded {bindings.Length} binding(s). Profile: {profileName}");
        }
        catch (Exception ex)
        {
            Log($"Config load failed: {ex}");
            return;
        }

        ShortcutHook.LoadBindings(bindings);
        ShortcutHook.CurrentProfileName = profileName;

        // Single-instance guard
        var mutex = new Mutex(true, @"Global\ShortcutHook", out bool created);
        if (!created)
        {
            Log("Already running.");
            return;
        }

        bool mutexReleased = false;
        try
        {
            ShortcutHook.Start();
            Log("Message loop exited.");

            // Handle profile switch request
            var profileSwitch = ShortcutHook.SwitchProfileRequest;
            if (profileSwitch != null)
            {
                Log($"Profile switch requested: {profileSwitch}");
                try
                {
                    var json = File.ReadAllText(configPath);
                    var cfg  = JsonSerializer.Deserialize<ConfigRoot>(json,
                                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    bool exists = cfg?.profiles?.Exists(p =>
                        string.Equals(p.name, profileSwitch, StringComparison.Ordinal)) == true;
                    if (exists)
                    {
                        cfg!.activeProfile = profileSwitch;
                        File.WriteAllText(configPath, JsonSerializer.Serialize(cfg,
                            new JsonSerializerOptions { WriteIndented = true }));
                        Log($"Switched active profile to: {profileSwitch}");

                        // Release mutex before relaunch so the new instance can acquire it
                        mutexReleased = true;
                        mutex.ReleaseMutex();
                        mutex.Dispose();

                        // Relaunch self
                        var exePath = Environment.ProcessPath
                                      ?? Process.GetCurrentProcess().MainModule?.FileName;
                        if (exePath != null)
                        {
                            Process.Start(new ProcessStartInfo(exePath)
                            {
                                WorkingDirectory = AppContext.BaseDirectory,
                                CreateNoWindow   = true,
                                UseShellExecute  = false,
                            });
                        }
                        return;
                    }
                    else
                    {
                        Log($"Profile switch target not found: {profileSwitch}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Profile switch failed: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex}");
        }
        finally
        {
            if (!mutexReleased)
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }
}
