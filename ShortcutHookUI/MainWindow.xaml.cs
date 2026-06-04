using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ShortcutHookUI;

public enum ActionKind { Shortcut, OpenApp, OpenFile, OpenFolder, Command }

public sealed class Row
{
    public Grid       Container   = null!;
    public Grid       OutputPanel = null!;
    public ActionKind Action;
    public string     OutputValue = "";      // shortcut (record mode), open-file, open-folder
    public object?    OutputCtrl;            // TextBox / ComboBox / TextBlock / Button, depends on action+mode
    public bool       ShortcutRecordMode = true;

    // mouse-only
    public string?    MouseGesture;
    public string     Label = "";

    // keyboard-only
    public Button?    CaptureBtn;
    public string     Trigger = "";          // "" or "key:..."
    public string?    App;                   // null = global; process name for app-scoped
    public TextBox?   AppTextBox;

    // command-only
    public CheckBox?  CmdShowCheckBox;
}

public partial class MainWindow : Window
{
    static readonly string[] ActionLabels = { "Trigger shortcut", "Open app", "Open file", "Open folder", "Run command" };
    static readonly ActionKind[] ActionOrder = { ActionKind.Shortcut, ActionKind.OpenApp, ActionKind.OpenFile, ActionKind.OpenFolder, ActionKind.Command };

    static readonly MouseGestureDef[] MouseDefs = {
        new("left+right",        "Left + Right click"),
        new("left+rightx2",      "Left hold + Right x2"),
        new("double-right",      "Right click twice"),
        new("double-right-sel",  "Right click twice (text selected)"),
        new("triple-right",      "Right click thrice"),
        new("right-scroll-down", "Right hold + Scroll Down"),
        new("right-scroll-up",   "Right hold + Scroll Up"),
        new("double-wheel",      "Wheel double click"),
        new("triple-wheel",      "Wheel triple click"),
    };

    static readonly Dictionary<Key,string> KeyDisplay = BuildKeyDisplay();
    static Dictionary<Key,string> BuildKeyDisplay()
    {
        var d = new Dictionary<Key,string>();
        for (var k = Key.A; k <= Key.Z; k++) d[k] = k.ToString();
        d[Key.D0] = "0"; d[Key.D1] = "1"; d[Key.D2] = "2"; d[Key.D3] = "3"; d[Key.D4] = "4";
        d[Key.D5] = "5"; d[Key.D6] = "6"; d[Key.D7] = "7"; d[Key.D8] = "8"; d[Key.D9] = "9";
        for (var k = Key.F1; k <= Key.F12; k++) d[k] = k.ToString();
        d[Key.Return]     = "Enter";
        d[Key.Tab]        = "Tab";
        d[Key.Space]      = "Space";
        d[Key.Back]       = "Back";
        d[Key.Delete]     = "Delete";
        d[Key.Insert]     = "Insert";
        d[Key.Home]       = "Home";
        d[Key.End]        = "End";
        d[Key.PageUp]     = "PgUp";
        d[Key.PageDown]   = "PgDn";
        d[Key.Left]       = "Left";
        d[Key.Right]      = "Right";
        d[Key.Up]         = "Up";
        d[Key.Down]       = "Down";
        d[Key.PrintScreen]= "PrtScr";
        d[Key.Escape]     = "Esc";
        return d;
    }
    static readonly Dictionary<Key,int> ModBits = new()
    {
        [Key.LeftCtrl]  = TriggerHelpers.MOD_CTRL,  [Key.RightCtrl]  = TriggerHelpers.MOD_CTRL,
        [Key.LeftShift] = TriggerHelpers.MOD_SHIFT, [Key.RightShift] = TriggerHelpers.MOD_SHIFT,
        [Key.LeftAlt]   = TriggerHelpers.MOD_ALT,   [Key.RightAlt]   = TriggerHelpers.MOD_ALT,
        [Key.LWin]      = TriggerHelpers.MOD_WIN,   [Key.RWin]       = TriggerHelpers.MOD_WIN,
    };

    // Colors / brushes
    static readonly Brush AmberBrush = Br("#F0A020");
    static readonly Brush GreenBrush = Br("#3DBA7B");
    static readonly Brush RedBrush   = Br("#E85C5C");
    static readonly Brush DimBrush   = Br("#555555");
    static readonly Brush TextBrush  = Br("#E8E8E8");
    static readonly Brush DarkBorder = Br("#2E2E2E");
    static readonly Brush BtnHoverBg = Br("#1F1F1F");
    static readonly Brush Transparent = System.Windows.Media.Brushes.Transparent;
    static Brush Br(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    static void UpdateAppTextBoxStyle(TextBox tb)
    {
        if (string.IsNullOrWhiteSpace(tb.Text))
            tb.ClearValue(TextBox.BorderBrushProperty);
        else
            tb.BorderBrush = AmberBrush;
    }

    readonly List<AppEntry> _apps;
    readonly Dictionary<string, Row> _mouseRows = new();
    readonly List<Row> _kbdRows = new();
    string _appRoot;
    bool _setupComplete;
    CheckBox? _altHScrollToggle;

    // Capture state — plain C# fields, no scoping issues.
    bool            _captureActive;
    Button?         _captureBtn;
    Action<string>? _captureOnCommit;   // called with composed combo (e.g. "Ctrl+S+L")
    Action?         _captureOnRestore;  // restore button's pre-capture appearance
    int             _captureMods;
    readonly List<Key> _captureNonMods = new();

    // Low-level keyboard hook (active only during capture) — swallows system hotkeys
    // like Win+Shift+S so Windows doesn't fire them.
    IntPtr _captureHookId = IntPtr.Zero;
    HookApi.LowLevelKeyboardProc? _captureHookProc;

    bool _mouseExpanded = true;
    bool _kbdExpanded   = false;

    readonly DispatcherTimer _pollTimer;
    readonly DispatcherTimer _feedbackTimer;

    public MainWindow()
    {
        InitializeComponent();
        _appRoot = InstallService.TryGetConfiguredAppRoot(out var configuredRoot)
            ? configuredRoot
            : InstallService.DefaultAppRoot;
        _apps = AppScanner.Scan();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pollTimer.Tick += (_, __) => UpdateHookStatus();

        _feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _feedbackTimer.Tick += (_, __) => { FeedbackMsg.Visibility = Visibility.Collapsed; _feedbackTimer.Stop(); };

        SourceInitialized += OnSourceInitialized;
        Loaded            += OnLoaded;
        Closed            += (_, __) => { _pollTimer.Stop(); _feedbackTimer.Stop(); UninstallCaptureHook(); };
        Deactivated       += (_, __) => { if (_captureActive) EndCapture(); };

        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewKeyUp   += OnWindowPreviewKeyUp;
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int one = 1;
        DwmApi.DwmSetWindowAttribute(hwnd, DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE, ref one, 4);
        DwmApi.DwmSetWindowAttribute(hwnd, DwmApi.DWMWA_CAPTION_COLOR,           ref one, 4);
    }

    void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshInstallState();
        ReloadBindingsFromConfig();
        ApplySectionState();
        UpdateHookStatus();
        _setupComplete = InstallService.IsSetupComplete()
                         && InstallService.IsInstalled()
                         && InstallService.TryGetConfiguredAppRoot(out _appRoot)
                         && InstallService.IsAppInstalled(_appRoot);
        StartupToggle.IsChecked = _setupComplete && StartupService.IsEnabled();
        UpdateSetupState();
        _pollTimer.Start();
    }

    // =========================================================================
    // Accordion
    // =========================================================================
    void MouseHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSection(mouse: true);
    void KbdHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)   => ToggleSection(mouse: false);

    void ToggleSection(bool mouse)
    {
        if (mouse) { _mouseExpanded = !_mouseExpanded; if (_mouseExpanded) _kbdExpanded = false; }
        else       { _kbdExpanded   = !_kbdExpanded;   if (_kbdExpanded)   _mouseExpanded = false; }
        ApplySectionState();
    }

    void ApplySectionState()
    {
        MouseBody.Visibility = _mouseExpanded ? Visibility.Visible   : Visibility.Collapsed;
        MouseChevron.Text    = _mouseExpanded ? "▾" : "›";
        KbdBody.Visibility   = _kbdExpanded   ? Visibility.Visible   : Visibility.Collapsed;
        KbdChevron.Text      = _kbdExpanded   ? "▾" : "›";
    }

    // =========================================================================
    // Status + hook control
    // =========================================================================
    void UpdateHookStatus()
    {
        if (!InstallService.IsInstalled())
        {
            StatusDot.Fill  = AmberBrush;
            StatusText.Text = "Runtime not installed";
            HookBtn.Content = "Start";
            HookBtn.Background = GreenBrush;
            HookBtn.IsEnabled = false;
            return;
        }

        HookBtn.IsEnabled = true;
        if (DaemonService.IsRunning())
        {
            StatusDot.Fill  = GreenBrush;
            StatusText.Text = "Running";
            HookBtn.Content = "Stop";
            HookBtn.Background = RedBrush;
        }
        else
        {
            StatusDot.Fill  = RedBrush;
            StatusText.Text = "Stopped";
            HookBtn.Content = "Start";
            HookBtn.Background = GreenBrush;
        }
    }

    void HookBtn_Click(object sender, RoutedEventArgs e)
    {
        HookBtn.IsEnabled = false;
        try
        {
            if (DaemonService.IsRunning()) { DaemonService.Stop(); UpdateHookStatus(); }
            else
            {
                DaemonService.Start();
                StatusDot.Fill = AmberBrush;
                StatusText.Text = "Starting...";
                HookBtn.Content = "...";
            }
        }
        catch (Exception ex)
        {
            ShowFeedback($"Hook change failed: {ex.Message}", FeedbackKind.Err);
            UpdateHookStatus();
        }
        HookBtn.IsEnabled = true;
    }

    void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        try { StartupService.Set(StartupToggle.IsChecked == true); }
        catch (Exception ex)
        {
            ShowFeedback($"Startup change failed: {ex.Message}", FeedbackKind.Err);
            StartupToggle.IsChecked = !StartupToggle.IsChecked;
        }
    }

    // =========================================================================
    // Installation
    // =========================================================================
    void RefreshInstallState()
    {
        InstallService.TryGetConfiguredAppRoot(out _appRoot);
        StartupToggle.IsEnabled = InstallService.IsInstalled();
        UpdateHookStatus();
    }

    void UpdateSetupState()
    {
        var scriptInstalled = InstallService.IsInstalled();
        var appConfigured   = InstallService.TryGetConfiguredAppRoot(out _appRoot);
        var appInstalled    = appConfigured && InstallService.IsAppInstalled(_appRoot);
        var fullyInstalled  = scriptInstalled && appInstalled;
        var showSetup       = !_setupComplete || !fullyInstalled;

        MainAppRoot.Visibility = showSetup ? Visibility.Collapsed : Visibility.Visible;
        SetupRoot.Visibility   = showSetup ? Visibility.Visible   : Visibility.Collapsed;

        SetupAppFolderText.Text    = appConfigured ? _appRoot : InstallService.DefaultAppRoot;
        SetupScriptFolderText.Text = InstallService.ScriptRoot;

        SetupInstallStatusText.Text = fullyInstalled
            ? $"Installed — app at {_appRoot}, script at {InstallService.ScriptRoot}"
            : scriptInstalled
                ? $"Script installed. App not found at {_appRoot}."
                : "Not installed.";
        SetupInstallStatusText.Foreground = fullyInstalled ? GreenBrush : TextBrush;

        SetupOpenFolderBtn.IsEnabled = Directory.Exists(InstallService.ScriptRoot);
        SetupInstallBtn.Content = fullyInstalled ? "Reinstall" : "Choose App Folder and Install";
        CompleteSetupBtn.IsEnabled = fullyInstalled;
        SetupHintText.Text = fullyInstalled
            ? (_setupComplete ? "Setup complete." : "Setup is ready. Choose optional shortcuts and finish.")
            : "Install first to continue.";
    }

    void ReloadBindingsFromConfig()
    {
        if (_captureActive) EndCapture();
        KbdStack.Children.Clear();
        _kbdRows.Clear();

        BuildMouseRows();
        foreach (var b in ConfigService.Read(InstallService.ScriptRoot))
            if (b.trigger.StartsWith("key:", StringComparison.Ordinal))
                AddKbdRow(b.trigger, b.output, b.app);
    }

    void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where to install the ShortcutHook app. The exe will be placed directly in this folder.",
            Multiselect = false
        };

        if (Directory.Exists(_appRoot))
            dialog.InitialDirectory = _appRoot;
        else if (Directory.Exists(InstallService.DefaultAppRoot))
            dialog.InitialDirectory = InstallService.DefaultAppRoot;

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        try
        {
            var appRoot = dialog.FolderName.Trim();
            InstallService.Install(appRoot);
            _appRoot = appRoot;
            _setupComplete = false;
            RefreshInstallState();
            ReloadBindingsFromConfig();
            StartupToggle.IsChecked = StartupService.IsEnabled();
            UpdateSetupState();
            ShowFeedback($"Installed. App: {_appRoot} | Script: {InstallService.ScriptRoot}", FeedbackKind.Ok);
        }
        catch (Exception ex)
        {
            var msg = $"Install failed: {ex.Message}";
            ShowFeedback(msg, FeedbackKind.Err);
            if (!_setupComplete) SetupHintText.Text = msg;
        }
    }

    void OpenInstallBtn_Click(object sender, RoutedEventArgs e)
    {
        try { InstallService.OpenScriptFolder(); }
        catch (Exception ex)
        {
            var msg = $"Open folder failed: {ex.Message}";
            ShowFeedback(msg, FeedbackKind.Err);
            if (!_setupComplete) SetupHintText.Text = msg;
        }
    }

    void CompleteSetupBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!InstallService.IsInstalled())
                throw new InvalidOperationException("Install before finishing setup.");
            if (!InstallService.TryGetConfiguredAppRoot(out _appRoot) || !InstallService.IsAppInstalled(_appRoot))
                throw new InvalidOperationException("App not installed. Run install first.");

            if (SetupStartMenuCheckbox.IsChecked == true)
                InstallService.CreateStartMenuShortcut(_appRoot);

            if (SetupDesktopCheckbox.IsChecked == true)
                InstallService.CreateDesktopShortcut(_appRoot);

            InstallService.MarkSetupComplete();
            _setupComplete = true;
            UpdateSetupState();
            StartupToggle.IsChecked = StartupService.IsEnabled();
            ShowFeedback("Setup complete.", FeedbackKind.Ok);

            if (!InstallService.IsRunningFromInstalledLocation(_appRoot))
            {
                InstallService.LaunchInstalledApp(_appRoot);
                Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            var msg = $"Setup failed: {ex.Message}";
            SetupHintText.Text = msg;
            ShowFeedback(msg, FeedbackKind.Err);
        }
    }

    void AboutBtn_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    // =========================================================================
    // Feedback toast
    // =========================================================================
    enum FeedbackKind { Ok, Warn, Err }
    void ShowFeedback(string msg, FeedbackKind kind)
    {
        FeedbackMsg.Foreground = kind switch
        {
            FeedbackKind.Err  => RedBrush,
            FeedbackKind.Warn => AmberBrush,
            _                 => GreenBrush,
        };
        FeedbackMsg.Text = msg;
        FeedbackMsg.Visibility = Visibility.Visible;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    // =========================================================================
    // Mouse rows
    // =========================================================================
    void BuildMouseRows()
    {
        MouseStack.Children.Clear();
        _mouseRows.Clear();
        _altHScrollToggle = null;

        var configRoot = ConfigService.ReadConfig(InstallService.ScriptRoot);
        var cfgMap = new Dictionary<string,string>();
        foreach (var b in configRoot.bindings)
            if (b.trigger.StartsWith("mouse:", StringComparison.Ordinal))
                cfgMap[b.trigger.Substring(6)] = b.output;

        foreach (var def in MouseDefs)
        {
            cfgMap.TryGetValue(def.Gesture, out var stored);
            stored ??= "";
            var action = DetectAction(stored);

            var grid = new Grid { Margin = new Thickness(0,0,0,5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lbl = new TextBlock {
                Text = def.Label, Foreground = Br("#CCCCCC"),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lbl, 0);

            var actionCB = NewActionCombo(action);
            Grid.SetColumn(actionCB, 1);

            var outPanel = new Grid { Margin = new Thickness(8,0,0,0) };
            Grid.SetColumn(outPanel, 2);

            grid.Children.Add(lbl);
            grid.Children.Add(actionCB);
            grid.Children.Add(outPanel);
            MouseStack.Children.Add(grid);

            var row = new Row {
                Container   = grid,
                OutputPanel = outPanel,
                Action      = action,
                OutputValue = stored,
                MouseGesture= def.Gesture,
                Label       = def.Label,
            };
            _mouseRows[def.Gesture] = row;
            SetRowOutput(row, action);

            actionCB.SelectionChanged += (_, __) =>
            {
                var idx = actionCB.SelectedIndex;
                if (idx >= 0 && idx < ActionOrder.Length) SetRowOutput(row, ActionOrder[idx]);
            };
        }

        // Separator
        MouseStack.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Height = 1, Fill = Br("#333333"), Margin = new Thickness(0, 8, 0, 8)
        });

        // Hardcoded: Alt + Scroll Wheel → Horizontal Scroll (with toggle)
        var altRow = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        altRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
        altRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        altRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var altLbl = new TextBlock
        {
            Text = "Alt + Scroll Wheel",
            Foreground = Br("#CCCCCC"), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(altLbl, 0);

        var altDesc = new TextBlock
        {
            Text = "→ Horizontal Scroll",
            Foreground = Br("#888888"), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(altDesc, 1);

        _altHScrollToggle = new CheckBox
        {
            Style     = (Style)FindResource("Toggle"),
            IsChecked = configRoot.altHScroll,
            Margin    = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(_altHScrollToggle, 2);

        altRow.Children.Add(altLbl);
        altRow.Children.Add(altDesc);
        altRow.Children.Add(_altHScrollToggle);
        MouseStack.Children.Add(altRow);
    }

    // =========================================================================
    // Keyboard rows
    // =========================================================================
    void AddKbdBtn_Click(object sender, RoutedEventArgs e) => AddKbdRow("", "");

    void AddKbdRow(string triggerStr, string outputStr, string? app = null)
    {
        var initAction = DetectAction(outputStr);

        var grid = new Grid { Margin = new Thickness(0,0,0,5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        var capBtn = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Height  = 28,
            Padding = new Thickness(6,0,6,0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 11,
        };
        Grid.SetColumn(capBtn, 0);

        var actionCB = NewActionCombo(initAction);
        Grid.SetColumn(actionCB, 1);

        var outPanel = new Grid { Margin = new Thickness(8,0,0,0) };
        Grid.SetColumn(outPanel, 2);

        var appTB = new TextBox
        {
            Style      = (Style)FindResource("DarkTB"),
            Height     = 28,
            Margin     = new Thickness(6,0,0,0),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 11,
            Text       = app ?? "",
            ToolTip    = "App filter — leave empty for all apps, or enter a process name (e.g. Code.exe)",
        };
        UpdateAppTextBoxStyle(appTB);
        appTB.TextChanged += (_, __) => UpdateAppTextBoxStyle(appTB);
        Grid.SetColumn(appTB, 3);

        var delBtn = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Content = "✕",
            Height  = 28,
            Margin  = new Thickness(6,0,0,0),
            Padding = new Thickness(0),
        };
        Grid.SetColumn(delBtn, 4);

        grid.Children.Add(capBtn);
        grid.Children.Add(actionCB);
        grid.Children.Add(outPanel);
        grid.Children.Add(appTB);
        grid.Children.Add(delBtn);

        var displayTrig = !string.IsNullOrEmpty(triggerStr) && triggerStr.StartsWith("key:", StringComparison.Ordinal)
            ? triggerStr.Substring(4)
            : "Click to record...";
        capBtn.Content    = displayTrig;
        capBtn.Foreground = !string.IsNullOrEmpty(triggerStr) ? TextBrush : DimBrush;

        var row = new Row
        {
            Container   = grid,
            OutputPanel = outPanel,
            Action      = initAction,
            OutputValue = outputStr,
            Trigger     = triggerStr,
            CaptureBtn  = capBtn,
            App         = app,
            AppTextBox  = appTB,
        };
        SetRowOutput(row, initAction);

        actionCB.SelectionChanged += (_, __) =>
        {
            var idx = actionCB.SelectedIndex;
            if (idx >= 0 && idx < ActionOrder.Length) SetRowOutput(row, ActionOrder[idx]);
        };

        delBtn.Click += (_, __) =>
        {
            if (_captureActive && _captureBtn == capBtn) EndCapture();
            KbdStack.Children.Remove(row.Container);
            _kbdRows.Remove(row);
        };

        capBtn.Click += (_, __) => BeginCapture(
            btn: capBtn,
            onCommit: combo =>
            {
                try
                {
                    TriggerHelpers.CanonicalizeTrigger("key:" + combo);
                    row.Trigger = "key:" + combo;
                    RestoreTriggerButton(capBtn, row.Trigger);
                }
                catch (Exception ex)
                {
                    ShowFeedback(ex.Message, FeedbackKind.Err);
                    RestoreTriggerButton(capBtn, row.Trigger);
                }
            },
            onRestore: () => RestoreTriggerButton(capBtn, row.Trigger));

        KbdStack.Children.Add(grid);
        _kbdRows.Add(row);
    }

    // =========================================================================
    // Key capture
    // =========================================================================
    void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_captureActive) return;
        e.Handled = true;
        var k = e.Key == Key.System ? e.SystemKey : e.Key;

        if (ModBits.TryGetValue(k, out var bit))
        {
            _captureMods |= bit;
            UpdateCaptureDisplay();
            return;
        }

        if (k == Key.Escape && _captureMods == 0 && _captureNonMods.Count == 0)
        {
            EndCapture();
            return;
        }

        if (KeyDisplay.ContainsKey(k) && !_captureNonMods.Contains(k))
            _captureNonMods.Add(k);

        UpdateCaptureDisplay();
    }

    void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_captureActive) return;
        e.Handled = true;
        var k = e.Key == Key.System ? e.SystemKey : e.Key;

        if (ModBits.TryGetValue(k, out var bit))
        {
            if (_captureNonMods.Count > 0) FinalizeCapture();
            else { _captureMods &= ~bit; UpdateCaptureDisplay(); }
            return;
        }
        if (_captureNonMods.Count > 0) FinalizeCapture();
    }

    string ComposeCaptureString()
    {
        var parts = new List<string>();
        if ((_captureMods & TriggerHelpers.MOD_WIN)   != 0) parts.Add("Win");
        if ((_captureMods & TriggerHelpers.MOD_CTRL)  != 0) parts.Add("Ctrl");
        if ((_captureMods & TriggerHelpers.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((_captureMods & TriggerHelpers.MOD_ALT)   != 0) parts.Add("Alt");
        foreach (var k in _captureNonMods) parts.Add(KeyDisplay[k]);
        return string.Join("+", parts);
    }

    void UpdateCaptureDisplay()
    {
        if (_captureBtn is null) return;
        var s = ComposeCaptureString();
        _captureBtn.Content = string.IsNullOrEmpty(s) ? "● Press keys..." : ("● " + s);
    }

    void BeginCapture(Button btn, Action<string> onCommit, Action onRestore)
    {
        if (_captureActive)
        {
            if (_captureBtn == btn) { EndCapture(); return; }
            EndCapture();
        }
        _captureActive    = true;
        _captureBtn       = btn;
        _captureOnCommit  = onCommit;
        _captureOnRestore = onRestore;
        _captureMods      = 0;
        _captureNonMods.Clear();
        btn.Background  = AmberBrush;
        btn.Foreground  = Br("#111111");
        btn.BorderBrush = AmberBrush;
        btn.Content     = "● Press keys...";
        var btnRef = btn;
        Dispatcher.BeginInvoke(DispatcherPriority.Input,
            new Action(() => Keyboard.Focus(btnRef)));
        InstallCaptureHook();
    }

    void InstallCaptureHook()
    {
        if (_captureHookId != IntPtr.Zero) return;
        _captureHookProc = CaptureHookCallback;
        _captureHookId = HookApi.SetWindowsHookEx(
            HookApi.WH_KEYBOARD_LL, _captureHookProc,
            HookApi.GetModuleHandle(null), 0);
        // If hook install fails, fall back to WPF PreviewKeyDown handlers (which can't
        // swallow system hotkeys, but at least handle non-hotkey combos).
    }

    void UninstallCaptureHook()
    {
        if (_captureHookId == IntPtr.Zero) return;
        HookApi.UnhookWindowsHookEx(_captureHookId);
        _captureHookId   = IntPtr.Zero;
        _captureHookProc = null;
    }

    IntPtr CaptureHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode < 0 || !_captureActive)
                return HookApi.CallNextHookEx(_captureHookId, nCode, wParam, lParam);

            var data = Marshal.PtrToStructure<HookApi.KBDLLHOOKSTRUCT>(lParam);
            if ((data.flags & HookApi.LLKHF_INJECTED) != 0)
                return HookApi.CallNextHookEx(_captureHookId, nCode, wParam, lParam);

            var msg = wParam.ToInt32();
            bool isDown = msg == HookApi.WM_KEYDOWN || msg == HookApi.WM_SYSKEYDOWN;
            bool isUp   = msg == HookApi.WM_KEYUP   || msg == HookApi.WM_SYSKEYUP;
            if (!isDown && !isUp)
                return HookApi.CallNextHookEx(_captureHookId, nCode, wParam, lParam);

            Key wpfKey;
            try { wpfKey = KeyInterop.KeyFromVirtualKey((int)data.vkCode); }
            catch { return HookApi.CallNextHookEx(_captureHookId, nCode, wParam, lParam); }

            if (ModBits.TryGetValue(wpfKey, out var bit))
            {
                if (isDown)      _captureMods |= bit;
                else if (isUp)
                {
                    if (_captureNonMods.Count > 0) { FinalizeCapture(); return new IntPtr(1); }
                    _captureMods &= ~bit;
                }
                UpdateCaptureDisplay();
                return new IntPtr(1);
            }

            if (isDown)
            {
                if (wpfKey == Key.Escape && _captureMods == 0 && _captureNonMods.Count == 0)
                {
                    EndCapture();
                    return new IntPtr(1);
                }
                if (KeyDisplay.ContainsKey(wpfKey) && !_captureNonMods.Contains(wpfKey))
                    _captureNonMods.Add(wpfKey);
                UpdateCaptureDisplay();
                return new IntPtr(1);
            }

            if (isUp)
            {
                if (_captureNonMods.Count > 0) FinalizeCapture();
                return new IntPtr(1);
            }
        }
        catch { /* swallow — hook callbacks must not throw across native boundary */ }
        return HookApi.CallNextHookEx(_captureHookId, nCode, wParam, lParam);
    }

    void FinalizeCapture()
    {
        var s = ComposeCaptureString();
        if (string.IsNullOrEmpty(s)) return;
        var onCommit = _captureOnCommit;
        ClearCaptureState();
        onCommit?.Invoke(s);
    }

    void EndCapture()
    {
        var onRestore = _captureOnRestore;
        ClearCaptureState();
        onRestore?.Invoke();
    }

    void ClearCaptureState()
    {
        UninstallCaptureHook();
        _captureActive    = false;
        _captureBtn       = null;
        _captureOnCommit  = null;
        _captureOnRestore = null;
        _captureMods      = 0;
        _captureNonMods.Clear();
    }

    void RestoreTriggerButton(Button btn, string trigger)
    {
        var disp = !string.IsNullOrEmpty(trigger) && trigger.StartsWith("key:", StringComparison.Ordinal)
            ? trigger.Substring(4) : "Click to record...";
        btn.Content    = disp;
        btn.Foreground = !string.IsNullOrEmpty(trigger) ? TextBrush : DimBrush;
        btn.Background = Transparent;
        btn.BorderBrush = DarkBorder;
    }

    // =========================================================================
    // Action selection + output panel swap
    // =========================================================================
    ComboBox NewActionCombo(ActionKind initial)
    {
        var cb = new ComboBox
        {
            Style  = (Style)FindResource("DarkCB"),
            Height = 28,
            Margin = new Thickness(8,0,0,0),
        };
        foreach (var label in ActionLabels) cb.Items.Add(label);
        cb.SelectedIndex = Array.IndexOf(ActionOrder, initial);
        if (cb.SelectedIndex < 0) cb.SelectedIndex = 0;
        return cb;
    }

    ActionKind DetectAction(string output)
    {
        if (output.StartsWith("cmd:", StringComparison.Ordinal) ||
            output.StartsWith("cmdw:", StringComparison.Ordinal)) return ActionKind.Command;
        if (string.IsNullOrEmpty(output) || !output.StartsWith("open:", StringComparison.Ordinal))
            return ActionKind.Shortcut;
        var p = output.Substring(5);
        try { if (Directory.Exists(p)) return ActionKind.OpenFolder; } catch { }
        if (_apps.Any(a => string.Equals(a.Path, p, StringComparison.OrdinalIgnoreCase))) return ActionKind.OpenApp;
        return ActionKind.OpenFile;
    }

    void SetRowOutput(Row row, ActionKind action)
    {
        // Snapshot current shortcut-text / command value into row.OutputValue before tearing down.
        if (row.Action == ActionKind.Shortcut && row.OutputCtrl is TextBox prevTB)
            row.OutputValue = prevTB.Text.Trim();
        if (row.Action == ActionKind.Command && row.OutputCtrl is TextBox prevCmd && !string.IsNullOrWhiteSpace(prevCmd.Text))
            row.OutputValue = (row.CmdShowCheckBox?.IsChecked == true ? "cmdw:" : "cmd:") + prevCmd.Text.Trim();

        // If we're in mid-capture on a button belonging to this row's output, cancel it.
        if (_captureActive && ReferenceEquals(_captureBtn, row.OutputCtrl)) EndCapture();

        row.OutputPanel.Children.Clear();
        row.Action          = action;
        row.OutputCtrl      = null;
        row.CmdShowCheckBox = null;

        switch (action)
        {
            case ActionKind.Shortcut:
                BuildShortcutOutput(row);
                break;
            case ActionKind.OpenApp:
            {
                var cb = new ComboBox
                {
                    Style       = (Style)FindResource("DarkCB"),
                    Height      = 28,
                    ItemsSource = _apps,
                };
                var curPath = row.OutputValue.StartsWith("open:", StringComparison.Ordinal)
                    ? row.OutputValue.Substring(5) : "";
                if (!string.IsNullOrEmpty(curPath))
                {
                    var match = _apps.FirstOrDefault(a => string.Equals(a.Path, curPath, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) cb.SelectedItem = match;
                }
                row.OutputPanel.Children.Add(cb);
                row.OutputCtrl = cb;
                break;
            }
            case ActionKind.OpenFile:
                AddBrowsePanel(row, isFolder: false);
                break;
            case ActionKind.OpenFolder:
                AddBrowsePanel(row, isFolder: true);
                break;
            case ActionKind.Command:
            {
                bool isShow = row.OutputValue.StartsWith("cmdw:", StringComparison.Ordinal);
                string cmdText = isShow ? row.OutputValue.Substring(5)
                               : row.OutputValue.StartsWith("cmd:", StringComparison.Ordinal)
                                     ? row.OutputValue.Substring(4) : "";

                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var lbl = new TextBlock
                {
                    Text = "cmd:",
                    Foreground = DimBrush,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 4, 0),
                };
                Grid.SetColumn(lbl, 0);

                var tb = new TextBox
                {
                    Style      = (Style)FindResource("DarkTB"),
                    Height     = 28,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 11,
                    Text       = cmdText,
                };
                Grid.SetColumn(tb, 1);

                var showCb = new CheckBox
                {
                    Content   = "Show",
                    Foreground = Br("#CCCCCC"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin    = new Thickness(8, 0, 2, 0),
                    IsChecked = isShow,
                };
                Grid.SetColumn(showCb, 2);

                g.Children.Add(lbl);
                g.Children.Add(tb);
                g.Children.Add(showCb);
                row.OutputPanel.Children.Add(g);
                row.OutputCtrl      = tb;
                row.CmdShowCheckBox = showCb;
                break;
            }
        }
    }

    void BuildShortcutOutput(Row row)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        FrameworkElement main;
        if (row.ShortcutRecordMode)
        {
            var recBtn = new Button
            {
                Style = (Style)FindResource("BtnGhost"),
                Height = 28,
                Padding = new Thickness(6,0,6,0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
            };
            RestoreOutputRecordButton(recBtn, row.OutputValue);

            recBtn.Click += (_, __) => BeginCapture(
                btn: recBtn,
                onCommit: combo =>
                {
                    row.OutputValue = combo;
                    RestoreOutputRecordButton(recBtn, row.OutputValue);
                },
                onRestore: () => RestoreOutputRecordButton(recBtn, row.OutputValue));

            main = recBtn;
            row.OutputCtrl = recBtn;
        }
        else
        {
            var tb = new TextBox { Style = (Style)FindResource("DarkTB"), Height = 28 };
            tb.Text = !string.IsNullOrEmpty(row.OutputValue) &&
                      !row.OutputValue.StartsWith("open:", StringComparison.Ordinal)
                ? row.OutputValue : "";
            main = tb;
            row.OutputCtrl = tb;
        }
        Grid.SetColumn(main, 0);

        var toggle = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Height  = 28,
            Margin  = new Thickness(6,0,0,0),
            Padding = new Thickness(0),
            FontSize = 13,
            Content  = row.ShortcutRecordMode ? "Aa" : "⌨",   // ⌨
            ToolTip  = row.ShortcutRecordMode ? "Switch to typing" : "Switch to recording",
        };
        Grid.SetColumn(toggle, 1);

        toggle.Click += (_, __) =>
        {
            row.ShortcutRecordMode = !row.ShortcutRecordMode;
            SetRowOutput(row, ActionKind.Shortcut);
        };

        g.Children.Add(main);
        g.Children.Add(toggle);
        row.OutputPanel.Children.Add(g);
    }

    void RestoreOutputRecordButton(Button btn, string value)
    {
        var hasValue = !string.IsNullOrEmpty(value) && !value.StartsWith("open:", StringComparison.Ordinal);
        btn.Content     = hasValue ? value : "Click to record...";
        btn.Foreground  = hasValue ? TextBrush : DimBrush;
        btn.Background  = Transparent;
        btn.BorderBrush = DarkBorder;
    }

    void AddBrowsePanel(Row row, bool isFolder)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

        var pathTB = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 11,
            Margin            = new Thickness(0,0,6,0),
        };
        var curPath = row.OutputValue.StartsWith("open:", StringComparison.Ordinal)
            ? row.OutputValue.Substring(5) : "";
        if (!string.IsNullOrEmpty(curPath))
        {
            pathTB.Text       = curPath;
            pathTB.Foreground = Br("#CCCCCC");
        }
        else
        {
            pathTB.Text       = "No path selected";
            pathTB.Foreground = DimBrush;
        }
        Grid.SetColumn(pathTB, 0);

        var browseBtn = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Content = "Browse...",
            Height  = 28,
            Padding = new Thickness(8,0,8,0),
        };
        Grid.SetColumn(browseBtn, 1);

        browseBtn.Click += (_, __) =>
        {
            if (!isFolder)
            {
                var ofd = new Microsoft.Win32.OpenFileDialog { Title = "Select a file", Filter = "All files (*.*)|*.*" };
                if (ofd.ShowDialog(this) == true)
                {
                    row.OutputValue  = "open:" + ofd.FileName;
                    pathTB.Text      = ofd.FileName;
                    pathTB.Foreground = Br("#CCCCCC");
                }
            }
            else
            {
                using var fbd = new System.Windows.Forms.FolderBrowserDialog { Description = "Select a folder" };
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    row.OutputValue  = "open:" + fbd.SelectedPath;
                    pathTB.Text      = fbd.SelectedPath;
                    pathTB.Foreground = Br("#CCCCCC");
                }
            }
        };

        g.Children.Add(pathTB);
        g.Children.Add(browseBtn);
        row.OutputPanel.Children.Add(g);
        row.OutputCtrl = pathTB;
    }

    string GetRowOutput(Row row) => row.Action switch
    {
        ActionKind.Shortcut   => row.ShortcutRecordMode
                                    ? row.OutputValue
                                    : (row.OutputCtrl as TextBox)?.Text.Trim() ?? "",
        ActionKind.OpenApp    => (row.OutputCtrl is ComboBox cb && cb.SelectedItem is AppEntry a) ? "open:" + a.Path : "",
        ActionKind.OpenFile   => row.OutputValue,
        ActionKind.OpenFolder => row.OutputValue,
        ActionKind.Command    => (row.OutputCtrl is TextBox cmdTb && !string.IsNullOrWhiteSpace(cmdTb.Text))
                                    ? (row.CmdShowCheckBox?.IsChecked == true ? "cmdw:" : "cmd:") + cmdTb.Text.Trim() : "",
        _                     => "",
    };

    // =========================================================================
    // Save
    // =========================================================================
    void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_captureActive) EndCapture();

        var entries   = new List<BindingEntry>();
        var canonSeen = new Dictionary<string,int>();

        foreach (var def in MouseDefs)
        {
            var row  = _mouseRows[def.Gesture];
            var outp = GetRowOutput(row);
            if (string.IsNullOrEmpty(outp)) continue;
            if (row.Action == ActionKind.Shortcut)
            {
                try { TriggerHelpers.ValidateShortcutOutput(outp); }
                catch (Exception ex) { ShowFeedback($"Mouse '{def.Label}': {ex.Message}", FeedbackKind.Err); return; }
            }
            entries.Add(new BindingEntry { trigger = "mouse:" + def.Gesture, output = outp });
        }

        var keyParsed = new List<(string Trigger, ParsedKey Parsed, int Row)>();
        int idx = 0;
        foreach (var r in _kbdRows)
        {
            idx++;
            var trig    = r.Trigger;
            var outp    = GetRowOutput(r);
            var appStr  = r.AppTextBox?.Text.Trim() ?? "";
            if (string.IsNullOrEmpty(trig) && string.IsNullOrEmpty(outp)) continue;
            if (string.IsNullOrEmpty(trig)) { ShowFeedback($"Keyboard row {idx}: no trigger recorded.", FeedbackKind.Err); return; }
            if (string.IsNullOrEmpty(outp)) { ShowFeedback($"Keyboard row {idx}: no output configured.", FeedbackKind.Err); return; }

            string canon;
            try { canon = TriggerHelpers.CanonicalizeTrigger(trig); }
            catch (Exception ex) { ShowFeedback($"Keyboard row {idx}: {ex.Message}", FeedbackKind.Err); return; }

            if (r.Action == ActionKind.Shortcut)
            {
                try { TriggerHelpers.ValidateShortcutOutput(outp); }
                catch (Exception ex) { ShowFeedback($"Keyboard row {idx} (output): {ex.Message}", FeedbackKind.Err); return; }
            }
            // Dedup key includes app scope: same trigger + same app is a duplicate, different apps are valid.
            var dedupKey = canon + "|" + appStr.ToLowerInvariant();
            if (canonSeen.ContainsKey(dedupKey)) { ShowFeedback($"Keyboard row {idx}: duplicate trigger{(appStr.Length > 0 ? $" for app '{appStr}'" : "")}.", FeedbackKind.Err); return; }
            canonSeen[dedupKey] = idx;

            var parsed = TriggerHelpers.ParseKeyTrigger(trig.Substring(4));
            keyParsed.Add((trig, parsed, idx));
            entries.Add(new BindingEntry { trigger = trig, output = outp, app = appStr.Length > 0 ? appStr : null });
        }

        if (entries.Count == 0) { ShowFeedback("Add at least one binding before saving.", FeedbackKind.Err); return; }

        var prefixPairs = new List<string>();
        for (int i = 0; i < keyParsed.Count; i++)
            for (int j = 0; j < keyParsed.Count; j++)
                if (i != j && TriggerHelpers.IsKeyPrefixOf(keyParsed[i].Parsed, keyParsed[j].Parsed))
                    prefixPairs.Add($"{keyParsed[i].Trigger} -> {keyParsed[j].Trigger}");

        var configToSave = new ConfigRoot
        {
            altHScroll = _altHScrollToggle?.IsChecked == true,
            bindings   = entries,
        };
        try { ConfigService.Save(InstallService.ScriptRoot, configToSave); }
        catch (Exception ex) { ShowFeedback($"Save failed: {ex.Message}", FeedbackKind.Err); return; }

        var wasRunning = DaemonService.IsRunning();
        if (wasRunning)
        {
            DaemonService.Stop();
            try { DaemonService.Start(); }
            catch (Exception ex) { ShowFeedback($"Saved, but restart failed: {ex.Message}", FeedbackKind.Err); UpdateHookStatus(); return; }
            StatusDot.Fill = AmberBrush;
            StatusText.Text = "Restarting...";
        }

        if (prefixPairs.Count > 0)
        {
            var prefix = wasRunning ? "Saved, restarting. " : "Saved. ";
            ShowFeedback(prefix + "Prefix pair(s): " + string.Join("; ", prefixPairs) + ". Shorter fires after ~80 ms.", FeedbackKind.Warn);
        }
        else if (wasRunning) ShowFeedback("Saved — daemon restarting.", FeedbackKind.Ok);
        else                 ShowFeedback("Settings saved.", FeedbackKind.Ok);
    }
}
