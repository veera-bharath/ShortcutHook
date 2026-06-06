using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public ComboBox?  AppCombo;

    // command-only
    public CheckBox?  CmdShowCheckBox;

    // enabled state
    public bool      Enabled       = true;
    public CheckBox? EnabledToggle;
}

public sealed class KbdTriggerCard
{
    public Border     CardBorder   = null!;
    public StackPanel VariantStack = null!;
    public Button     CaptureBtn   = null!;
    public string     Trigger      = "";
    public List<Row>  Variants     = new();
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
    static readonly Brush LabelBrush = Br("#CCCCCC");
    static readonly Brush DarkBorder = Br("#2E2E2E");
    static readonly Brush BtnHoverBg = Br("#1F1F1F");
    static readonly Brush Transparent = System.Windows.Media.Brushes.Transparent;
    static Brush Br(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    static string? GetAppComboValue(ComboBox? cb)
    {
        var s = cb?.SelectedItem as string;
        return string.IsNullOrEmpty(s) || s == "All apps" ? null : s;
    }

    static void PopulateAppCombo(ComboBox cb, string? current)
    {
        cb.Items.Clear();
        cb.Items.Add("All apps");

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { if (!string.IsNullOrEmpty(p.ProcessName)) names.Add(p.ProcessName + ".exe"); }
                catch { }
            }
        }
        foreach (var n in names) cb.Items.Add(n);

        // Keep the stored value selectable even if that process isn't currently running.
        if (!string.IsNullOrWhiteSpace(current) && !names.Contains(current))
            cb.Items.Add(current);

        if (string.IsNullOrWhiteSpace(current))
        {
            cb.SelectedIndex = 0;
            return;
        }
        for (int i = 1; i < cb.Items.Count; i++)
            if (string.Equals(cb.Items[i] as string, current, StringComparison.OrdinalIgnoreCase))
                { cb.SelectedIndex = i; return; }
        cb.SelectedIndex = 0;
    }

    static void UpdateAppComboStyle(ComboBox cb)
    {
        bool scoped = GetAppComboValue(cb) != null;
        cb.BorderBrush = scoped ? AmberBrush : DarkBorder;
        cb.Foreground  = scoped ? AmberBrush : LabelBrush;
    }

    readonly List<AppEntry> _apps;
    readonly Dictionary<string, List<Row>> _mouseRows = new();
    readonly Dictionary<string, StackPanel> _mouseGestureStacks = new();
    readonly List<KbdTriggerCard> _kbdCards = new();
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
        _kbdCards.Clear();

        BuildMouseRows();

        // Group keyboard bindings by trigger to build trigger cards.
        var triggerOrder  = new List<string>();
        var triggerGroups = new Dictionary<string, List<BindingEntry>>(StringComparer.Ordinal);
        foreach (var b in ConfigService.Read(InstallService.ScriptRoot))
        {
            if (!b.trigger.StartsWith("key:", StringComparison.Ordinal)) continue;
            if (!triggerGroups.TryGetValue(b.trigger, out var list))
            {
                list = new List<BindingEntry>();
                triggerGroups[b.trigger] = list;
                triggerOrder.Add(b.trigger);
            }
            list.Add(b);
        }
        foreach (var trig in triggerOrder)
        {
            var variants = triggerGroups[trig]
                .Select(b => (b.output, b.app, b.enabled != false))
                .ToList();
            AddKbdTriggerCard(trig, variants);
        }
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
        _mouseGestureStacks.Clear();
        _altHScrollToggle = null;

        var configRoot = ConfigService.ReadConfig(InstallService.ScriptRoot);

        // Group mouse bindings by gesture; sort each group: global first, then app-scoped.
        var gestureGroups = new Dictionary<string, List<BindingEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in configRoot.bindings)
        {
            if (!b.trigger.StartsWith("mouse:", StringComparison.Ordinal)) continue;
            var g = b.trigger.Substring(6);
            if (!gestureGroups.TryGetValue(g, out var list)) { list = new List<BindingEntry>(); gestureGroups[g] = list; }
            list.Add(b);
        }

        foreach (var def in MouseDefs)
        {
            gestureGroups.TryGetValue(def.Gesture, out var bindings);

            var gestureSP = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            _mouseGestureStacks[def.Gesture] = gestureSP;
            _mouseRows[def.Gesture] = new List<Row>();

            // Global row (app == null).
            var globalEntry = bindings?.FirstOrDefault(b => b.app == null);
            AddMouseVariantRow(def, gestureSP, globalEntry?.output ?? "", null, globalEntry?.enabled != false, isGlobal: true);

            // App-scoped variant rows.
            if (bindings != null)
                foreach (var b in bindings.Where(b => b.app != null))
                    AddMouseVariantRow(def, gestureSP, b.output, b.app, b.enabled != false, isGlobal: false);

            MouseStack.Children.Add(gestureSP);
        }

        // Separator
        MouseStack.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Height = 1, Fill = Br("#333333"), Margin = new Thickness(0, 8, 0, 8)
        });

        // Alt + Scroll Wheel → Horizontal Scroll
        var altRow = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        altRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });
        altRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        altRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var altLbl = new TextBlock { Text = "Alt + Scroll Wheel", Foreground = Br("#CCCCCC"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(altLbl, 0);

        var altDesc = new TextBlock { Text = "→ Horizontal Scroll", Foreground = Br("#888888"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(altDesc, 1);

        _altHScrollToggle = new CheckBox { Style = (Style)FindResource("Toggle"), IsChecked = configRoot.altHScroll, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(_altHScrollToggle, 2);

        altRow.Children.Add(altLbl);
        altRow.Children.Add(altDesc);
        altRow.Children.Add(_altHScrollToggle);
        MouseStack.Children.Add(altRow);
    }

    void AddMouseVariantRow(MouseGestureDef def, StackPanel container, string output, string? app, bool enabled, bool isGlobal)
    {
        var action = DetectAction(output);

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        // col0=175: gesture label (global) or indent arrow (variant)
        // col1=120: action combo
        // col2=*:   output panel
        // col3=100: app combo
        // col4=Auto: enable toggle
        // col5=Auto: add (+) or delete (×) button — Auto so margin doesn't overflow a fixed column
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // col0: label or indent
        if (isGlobal)
        {
            var lbl = new TextBlock { Text = def.Label, Foreground = Br("#CCCCCC"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);
        }
        else
        {
            var arrow = new TextBlock { Text = "↳", Foreground = Br("#3A3A3A"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(arrow, 0);
            grid.Children.Add(arrow);
        }

        var actionCB = NewActionCombo(action);
        actionCB.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(actionCB, 1);

        var outPanel = new Grid { Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(outPanel, 2);

        // col3: editable app combo for all rows; global row defaults to "All apps"
        var appCB = new ComboBox
        {
            Style      = (Style)FindResource("DarkCB"),
            Height     = 28,
            Margin     = new Thickness(6, 0, 0, 0),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 11,
            ToolTip    = "App scope — 'All apps' fires everywhere; pick a specific app to scope this gesture",
        };
        PopulateAppCombo(appCB, app);
        UpdateAppComboStyle(appCB);
        appCB.SelectionChanged += (_, __) => UpdateAppComboStyle(appCB);
        appCB.DropDownOpened   += (_, __) => { var cur = GetAppComboValue(appCB); PopulateAppCombo(appCB, cur); };
        Grid.SetColumn(appCB, 3);
        grid.Children.Add(appCB);

        var enableToggle = new CheckBox { Style = (Style)FindResource("Toggle"), IsChecked = enabled, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Enable this binding" };
        Grid.SetColumn(enableToggle, 4);

        // col5: "+" for global row, "×" for variant rows
        var actionBtn = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Content = isGlobal ? "+" : "✕",
            Height  = 26,
            Width   = 26,
            Padding = new Thickness(0),
            Margin  = new Thickness(4, 0, 0, 0),
            FontSize = isGlobal ? 15 : 12,
            ToolTip  = isGlobal ? "Add an app-specific variant for this gesture" : "Remove this variant",
        };
        Grid.SetColumn(actionBtn, 5);

        grid.Children.Add(actionCB);
        grid.Children.Add(outPanel);
        grid.Children.Add(enableToggle);
        grid.Children.Add(actionBtn);

        var row = new Row
        {
            Container     = grid,
            OutputPanel   = outPanel,
            Action        = action,
            OutputValue   = output,
            MouseGesture  = def.Gesture,
            Label         = def.Label,
            App           = app,
            AppCombo      = appCB,
            Enabled       = enabled,
            EnabledToggle = enableToggle,
        };
        SetRowOutput(row, action);
        if (!enabled) grid.Opacity = 0.45;

        enableToggle.Checked   += (_, __) => { row.Enabled = true;  row.Container.Opacity = 1.0; };
        enableToggle.Unchecked += (_, __) => { row.Enabled = false; row.Container.Opacity = 0.45; };

        actionCB.SelectionChanged += (_, __) =>
        {
            var idx = actionCB.SelectedIndex;
            if (idx >= 0 && idx < ActionOrder.Length) SetRowOutput(row, ActionOrder[idx]);
        };

        if (isGlobal)
        {
            actionBtn.Click += (_, __) => AddMouseVariantRow(def, container, "", null, true, isGlobal: false);
        }
        else
        {
            actionBtn.Click += (_, __) =>
            {
                container.Children.Remove(grid);
                _mouseRows[def.Gesture].Remove(row);
            };
        }

        container.Children.Add(grid);
        _mouseRows[def.Gesture].Add(row);
    }

    // =========================================================================
    // Keyboard trigger cards
    // =========================================================================
    void AddKbdBtn_Click(object sender, RoutedEventArgs e) => AddKbdTriggerCard("", null);

    void AddKbdTriggerCard(string trigger, List<(string output, string? app, bool enabled)>? variants)
    {
        var card = new KbdTriggerCard { Trigger = trigger };

        var cardBorder = new Border
        {
            Background      = Br("#181818"),
            BorderBrush     = Br("#2A2A2A"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Margin          = new Thickness(0, 0, 0, 6),
            Padding         = new Thickness(12, 10, 12, 10),
        };
        card.CardBorder = cardBorder;

        var cardContent = new StackPanel();

        // Header: trigger capture button + "+ App" + "×" delete card
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var capBtn = new Button
        {
            Style      = (Style)FindResource("BtnGhost"),
            Height     = 28,
            Padding    = new Thickness(10, 0, 10, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 12,
            ToolTip    = "Click to record trigger combo",
        };
        Grid.SetColumn(capBtn, 0);
        card.CaptureBtn = capBtn;

        var addAppBtn = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Content = "+ App",
            Height  = 26,
            Width   = 58,
            Padding = new Thickness(0),
            Margin  = new Thickness(6, 0, 0, 0),
            FontSize = 11,
            ToolTip = "Add an app-specific variant for this trigger",
        };
        Grid.SetColumn(addAppBtn, 1);

        var delCardBtn = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Content = "✕",
            Height  = 26,
            Width   = 26,
            Padding = new Thickness(0),
            Margin  = new Thickness(4, 0, 0, 0),
            FontSize = 11,
            ToolTip = "Remove this trigger",
        };
        Grid.SetColumn(delCardBtn, 2);

        header.Children.Add(capBtn);
        header.Children.Add(addAppBtn);
        header.Children.Add(delCardBtn);

        var variantStack = new StackPanel();
        card.VariantStack = variantStack;

        cardContent.Children.Add(header);
        cardContent.Children.Add(variantStack);
        cardBorder.Child = cardContent;

        // Wire up trigger capture
        var displayTrig = !string.IsNullOrEmpty(trigger) && trigger.StartsWith("key:", StringComparison.Ordinal)
            ? trigger.Substring(4) : "Click to record trigger...";
        capBtn.Content    = displayTrig;
        capBtn.Foreground = !string.IsNullOrEmpty(trigger) ? TextBrush : DimBrush;

        capBtn.Click += (_, __) => BeginCapture(
            btn: capBtn,
            onCommit: combo =>
            {
                try
                {
                    TriggerHelpers.CanonicalizeTrigger("key:" + combo);
                    card.Trigger = "key:" + combo;
                    RestoreTriggerButton(capBtn, card.Trigger);
                    UpdateCardAccentBorder(card);
                }
                catch (Exception ex)
                {
                    ShowFeedback(ex.Message, FeedbackKind.Err);
                    RestoreTriggerButton(capBtn, card.Trigger);
                }
            },
            onRestore: () => RestoreTriggerButton(capBtn, card.Trigger));

        addAppBtn.Click += (_, __) => AddKbdVariantRow(card, "", null, true);

        delCardBtn.Click += (_, __) =>
        {
            if (_captureActive && _captureBtn == capBtn) EndCapture();
            KbdStack.Children.Remove(cardBorder);
            _kbdCards.Remove(card);
        };

        KbdStack.Children.Add(cardBorder);
        _kbdCards.Add(card);

        // Populate variants; if none provided, add one blank global row.
        if (variants is { Count: > 0 })
            foreach (var (o, a, e) in variants) AddKbdVariantRow(card, o, a, e);
        else
            AddKbdVariantRow(card, "", null, true);

        UpdateCardAccentBorder(card);
    }

    Row AddKbdVariantRow(KbdTriggerCard card, string output, string? app, bool enabled)
    {
        var initAction = DetectAction(output);

        var grid = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        // col0=120: action combo  col1=*: output  col2=100: app combo  col3=Auto: enable  col4=Auto: delete
        // col4 is Auto (not fixed) so the button's left margin doesn't overflow the column width.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var actionCB = NewActionCombo(initAction);
        Grid.SetColumn(actionCB, 0);

        var outPanel = new Grid { Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(outPanel, 1);

        var appCB = new ComboBox
        {
            Style      = (Style)FindResource("DarkCB"),
            Height     = 28,
            Margin     = new Thickness(6, 0, 0, 0),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 11,
            ToolTip    = "App scope — 'All apps' fires everywhere; pick a specific app to override per-app",
        };
        PopulateAppCombo(appCB, app);
        UpdateAppComboStyle(appCB);
        appCB.SelectionChanged += (_, __) => UpdateAppComboStyle(appCB);
        appCB.DropDownOpened   += (_, __) => { var cur = GetAppComboValue(appCB); PopulateAppCombo(appCB, cur); };
        Grid.SetColumn(appCB, 2);

        var enableToggle = new CheckBox
        {
            Style     = (Style)FindResource("Toggle"),
            IsChecked = enabled,
            Margin    = new Thickness(6, 0, 0, 0),
            ToolTip   = "Enable this binding",
        };
        Grid.SetColumn(enableToggle, 3);

        var delBtn = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Content = "✕",
            Height  = 26,
            Width   = 26,
            Padding = new Thickness(0),
            Margin  = new Thickness(4, 0, 0, 0),
            FontSize = 11,
        };
        Grid.SetColumn(delBtn, 4);

        grid.Children.Add(actionCB);
        grid.Children.Add(outPanel);
        grid.Children.Add(appCB);
        grid.Children.Add(enableToggle);
        grid.Children.Add(delBtn);

        var row = new Row
        {
            Container     = grid,
            OutputPanel   = outPanel,
            Action        = initAction,
            OutputValue   = output,
            App           = app,
            AppCombo      = appCB,
            Enabled       = enabled,
            EnabledToggle = enableToggle,
        };
        SetRowOutput(row, initAction);
        if (!enabled) grid.Opacity = 0.45;

        enableToggle.Checked   += (_, __) => { row.Enabled = true;  row.Container.Opacity = 1.0; };
        enableToggle.Unchecked += (_, __) => { row.Enabled = false; row.Container.Opacity = 0.45; };

        actionCB.SelectionChanged += (_, __) =>
        {
            var idx = actionCB.SelectedIndex;
            if (idx >= 0 && idx < ActionOrder.Length) SetRowOutput(row, ActionOrder[idx]);
        };

        delBtn.Click += (_, __) =>
        {
            card.VariantStack.Children.Remove(grid);
            card.Variants.Remove(row);
            UpdateCardAccentBorder(card);
        };

        card.VariantStack.Children.Add(grid);
        card.Variants.Add(row);
        UpdateCardAccentBorder(card);
        return row;
    }

    void UpdateCardAccentBorder(KbdTriggerCard card)
    {
        bool hasAppScoped = card.Variants.Any(r => GetAppComboValue(r.AppCombo) != null);
        card.CardBorder.BorderBrush = hasAppScoped ? AmberBrush : Br("#2A2A2A");
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

        var entries = new List<BindingEntry>();

        // --- Mouse bindings ---
        foreach (var def in MouseDefs)
        {
            var rows = _mouseRows[def.Gesture];
            var mouseDedupSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var outp   = GetRowOutput(row);
                if (string.IsNullOrEmpty(outp)) continue;
                var appStr = GetAppComboValue(row.AppCombo) ?? "";

                if (row.Enabled && row.Action == ActionKind.Shortcut)
                {
                    try { TriggerHelpers.ValidateShortcutOutput(outp); }
                    catch (Exception ex) { ShowFeedback($"Mouse '{def.Label}': {ex.Message}", FeedbackKind.Err); return; }
                }

                var dedupKey = appStr.ToLowerInvariant();
                if (!mouseDedupSeen.Add(dedupKey))
                {
                    var scopeDesc = appStr.Length > 0 ? $"for app '{appStr}'" : "global";
                    ShowFeedback($"Mouse '{def.Label}': duplicate {scopeDesc} binding.", FeedbackKind.Err);
                    return;
                }

                var entry = new BindingEntry { trigger = "mouse:" + def.Gesture, output = outp, app = appStr.Length > 0 ? appStr : null };
                if (!row.Enabled) entry.enabled = false;
                entries.Add(entry);
            }
        }

        // --- Keyboard bindings ---
        var keyParsed  = new List<(string Trigger, ParsedKey Parsed, int CardIdx, string? App)>();
        var canonSeen  = new Dictionary<string, (int CardIdx, string? App)>();
        int cardIdx    = 0;
        foreach (var card in _kbdCards)
        {
            cardIdx++;
            if (card.Variants.Count == 0) continue;
            var trig = card.Trigger;
            bool anyContent = card.Variants.Any(r => !string.IsNullOrEmpty(GetRowOutput(r)));
            if (string.IsNullOrEmpty(trig) && !anyContent) continue;

            foreach (var row in card.Variants)
            {
                var outp   = GetRowOutput(row);
                var appStr = GetAppComboValue(row.AppCombo) ?? "";

                if (!row.Enabled)
                {
                    if (!string.IsNullOrEmpty(trig))
                        entries.Add(new BindingEntry { trigger = trig, output = outp, app = appStr.Length > 0 ? appStr : null, enabled = false });
                    continue;
                }

                if (string.IsNullOrEmpty(trig)) { ShowFeedback($"Keyboard trigger {cardIdx}: no trigger recorded.", FeedbackKind.Err); return; }
                if (string.IsNullOrEmpty(outp)) { ShowFeedback($"Keyboard trigger {cardIdx}: no output configured.", FeedbackKind.Err); return; }

                string canon;
                try { canon = TriggerHelpers.CanonicalizeTrigger(trig); }
                catch (Exception ex) { ShowFeedback($"Keyboard trigger {cardIdx}: {ex.Message}", FeedbackKind.Err); return; }

                if (row.Action == ActionKind.Shortcut)
                {
                    try { TriggerHelpers.ValidateShortcutOutput(outp); }
                    catch (Exception ex) { ShowFeedback($"Keyboard trigger {cardIdx}: {ex.Message}", FeedbackKind.Err); return; }
                }

                var dedupKey = canon + "|" + appStr.ToLowerInvariant();
                if (canonSeen.TryGetValue(dedupKey, out var prevInfo))
                {
                    var scopeDesc = appStr.Length > 0 ? $"for app '{appStr}'" : "global";
                    ShowFeedback($"Keyboard trigger {cardIdx}: duplicate {scopeDesc} binding (also at trigger {prevInfo.CardIdx}).", FeedbackKind.Err);
                    return;
                }
                canonSeen[dedupKey] = (cardIdx, appStr.Length > 0 ? appStr : null);

                var parsed = TriggerHelpers.ParseKeyTrigger(trig.Substring(4));
                keyParsed.Add((trig, parsed, cardIdx, appStr.Length > 0 ? appStr : null));
                entries.Add(new BindingEntry { trigger = trig, output = outp, app = appStr.Length > 0 ? appStr : null });
            }
        }

        if (entries.Count == 0) { ShowFeedback("Add at least one binding before saving.", FeedbackKind.Err); return; }

        // Prefix-pair warnings (only between bindings that share app scope)
        var prefixPairs = new List<string>();
        for (int i = 0; i < keyParsed.Count; i++)
            for (int j = 0; j < keyParsed.Count; j++)
            {
                if (i == j || !TriggerHelpers.IsKeyPrefixOf(keyParsed[i].Parsed, keyParsed[j].Parsed)) continue;
                var appI = keyParsed[i].App; var appJ = keyParsed[j].App;
                if (appI != null && appJ != null && !string.Equals(appI, appJ, StringComparison.OrdinalIgnoreCase)) continue;
                prefixPairs.Add($"{keyParsed[i].Trigger} → {keyParsed[j].Trigger}");
            }

        // Windows hotkey conflict probe
        var conflicts = new List<string>();
        foreach (var (trig, parsed, _, _) in keyParsed)
            if (HotkeyProbe.IsConflicted(parsed.Mods, parsed.Keys[0]))
                conflicts.Add(trig.Substring(4));

        var configToSave = new ConfigRoot { altHScroll = _altHScrollToggle?.IsChecked == true, bindings = entries };
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

        var savedPrefix = wasRunning ? "Saved, restarting." : "Saved.";
        var warnParts   = new List<string>();
        if (prefixPairs.Count > 0)
            warnParts.Add("Prefix pair(s): " + string.Join("; ", prefixPairs) + ". Shorter fires after ~80 ms.");
        if (conflicts.Count > 0)
            warnParts.Add("Hotkey conflict(s): " + string.Join(", ", conflicts) + ". Will still fire via low-level hook but may behave inconsistently.");

        if (warnParts.Count > 0)
            ShowFeedback(savedPrefix + " " + string.Join(" ", warnParts), FeedbackKind.Warn);
        else if (wasRunning) ShowFeedback("Saved — daemon restarting.", FeedbackKind.Ok);
        else                 ShowFeedback("Settings saved.", FeedbackKind.Ok);
    }
}
