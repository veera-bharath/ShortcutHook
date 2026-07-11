using System.Diagnostics;
using System.Text.Json;
using ShortcutHookDaemon;
using ConfigRoot = ShortcutHookCore.Models.ConfigRoot;

var logPath    = Path.Combine(AppContext.BaseDirectory, "ShortcutHook.log");
var configPath = Path.Combine(AppContext.BaseDirectory, "shortcuts.json");

ShortcutHook.PauseStatePath = Path.Combine(AppContext.BaseDirectory, "pause.state");

void Log(string msg)
{
    try { File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}"); }
    catch { }
}

Log($"=== Start (PID {Environment.ProcessId}) ===");

ShortcutHook.Binding[] bindings;
string profileName;

try
{
    (bindings, profileName, _) = ConfigLoader.LoadFromConfig(configPath);
    Log($"Loaded {bindings.Length} binding(s). Profile: {profileName}");
}
catch (Exception ex)
{
    Log($"Config load failed: {ex}");
    return;
}

ShortcutHook.LoadBindings(bindings);
ShortcutHook.CurrentProfileName = profileName;

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

    var profileSwitch = ShortcutHook.SwitchProfileRequest;
    if (profileSwitch != null)
    {
        Log($"Profile switch requested: {profileSwitch}");
        try
        {
            var cfg = JsonSerializer.Deserialize<ConfigRoot>(
                File.ReadAllText(configPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (cfg?.profiles?.Exists(p => string.Equals(p.name, profileSwitch, StringComparison.Ordinal)) == true)
            {
                cfg.activeProfile = profileSwitch;
                File.WriteAllText(configPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
                Log($"Switched active profile to: {profileSwitch}");

                mutexReleased = true;
                mutex.ReleaseMutex();
                mutex.Dispose();

                var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (exe != null)
                    Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = true, UseShellExecute = false });

                return;
            }
            else
            {
                Log($"Profile switch target not found: {profileSwitch}");
            }
        }
        catch (Exception ex) { Log($"Profile switch failed: {ex}"); }
    }
}
catch (Exception ex) { Log($"ERROR: {ex}"); }
finally
{
    if (!mutexReleased)
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
