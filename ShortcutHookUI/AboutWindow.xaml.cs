using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Navigation;

namespace ShortcutHookUI;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;

        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        VersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int one = 1;
        DwmApi.DwmSetWindowAttribute(hwnd, DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE, ref one, 4);
        DwmApi.DwmSetWindowAttribute(hwnd, DwmApi.DWMWA_CAPTION_COLOR,           ref one, 4);
    }

    void GitHubLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
