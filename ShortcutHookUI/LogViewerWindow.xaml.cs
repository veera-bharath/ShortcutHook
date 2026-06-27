using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace ShortcutHookUI;

public partial class LogViewerWindow : Window
{
    static readonly string LogPath = Path.Combine(InstallService.ScriptRoot, "ShortcutHook.log");
    const int MaxLines = 2000;

    FileSystemWatcher? _watcher;
    bool _autoScroll = true;

    public LogViewerWindow()
    {
        InitializeComponent();
        LogPathText.Text = LogPath;
        LoadContent();
        StartWatcher();
    }

    void LoadContent()
    {
        string text;
        if (!File.Exists(LogPath))
        {
            text = "(Log file does not exist yet — start the daemon to generate log output.)";
        }
        else
        {
            try
            {
                using var fs  = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr  = new StreamReader(fs, Encoding.UTF8);
                var lines     = new System.Collections.Generic.Queue<string>();
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    lines.Enqueue(line);
                    if (lines.Count > MaxLines) lines.Dequeue();
                }
                text = lines.Count == MaxLines
                    ? $"(showing last {MaxLines} lines)\r\n" + string.Join("\r\n", lines)
                    : string.Join("\r\n", lines);
            }
            catch (Exception ex)
            {
                text = $"(Could not read log: {ex.Message})";
            }
        }

        LogBox.Text = text;
        LastUpdatedText.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss");

        if (_autoScroll)
            Dispatcher.InvokeAsync(() => LogScroller.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
    }

    void StartWatcher()
    {
        try
        {
            var dir = InstallService.ScriptRoot;
            if (!Directory.Exists(dir)) return;

            _watcher = new FileSystemWatcher(dir, "ShortcutHook.log")
            {
                NotifyFilter         = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents  = true,
            };
            _watcher.Changed += OnLogChanged;
            _watcher.Created += OnLogChanged;
        }
        catch { }
    }

    void OnLogChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.InvokeAsync(LoadContent);
    }

    void RefreshBtn_Click(object sender, RoutedEventArgs e) => LoadContent();

    void OpenFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(LogPath))
        {
            MessageBox.Show("Log file does not exist yet.", "No Log File", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{InstallService.ScriptRoot}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void ClearLogBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Clear all log entries?\n\nThis cannot be undone.",
            "Clear Log",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        try
        {
            if (File.Exists(LogPath))
            {
                using var fs = new FileStream(LogPath, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite);
            }
            LoadContent();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not clear log: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void Window_Closed(object sender, EventArgs e)
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int one = 1;
        DwmApi.DwmSetWindowAttribute(hwnd, DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE, ref one, 4);
    }
}
