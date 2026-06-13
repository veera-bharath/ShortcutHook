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

public enum ActionKind { Shortcut, OpenApp, OpenFile, OpenFolder, Command, ShiftHome, ShiftEnd, CtrlShiftLeft, CtrlShiftRight, HScrollLeft, HScrollRight }

// One action in a chained binding. Multiple ChainedActions make up one Row's output.
public sealed class ChainedAction
{
    public ActionKind Action             = ActionKind.Shortcut;
    public string     OutputValue        = "";
    public object?    OutputCtrl;
    public bool       ShortcutRecordMode = true;
    public CheckBox?  CmdShowCheckBox;
    public Grid       ItemContainer      = null!;  // the row grid for this chain item
    public ComboBox   ActionCombo        = null!;
    public Grid       OutputPanel        = null!;
    public Button?    DeleteBtn;
}

public sealed class Row
{
    public Grid       Container   = null!;
    public StackPanel ChainStack  = null!;   // vertical list: chain items + footer
    public List<ChainedAction> Chain = new();
    public int        OutputDelay = 0;       // ms between chained actions
    public TextBox?   DelayBox;
    public Grid       ChainFooter = null!;   // footer row: [+ Add action] [delay spinner]

    // mouse-only
    public string?    MouseGesture;
    public string     Label = "";

    // keyboard-only
    public Button?    CaptureBtn;
    public string     Trigger = "";

    // app scope
    public bool          IsGlobal  = true;
    public List<string>  Apps      = new();
    public Button?       AppScopeBtn;
    public Popup?        AppScopePopup;
    public CheckBox?     GlobalCheckBox;
    public List<(CheckBox Cb, string AppName)> AppCheckBoxes = new();

    // enabled state
    public bool      Enabled          = true;
    public CheckBox? EnabledToggle;

    // debounce (scroll gestures only): ignore repeats within 200 ms
    public bool      Debounce         = false;
    public CheckBox? DebounceToggle;

    // gesture-specific action options (null → StandardActions)
    public ActionDef[]? AvailableActions;
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
    static readonly ActionDef[] StandardActions = {
        new("Trigger shortcut", ActionKind.Shortcut),
        new("Open app",         ActionKind.OpenApp),
        new("Open file",        ActionKind.OpenFile),
        new("Open folder",      ActionKind.OpenFolder),
        new("Run command",      ActionKind.Command),
    };

    static ActionDef[] BuildActionsForGesture(MouseGestureDef def)
    {
        if (def.GestureDefault == null) return StandardActions;
        var list = new List<ActionDef> { def.GestureDefault };
        list.AddRange(StandardActions);
        return list.ToArray();
    }

    static readonly MouseGestureDef[] MouseDefs = {
        new("left+right",        "Left Hold + Right click x1"),
        new("left+rightx2",      "Left Hold + Right click x2"),
        new("left+rightx3",      "Left Hold + Right click x3"),
        new("single-wheel",      "Wheel click x1"),
        new("double-wheel",      "Wheel click x2"),
        new("triple-wheel",      "Wheel click x3"),
        new("double-right",      "Right click x2"),
        new("double-right-sel",  "Right click x2 (text selected)"),
        new("triple-right",      "Right click x3"),
        new("right-scroll-down", "Right Hold + Wheel Down"),
        new("right-scroll-up",   "Right Hold + Wheel Up"),
        new("shift-scroll-up",        "Shift + Wheel Up"),
        new("shift-scroll-down",      "Shift + Wheel Down"),
        new("ctrl-shift-scroll-up",   "Ctrl+Shift + Wheel Up"),
        new("ctrl-shift-scroll-down", "Ctrl+Shift + Wheel Down"),
        new("alt-scroll-up",   "Alt + Wheel Up",
            new("Scroll left (horizontal)",                 ActionKind.HScrollLeft)),
        new("alt-scroll-down", "Alt + Wheel Down",
            new("Scroll right (horizontal)",                ActionKind.HScrollRight)),
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

    static (bool isGlobal, List<string> apps) GetRowAppScope(Row row) =>
        (row.IsGlobal, row.Apps);

    void SetRowAppScope(Row row, bool isGlobal, List<string> apps)
    {
        row.IsGlobal = isGlobal;
        row.Apps     = apps;

        if (row.GlobalCheckBox != null)
            row.GlobalCheckBox.IsChecked = isGlobal;

        if (row.AppCheckBoxes != null)
        {
            var appSet = new HashSet<string>(apps, StringComparer.OrdinalIgnoreCase);
            foreach (var (cb, name) in row.AppCheckBoxes)
            {
                cb.IsChecked = appSet.Contains(name);
                cb.IsEnabled = !isGlobal;
                cb.Opacity   = isGlobal ? 0.4 : 1.0;
            }
        }

        UpdateAppScopeBtnLabel(row);
    }

    static void UpdateAppScopeBtnLabel(Row row)
    {
        if (row.AppScopeBtn == null) return;
        if (row.IsGlobal)
        {
            row.AppScopeBtn.Content    = "Global";
            row.AppScopeBtn.Foreground = LabelBrush;
            row.AppScopeBtn.BorderBrush = DarkBorder;
        }
        else if (row.Apps.Count == 0)
        {
            row.AppScopeBtn.Content    = "Select apps";
            row.AppScopeBtn.Foreground = RedBrush;
            row.AppScopeBtn.BorderBrush = RedBrush;
        }
        else if (row.Apps.Count == 1)
        {
            row.AppScopeBtn.Content    = row.Apps[0];
            row.AppScopeBtn.Foreground = AmberBrush;
            row.AppScopeBtn.BorderBrush = AmberBrush;
        }
        else
        {
            row.AppScopeBtn.Content    = $"{row.Apps.Count} apps";
            row.AppScopeBtn.Foreground = AmberBrush;
            row.AppScopeBtn.BorderBrush = AmberBrush;
        }
    }

    readonly List<AppEntry> _apps;
    readonly Dictionary<string, List<Row>> _mouseRows = new();
    readonly Dictionary<string, StackPanel> _mouseGestureStacks = new();
    readonly List<KbdTriggerCard> _kbdCards = new();
    string _appRoot;
    bool _setupComplete;

    // Capture state — plain C# fields, no scoping issues.
    bool            _captureActive;
    Button?         _captureBtn;
    Action<string>? _captureOnCommit;
    Action?         _captureOnRestore;
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

        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        SettingsVersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
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
                .Select(b =>
                {
                    bool isGlobal = b.apps == null || b.apps.Count == 0;
                    var  apps     = b.apps ?? new List<string>();
                    return (b.outputs ?? new List<string> { "" }, b.outputDelay, isGlobal, apps, b.enabled != false);
                })
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

    // =========================================================================
    // Settings overlay
    // =========================================================================
    void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsMenu();
        SettingsRoot.Visibility = Visibility.Visible;
    }

    void ShowSettingsMenu()
    {
        SettingsMenuView.Visibility     = Visibility.Visible;
        SettingsProfilesView.Visibility = Visibility.Collapsed;
        SettingsAboutView.Visibility    = Visibility.Collapsed;
    }

    void CloseSettings() => SettingsRoot.Visibility = Visibility.Collapsed;

    void SettingsRoot_MouseDown(object sender, MouseButtonEventArgs e) => CloseSettings();

    void SettingsCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void ManageProfilesOption_Click(object sender, MouseButtonEventArgs e)
    {
        SettingsMenuView.Visibility     = Visibility.Collapsed;
        SettingsProfilesView.Visibility = Visibility.Visible;
    }

    void AboutOption_Click(object sender, MouseButtonEventArgs e)
    {
        SettingsMenuView.Visibility  = Visibility.Collapsed;
        SettingsAboutView.Visibility = Visibility.Visible;
    }

    void SettingsBack_Click(object sender, RoutedEventArgs e) => ShowSettingsMenu();

    void SettingsGitHubBtn_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/veera-bharath/ShortcutHook")
        {
            UseShellExecute = true
        });

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

        var configRoot     = ConfigService.ReadConfig(InstallService.ScriptRoot);
        var activeBindings = ConfigService.GetActiveProfile(configRoot).bindings;

        var gestureGroups = new Dictionary<string, List<BindingEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in activeBindings)
        {
            if (!b.trigger.StartsWith("mouse:", StringComparison.Ordinal)) continue;
            var g = b.trigger.Substring(6);
            if (!gestureGroups.TryGetValue(g, out var list)) { list = new List<BindingEntry>(); gestureGroups[g] = list; }
            list.Add(b);
        }

        foreach (var def in MouseDefs)
        {
            gestureGroups.TryGetValue(def.Gesture, out var bindings);

            var gestureSP = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            _mouseGestureStacks[def.Gesture] = gestureSP;
            _mouseRows[def.Gesture] = new List<Row>();

            // Global row: entry with no apps (null or empty list)
            var globalEntry = bindings?.FirstOrDefault(b => b.apps == null || b.apps.Count == 0);
            AddMouseVariantRow(def, gestureSP,
                globalEntry?.outputs ?? new List<string> { "" },
                globalEntry?.outputDelay ?? 0,
                true, new List<string>(), globalEntry?.enabled != false, isGlobal: true,
                debounce: globalEntry?.debounce ?? false);

            // App-scoped variant rows
            if (bindings != null)
                foreach (var b in bindings.Where(b => b.apps != null && b.apps.Count > 0))
                    AddMouseVariantRow(def, gestureSP,
                        b.outputs ?? new List<string> { "" },
                        b.outputDelay, false, b.apps!, b.enabled != false, isGlobal: false,
                        debounce: b.debounce);

            MouseStack.Children.Add(gestureSP);
        }

    }

    void AddMouseVariantRow(MouseGestureDef def, StackPanel container, List<string> outputs, int outputDelay,
                            bool isGlobal_scope, List<string> apps, bool enabled, bool isGlobal, bool debounce = false)
    {
        // col0=175: gesture label (global) or indent arrow (variant)
        // col1=*:   chain block border (different shade per global/variant) containing the chain stack
        // col2=Auto: variant add (+) or delete (×) button — top-aligned
        // Global rows have a slightly lighter card; app-scoped variants use a brighter shade so the
        // grouping boundary is immediately visible without a hard separator line.
        var topMargin = isGlobal ? 0 : 6;
        var grid = new Grid { Margin = new Thickness(0, topMargin, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (isGlobal)
        {
            var lbl = new TextBlock { Text = def.Label, Foreground = Br("#CCCCCC"), FontSize = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) };
            Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);
        }
        else
        {
            var arrow = new TextBlock { Text = "↳", Foreground = Br("#555555"), FontSize = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 8, 0, 0) };
            Grid.SetColumn(arrow, 0);
            grid.Children.Add(arrow);
        }

        // Each variant gets its own rounded card so chain items are visually grouped.
        var chainBorder = new Border
        {
            Background      = Br(isGlobal ? "#1A1A1A" : "#222222"),
            BorderBrush     = Br(isGlobal ? "#2A2A2A" : "#333333"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(7),
            Padding         = new Thickness(10, 8, 10, 8),
            Margin          = new Thickness(6, 0, 4, 0),
        };
        var chainStack = new StackPanel();
        chainBorder.Child = chainStack;
        Grid.SetColumn(chainBorder, 1);
        grid.Children.Add(chainBorder);

        var variantBtn = new Button
        {
            Style    = (Style)FindResource("BtnGhost"),
            Content  = isGlobal ? "+" : "✕",
            Height   = 26,
            Width    = 26,
            Padding  = new Thickness(0),
            Margin   = new Thickness(2, 6, 0, 0),
            FontSize = isGlobal ? 15 : 12,
            ToolTip  = isGlobal ? "Add an app-specific variant for this gesture" : "Remove this variant",
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(variantBtn, 2);
        grid.Children.Add(variantBtn);

        var row = new Row
        {
            Container        = grid,
            ChainStack       = chainStack,
            OutputDelay      = outputDelay,
            MouseGesture     = def.Gesture,
            Label            = def.Label,
            AvailableActions = BuildActionsForGesture(def),
            Debounce         = debounce,
        };

        BuildChainStack(row, outputs, isGlobal_scope, apps, enabled);
        if (!enabled) grid.Opacity = 0.45;

        if (isGlobal)
            variantBtn.Click += (_, __) => AddMouseVariantRow(def, container, new List<string> { "" }, 0, false, new List<string>(), true, isGlobal: false);
        else
            variantBtn.Click += (_, __) =>
            {
                container.Children.Remove(grid);
                _mouseRows[def.Gesture].Remove(row);
            };

        container.Children.Add(grid);
        _mouseRows[def.Gesture].Add(row);
    }

    // =========================================================================
    // Chain stack builder
    // =========================================================================

    // Builds chain items + control row into row.ChainStack.
    void BuildChainStack(Row row, List<string> outputs, bool isGlobal, List<string> apps, bool enabled)
    {
        row.ChainStack.Children.Clear();
        row.Chain.Clear();

        var effectiveOutputs = outputs.Count > 0 ? outputs : new List<string> { "" };
        foreach (var o in effectiveOutputs)
            AddChainItem(row, o, rebuild: false);

        var controlRow = BuildChainControlRow(row, isGlobal, apps, enabled);
        row.ChainFooter = controlRow;
        row.ChainStack.Children.Add(controlRow);

        RefreshChainDeleteButtons(row);
        RefreshChainFooter(row);
    }

    // Adds one action item to the chain (appends before the control row if already built).
    ChainedAction AddChainItem(Row row, string output, bool rebuild = true)
    {
        var rowActions = row.AvailableActions ?? StandardActions;
        var action = DetectAction(output, rowActions);

        // Item grid: [ActionCombo 120][OutputPanel *][chain-× Auto]
        var itemGrid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var actionCB = NewActionCombo(rowActions, action);
        Grid.SetColumn(actionCB, 0);

        var outPanel = new Grid { Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(outPanel, 1);

        var delBtn = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Content = "✕",
            Height  = 26,
            Width   = 22,
            Padding = new Thickness(0),
            Margin  = new Thickness(3, 0, 0, 0),
            FontSize = 10,
            ToolTip  = "Remove this action from the chain",
        };
        Grid.SetColumn(delBtn, 2);

        itemGrid.Children.Add(actionCB);
        itemGrid.Children.Add(outPanel);
        itemGrid.Children.Add(delBtn);

        var item = new ChainedAction
        {
            Action        = action,
            OutputValue   = output,
            ItemContainer = itemGrid,
            ActionCombo   = actionCB,
            OutputPanel   = outPanel,
            DeleteBtn     = delBtn,
        };
        SetChainItemOutput(row, item, action);

        var capturedActions = rowActions;
        actionCB.SelectionChanged += (_, __) =>
        {
            var idx = actionCB.SelectedIndex;
            if (idx >= 0 && idx < capturedActions.Length) SetChainItemOutput(row, item, capturedActions[idx].Kind);
        };

        delBtn.Click += (_, __) =>
        {
            if (row.Chain.Count <= 1) return;
            row.Chain.Remove(item);
            row.ChainStack.Children.Remove(itemGrid);
            RefreshChainDeleteButtons(row);
            RefreshChainFooter(row);
        };

        row.Chain.Add(item);

        if (rebuild && row.ChainFooter != null)
        {
            int footerIdx = row.ChainStack.Children.IndexOf(row.ChainFooter);
            if (footerIdx >= 0)
                row.ChainStack.Children.Insert(footerIdx, itemGrid);
            else
                row.ChainStack.Children.Add(itemGrid);
            RefreshChainDeleteButtons(row);
            RefreshChainFooter(row);
        }
        else
        {
            row.ChainStack.Children.Add(itemGrid);
        }

        return item;
    }

    // Builds the bottom control row for a variant:
    // [+ chain][delay-lbl][delay-tb][ms][spacer *][AppScopeBtn][Enable]
    Grid BuildChainControlRow(Row row, bool isGlobal, List<string> apps, bool enabled)
    {
        var footer = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var addBtn = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Content = "+ chain",
            Height  = 24,
            Padding = new Thickness(6, 0, 6, 0),
            FontSize = 10,
            ToolTip  = "Add another action to this chain",
        };
        Grid.SetColumn(addBtn, 0);
        addBtn.Click += (_, __) => AddChainItem(row, "", rebuild: true);

        var delayLbl = new TextBlock
        {
            Text = "delay:",
            Foreground = DimBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 3, 0),
        };
        Grid.SetColumn(delayLbl, 1);

        var delayTB = new TextBox
        {
            Style      = (Style)FindResource("DarkTB"),
            Height     = 24,
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 10,
            Text       = row.OutputDelay > 0 ? row.OutputDelay.ToString() : "0",
            ToolTip    = "Delay between chained actions (ms)",
        };
        delayTB.TextChanged += (_, __) =>
        {
            if (int.TryParse(delayTB.Text, out var ms) && ms >= 0)
                row.OutputDelay = ms;
        };
        Grid.SetColumn(delayTB, 2);

        var msLbl = new TextBlock
        {
            Text = "ms",
            Foreground = DimBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0),
        };
        Grid.SetColumn(msLbl, 3);

        var scopeBtn = BuildAppScopeButton(row, isGlobal, apps);
        Grid.SetColumn(scopeBtn, 5);

        var enableToggle = new CheckBox
        {
            Style     = (Style)FindResource("Toggle"),
            IsChecked = enabled,
            Margin    = new Thickness(6, 0, 0, 0),
            ToolTip   = "Enable this binding",
        };
        Grid.SetColumn(enableToggle, 6);

        row.EnabledToggle = enableToggle;
        row.Enabled       = enabled;
        row.DelayBox      = delayTB;

        enableToggle.Checked   += (_, __) => { row.Enabled = true;  row.Container.Opacity = 1.0; };
        enableToggle.Unchecked += (_, __) => { row.Enabled = false; row.Container.Opacity = 0.45; };

        footer.Children.Add(addBtn);
        footer.Children.Add(delayLbl);
        footer.Children.Add(delayTB);
        footer.Children.Add(msLbl);
        footer.Children.Add(scopeBtn);
        footer.Children.Add(enableToggle);

        if (row.MouseGesture?.Contains("scroll") == true)
        {
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var debounceLbl = new TextBlock
            {
                Text = "debounce",
                Foreground = DimBrush,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 3, 0),
                ToolTip = "Ignore repeated scroll firings within 200 ms",
            };
            Grid.SetColumn(debounceLbl, 7);

            var debounceToggle = new CheckBox
            {
                Style     = (Style)FindResource("Toggle"),
                IsChecked = row.Debounce,
                Margin    = new Thickness(0, 0, 0, 0),
                ToolTip   = "Ignore repeated scroll firings within 200 ms",
            };
            Grid.SetColumn(debounceToggle, 8);

            row.DebounceToggle = debounceToggle;
            debounceToggle.Checked   += (_, __) => row.Debounce = true;
            debounceToggle.Unchecked += (_, __) => row.Debounce = false;

            footer.Children.Add(debounceLbl);
            footer.Children.Add(debounceToggle);
        }

        return footer;
    }

    // Builds the multi-select app scope button + popup and wires it to row state.
    Button BuildAppScopeButton(Row row, bool isGlobal, List<string> initialApps)
    {
        row.IsGlobal      = isGlobal;
        row.Apps          = new List<string>(initialApps);
        row.AppCheckBoxes = new List<(CheckBox, string)>();

        var scopeBtn = new Button
        {
            Style      = (Style)FindResource("BtnGhost"),
            Height     = 26,
            Margin     = new Thickness(6, 0, 0, 0),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 11,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding    = new Thickness(6, 0, 6, 0),
            ToolTip    = "App scope — Global fires everywhere; select specific apps to scope this binding",
        };
        row.AppScopeBtn = scopeBtn;

        // Popup panel
        var popupPanel = new Border
        {
            Background      = Br("#222222"),
            BorderBrush     = Br("#444444"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(5),
            Padding         = new Thickness(4),
            MaxHeight       = 260,
        };
        var outerSV = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var popupStack = new StackPanel();
        outerSV.Content  = popupStack;
        popupPanel.Child = outerSV;

        var popup = new Popup
        {
            Child               = popupPanel,
            StaysOpen           = false,
            Placement           = PlacementMode.Bottom,
            PlacementTarget     = scopeBtn,
            AllowsTransparency  = true,
            MinWidth            = 160,
            MaxWidth            = 260,
        };
        row.AppScopePopup = popup;

        void RebuildPopupItems()
        {
            popupStack.Children.Clear();
            row.AppCheckBoxes.Clear();

            // Global checkbox
            var globalCb = new CheckBox
            {
                Content    = "Global",
                Foreground = Br("#E8E8E8"),
                IsChecked  = row.IsGlobal,
                Margin     = new Thickness(4, 4, 4, 4),
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 11,
            };
            row.GlobalCheckBox = globalCb;
            popupStack.Children.Add(globalCb);

            var sep = new Rectangle { Height = 1, Fill = Br("#444444"), Margin = new Thickness(0, 2, 0, 2) };
            popupStack.Children.Add(sep);

            // App entries: running processes (same source as old single-select combo).
            // Process.GetProcesses() returns process names without extension; add .exe to match
            // what GetForegroundProcessName() returns in the daemon (Path.GetFileName of full exe path).
            var selectedSet = new HashSet<string>(row.Apps, StringComparer.OrdinalIgnoreCase);
            var runningNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try { if (!string.IsNullOrEmpty(p.ProcessName)) runningNames.Add(p.ProcessName + ".exe"); }
                    catch { }
                }
            }
            // Include any stored names that aren't currently running so they remain selectable.
            foreach (var stored in row.Apps)
                runningNames.Add(stored);

            foreach (var procName in runningNames)
            {
                var name = procName; // capture for closure
                var cb = new CheckBox
                {
                    Content    = name,
                    Foreground = Br("#CCCCCC"),
                    IsChecked  = selectedSet.Contains(name),
                    IsEnabled  = !row.IsGlobal,
                    Opacity    = row.IsGlobal ? 0.4 : 1.0,
                    Margin     = new Thickness(4, 2, 4, 2),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 11,
                };
                cb.Checked   += (_, __) => { if (!row.Apps.Contains(name, StringComparer.OrdinalIgnoreCase)) row.Apps.Add(name); UpdateAppScopeBtnLabel(row); };
                cb.Unchecked += (_, __) => { row.Apps.RemoveAll(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)); UpdateAppScopeBtnLabel(row); };
                popupStack.Children.Add(cb);
                row.AppCheckBoxes.Add((cb, name));
            }

            globalCb.Checked += (_, __) =>
            {
                row.IsGlobal = true;
                foreach (var (cb, _) in row.AppCheckBoxes) { cb.IsEnabled = false; cb.Opacity = 0.4; }
                UpdateAppScopeBtnLabel(row);
            };
            globalCb.Unchecked += (_, __) =>
            {
                row.IsGlobal = false;
                foreach (var (cb, _) in row.AppCheckBoxes) { cb.IsEnabled = true; cb.Opacity = 1.0; }
                UpdateAppScopeBtnLabel(row);
            };
        }

        scopeBtn.Click += (_, __) =>
        {
            RebuildPopupItems();
            popup.IsOpen = !popup.IsOpen;
        };

        UpdateAppScopeBtnLabel(row);
        return scopeBtn;
    }

    void RefreshChainDeleteButtons(Row row)
    {
        bool moreThanOne = row.Chain.Count > 1;
        foreach (var item in row.Chain)
            if (item.DeleteBtn != null)
                item.DeleteBtn.Visibility = moreThanOne ? Visibility.Visible : Visibility.Collapsed;
    }

    void RefreshChainFooter(Row row)
    {
        if (row.ChainFooter == null) return;
        bool multi = row.Chain.Count > 1;
        // cols 1-3 are the delay controls — hidden when single action
        foreach (UIElement child in row.ChainFooter.Children)
        {
            int col = Grid.GetColumn(child);
            if (col >= 1 && col <= 3)
                child.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // =========================================================================
    // Keyboard trigger cards
    // =========================================================================
    void AddKbdBtn_Click(object sender, RoutedEventArgs e) => AddKbdTriggerCard("", null);

    void AddKbdTriggerCard(string trigger, List<(List<string> outputs, int outputDelay, bool isGlobal, List<string> apps, bool enabled)>? variants)
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

        addAppBtn.Click += (_, __) => AddKbdVariantRow(card, new List<string> { "" }, 0, false, new List<string>(), true);

        delCardBtn.Click += (_, __) =>
        {
            if (_captureActive && _captureBtn == capBtn) EndCapture();
            KbdStack.Children.Remove(cardBorder);
            _kbdCards.Remove(card);
        };

        KbdStack.Children.Add(cardBorder);
        _kbdCards.Add(card);

        if (variants is { Count: > 0 })
            foreach (var (o, d, isG, apps, e) in variants) AddKbdVariantRow(card, o, d, isG, apps, e);
        else
            AddKbdVariantRow(card, new List<string> { "" }, 0, true, new List<string>(), true);

        UpdateCardAccentBorder(card);
    }

    Row AddKbdVariantRow(KbdTriggerCard card, List<string> outputs, int outputDelay, bool scopeIsGlobal, List<string> scopeApps, bool enabled)
    {
        // col0=*: chain block border containing chain items + control row
        // col1=Auto: delete-variant button — top-aligned
        // First variant uses a slightly darker shade; extra app-scoped variants use a lighter shade
        // so each grouping is visually distinct within the keyboard trigger card.
        bool isFirstKbd = card.Variants.Count == 0;
        var topGap = isFirstKbd ? 0 : 6;
        var grid = new Grid { Margin = new Thickness(0, topGap, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var chainBorder = new Border
        {
            Background      = Br(isFirstKbd ? "#1E1E1E" : "#262626"),
            BorderBrush     = Br(isFirstKbd ? "#2D2D2D" : "#363636"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(10, 8, 10, 8),
        };
        var chainStack = new StackPanel();
        chainBorder.Child = chainStack;
        Grid.SetColumn(chainBorder, 0);

        var delBtn = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Content = "✕",
            Height  = 26,
            Width   = 26,
            Padding = new Thickness(0),
            Margin  = new Thickness(4, 6, 0, 0),
            FontSize = 11,
            ToolTip  = "Remove this variant",
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(delBtn, 1);

        grid.Children.Add(chainBorder);
        grid.Children.Add(delBtn);

        var row = new Row
        {
            Container   = grid,
            ChainStack  = chainStack,
            OutputDelay = outputDelay,
        };
        BuildChainStack(row, outputs, scopeIsGlobal, scopeApps, enabled);
        if (!enabled) grid.Opacity = 0.45;

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
        bool hasAppScoped = card.Variants.Any(r => !r.IsGlobal && r.Apps.Count > 0);
        card.CardBorder.BorderBrush = hasAppScoped ? AmberBrush : Br("#2A2A2A");
    }

    // =========================================================================
    // Key capture
    // =========================================================================
    void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SettingsRoot.Visibility == Visibility.Visible)
        {
            var settingsKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (settingsKey == Key.Escape) { CloseSettings(); e.Handled = true; }
            return;
        }

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
    // Action selection + output panel swap (per ChainedAction)
    // =========================================================================
    ComboBox NewActionCombo(ActionDef[] actions, ActionKind initial)
    {
        var cb = new ComboBox
        {
            Style  = (Style)FindResource("DarkCB"),
            Height = 28,
        };
        foreach (var def in actions) cb.Items.Add(def.Label);
        cb.SelectedIndex = Array.FindIndex(actions, d => d.Kind == initial);
        if (cb.SelectedIndex < 0) cb.SelectedIndex = 0;
        return cb;
    }

    ActionKind DetectAction(string output, ActionDef[]? available = null)
    {
        bool Has(ActionKind k) => available == null || Array.FindIndex(available, d => d.Kind == k) >= 0;

        if (output.StartsWith("cmd:", StringComparison.Ordinal) ||
            output.StartsWith("cmdw:", StringComparison.Ordinal)) return ActionKind.Command;
        if (output == "hscroll:left"   && Has(ActionKind.HScrollLeft))    return ActionKind.HScrollLeft;
        if (output == "hscroll:right"  && Has(ActionKind.HScrollRight))   return ActionKind.HScrollRight;
        if (output == "Shift+Home"         && Has(ActionKind.ShiftHome))      return ActionKind.ShiftHome;
        if (output == "Shift+End"          && Has(ActionKind.ShiftEnd))       return ActionKind.ShiftEnd;
        if (output == "Ctrl+Shift+Left"    && Has(ActionKind.CtrlShiftLeft))  return ActionKind.CtrlShiftLeft;
        if (output == "Ctrl+Shift+Right"   && Has(ActionKind.CtrlShiftRight)) return ActionKind.CtrlShiftRight;
        if (string.IsNullOrEmpty(output) || !output.StartsWith("open:", StringComparison.Ordinal))
            return ActionKind.Shortcut;
        var p = output.Substring(5);
        try { if (Directory.Exists(p)) return ActionKind.OpenFolder; } catch { }
        if (_apps.Any(a => string.Equals(a.Path, p, StringComparison.OrdinalIgnoreCase))) return ActionKind.OpenApp;
        return ActionKind.OpenFile;
    }

    void SetChainItemOutput(Row row, ChainedAction item, ActionKind action)
    {
        // Snapshot current text into OutputValue before tearing down controls.
        if (item.Action == ActionKind.Shortcut && item.OutputCtrl is TextBox prevTB)
            item.OutputValue = prevTB.Text.Trim();
        if (item.Action == ActionKind.Command && item.OutputCtrl is TextBox prevCmd && !string.IsNullOrWhiteSpace(prevCmd.Text))
            item.OutputValue = (item.CmdShowCheckBox?.IsChecked == true ? "cmdw:" : "cmd:") + prevCmd.Text.Trim();

        if (_captureActive && ReferenceEquals(_captureBtn, item.OutputCtrl)) EndCapture();

        item.OutputPanel.Children.Clear();
        item.Action          = action;
        item.OutputCtrl      = null;
        item.CmdShowCheckBox = null;

        switch (action)
        {
            case ActionKind.Shortcut:
                BuildShortcutOutput(row, item);
                break;
            case ActionKind.OpenApp:
            {
                var cb = new ComboBox
                {
                    Style       = (Style)FindResource("DarkCB"),
                    Height      = 28,
                    ItemsSource = _apps,
                };
                var curPath = item.OutputValue.StartsWith("open:", StringComparison.Ordinal)
                    ? item.OutputValue.Substring(5) : "";
                if (!string.IsNullOrEmpty(curPath))
                {
                    var match = _apps.FirstOrDefault(a => string.Equals(a.Path, curPath, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) cb.SelectedItem = match;
                }
                item.OutputPanel.Children.Add(cb);
                item.OutputCtrl = cb;
                break;
            }
            case ActionKind.OpenFile:
                AddBrowsePanel(item, isFolder: false);
                break;
            case ActionKind.OpenFolder:
                AddBrowsePanel(item, isFolder: true);
                break;
            case ActionKind.ShiftHome:
            case ActionKind.ShiftEnd:
            case ActionKind.CtrlShiftLeft:
            case ActionKind.CtrlShiftRight:
            case ActionKind.HScrollLeft:
            case ActionKind.HScrollRight:
            {
                var desc = action switch {
                    ActionKind.ShiftHome      => "Shift+Home — select to line start",
                    ActionKind.ShiftEnd       => "Shift+End — select to line end",
                    ActionKind.CtrlShiftLeft  => "Ctrl+Shift+Left — select word left",
                    ActionKind.CtrlShiftRight => "Ctrl+Shift+Right — select word right",
                    ActionKind.HScrollLeft    => "Horizontal scroll left",
                    ActionKind.HScrollRight   => "Horizontal scroll right",
                    _                         => ""
                };
                item.OutputPanel.Children.Add(new TextBlock
                {
                    Text              = desc,
                    Foreground        = DimBrush,
                    FontFamily        = new FontFamily("Consolas"),
                    FontSize          = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                item.OutputCtrl = null;
                break;
            }
            case ActionKind.Command:
            {
                bool isShow = item.OutputValue.StartsWith("cmdw:", StringComparison.Ordinal);
                string cmdText = isShow ? item.OutputValue.Substring(5)
                               : item.OutputValue.StartsWith("cmd:", StringComparison.Ordinal)
                                     ? item.OutputValue.Substring(4) : "";

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
                item.OutputPanel.Children.Add(g);
                item.OutputCtrl      = tb;
                item.CmdShowCheckBox = showCb;
                break;
            }
        }
    }

    void BuildShortcutOutput(Row row, ChainedAction item)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        FrameworkElement main;
        if (item.ShortcutRecordMode)
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
            RestoreOutputRecordButton(recBtn, item.OutputValue);

            recBtn.Click += (_, __) => BeginCapture(
                btn: recBtn,
                onCommit: combo =>
                {
                    item.OutputValue = combo;
                    RestoreOutputRecordButton(recBtn, item.OutputValue);
                },
                onRestore: () => RestoreOutputRecordButton(recBtn, item.OutputValue));

            main = recBtn;
            item.OutputCtrl = recBtn;
        }
        else
        {
            var tb = new TextBox { Style = (Style)FindResource("DarkTB"), Height = 28 };
            tb.Text = !string.IsNullOrEmpty(item.OutputValue) &&
                      !item.OutputValue.StartsWith("open:", StringComparison.Ordinal)
                ? item.OutputValue : "";
            main = tb;
            item.OutputCtrl = tb;
        }
        Grid.SetColumn(main, 0);

        var toggle = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Height  = 28,
            Margin  = new Thickness(6,0,0,0),
            Padding = new Thickness(0),
            FontSize = 13,
            Content  = item.ShortcutRecordMode ? "Aa" : "⌨",
            ToolTip  = item.ShortcutRecordMode ? "Switch to typing" : "Switch to recording",
        };
        Grid.SetColumn(toggle, 1);

        toggle.Click += (_, __) =>
        {
            item.ShortcutRecordMode = !item.ShortcutRecordMode;
            SetChainItemOutput(row, item, ActionKind.Shortcut);
        };

        g.Children.Add(main);
        g.Children.Add(toggle);
        item.OutputPanel.Children.Add(g);
    }

    void RestoreOutputRecordButton(Button btn, string value)
    {
        var hasValue = !string.IsNullOrEmpty(value) && !value.StartsWith("open:", StringComparison.Ordinal);
        btn.Content     = hasValue ? value : "Click to record...";
        btn.Foreground  = hasValue ? TextBrush : DimBrush;
        btn.Background  = Transparent;
        btn.BorderBrush = DarkBorder;
    }

    void AddBrowsePanel(ChainedAction item, bool isFolder)
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
        var curPath = item.OutputValue.StartsWith("open:", StringComparison.Ordinal)
            ? item.OutputValue.Substring(5) : "";
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
                    item.OutputValue  = "open:" + ofd.FileName;
                    pathTB.Text      = ofd.FileName;
                    pathTB.Foreground = Br("#CCCCCC");
                }
            }
            else
            {
                using var fbd = new System.Windows.Forms.FolderBrowserDialog { Description = "Select a folder" };
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    item.OutputValue  = "open:" + fbd.SelectedPath;
                    pathTB.Text      = fbd.SelectedPath;
                    pathTB.Foreground = Br("#CCCCCC");
                }
            }
        };

        g.Children.Add(pathTB);
        g.Children.Add(browseBtn);
        item.OutputPanel.Children.Add(g);
        item.OutputCtrl = pathTB;
    }

    string GetChainItemOutput(ChainedAction item) => item.Action switch
    {
        ActionKind.Shortcut        => item.ShortcutRecordMode
                                         ? item.OutputValue
                                         : (item.OutputCtrl as TextBox)?.Text.Trim() ?? "",
        ActionKind.OpenApp         => (item.OutputCtrl is ComboBox cb && cb.SelectedItem is AppEntry a) ? "open:" + a.Path : "",
        ActionKind.OpenFile        => item.OutputValue,
        ActionKind.OpenFolder      => item.OutputValue,
        ActionKind.Command         => (item.OutputCtrl is TextBox cmdTb && !string.IsNullOrWhiteSpace(cmdTb.Text))
                                         ? (item.CmdShowCheckBox?.IsChecked == true ? "cmdw:" : "cmd:") + cmdTb.Text.Trim() : "",
        ActionKind.ShiftHome       => "Shift+Home",
        ActionKind.ShiftEnd        => "Shift+End",
        ActionKind.CtrlShiftLeft   => "Ctrl+Shift+Left",
        ActionKind.CtrlShiftRight  => "Ctrl+Shift+Right",
        ActionKind.HScrollLeft     => "hscroll:left",
        ActionKind.HScrollRight    => "hscroll:right",
        _                          => "",
    };

    // Returns non-empty outputs only.
    List<string> GetRowOutputs(Row row) =>
        row.Chain.Select(GetChainItemOutput).Where(s => !string.IsNullOrEmpty(s)).ToList();

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
            bool mouseGlobalSeen = false;
            var  mouseAppSeen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var outputsList = GetRowOutputs(row);
                if (outputsList.Count == 0) continue;

                // Validate: non-global must have at least one app
                if (!row.IsGlobal && row.Apps.Count == 0)
                {
                    ShowFeedback($"Mouse '{def.Label}': app-scoped variant has no apps selected.", FeedbackKind.Err);
                    return;
                }

                if (row.Enabled)
                {
                    foreach (var outp in outputsList)
                    {
                        if (outp.StartsWith("open:", StringComparison.Ordinal) ||
                            outp.StartsWith("cmd:", StringComparison.Ordinal)  ||
                            outp.StartsWith("cmdw:", StringComparison.Ordinal) ||
                            outp.StartsWith("hscroll:", StringComparison.Ordinal)) continue;
                        try { TriggerHelpers.ValidateShortcutOutput(outp); }
                        catch (Exception ex) { ShowFeedback($"Mouse '{def.Label}': {ex.Message}", FeedbackKind.Err); return; }
                    }
                }

                if (row.IsGlobal)
                {
                    if (mouseGlobalSeen) { ShowFeedback($"Mouse '{def.Label}': duplicate global binding.", FeedbackKind.Err); return; }
                    mouseGlobalSeen = true;
                }
                else
                {
                    foreach (var a in row.Apps)
                    {
                        if (!mouseAppSeen.Add(a)) { ShowFeedback($"Mouse '{def.Label}': app '{a}' appears in multiple variants.", FeedbackKind.Err); return; }
                    }
                }

                var entry = new BindingEntry
                {
                    trigger     = "mouse:" + def.Gesture,
                    outputs     = outputsList,
                    outputDelay = row.OutputDelay,
                    apps        = row.IsGlobal ? null : new List<string>(row.Apps),
                };
                if (!row.Enabled) entry.enabled = false;
                if (row.Debounce)  entry.debounce = true;
                entries.Add(entry);
            }
        }

        // --- Keyboard bindings ---
        var keyParsed  = new List<(string Trigger, ParsedKey Parsed, int CardIdx, bool IsGlobal, List<string> Apps)>();
        // canon+"|global" or canon+"|app:Name" → card index
        var canonAppSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int cardIdx    = 0;
        foreach (var card in _kbdCards)
        {
            cardIdx++;
            if (card.Variants.Count == 0) continue;
            var trig = card.Trigger;
            bool anyContent = card.Variants.Any(r => GetRowOutputs(r).Count > 0);
            if (string.IsNullOrEmpty(trig) && !anyContent) continue;

            foreach (var row in card.Variants)
            {
                var outputsList = GetRowOutputs(row);

                // Validate: non-global must have at least one app
                if (!row.IsGlobal && row.Apps.Count == 0 && row.Enabled)
                {
                    ShowFeedback($"Keyboard trigger {cardIdx}: app-scoped variant has no apps selected.", FeedbackKind.Err);
                    return;
                }

                if (!row.Enabled)
                {
                    if (!string.IsNullOrEmpty(trig))
                        entries.Add(new BindingEntry
                        {
                            trigger     = trig,
                            outputs     = outputsList.Count > 0 ? outputsList : new List<string> { "" },
                            outputDelay = row.OutputDelay,
                            apps        = row.IsGlobal ? null : new List<string>(row.Apps),
                            enabled     = false,
                        });
                    continue;
                }

                if (string.IsNullOrEmpty(trig)) { ShowFeedback($"Keyboard trigger {cardIdx}: no trigger recorded.", FeedbackKind.Err); return; }
                if (outputsList.Count == 0) { ShowFeedback($"Keyboard trigger {cardIdx}: no output configured.", FeedbackKind.Err); return; }

                string canon;
                try { canon = TriggerHelpers.CanonicalizeTrigger(trig); }
                catch (Exception ex) { ShowFeedback($"Keyboard trigger {cardIdx}: {ex.Message}", FeedbackKind.Err); return; }

                foreach (var outp in outputsList)
                {
                    if (outp.StartsWith("open:", StringComparison.Ordinal) ||
                        outp.StartsWith("cmd:", StringComparison.Ordinal)  ||
                        outp.StartsWith("cmdw:", StringComparison.Ordinal)) continue;
                    try { TriggerHelpers.ValidateShortcutOutput(outp); }
                    catch (Exception ex) { ShowFeedback($"Keyboard trigger {cardIdx}: {ex.Message}", FeedbackKind.Err); return; }
                }

                // Dedup check
                if (row.IsGlobal)
                {
                    var dedupKey = canon + "|global";
                    if (canonAppSeen.TryGetValue(dedupKey, out var prevCard))
                    {
                        ShowFeedback($"Keyboard trigger {cardIdx}: duplicate global binding (also at trigger {prevCard}).", FeedbackKind.Err);
                        return;
                    }
                    canonAppSeen[dedupKey] = cardIdx;
                }
                else
                {
                    foreach (var a in row.Apps)
                    {
                        var dedupKey = canon + "|app:" + a;
                        if (canonAppSeen.TryGetValue(dedupKey, out var prevCard))
                        {
                            ShowFeedback($"Keyboard trigger {cardIdx}: app '{a}' already used in trigger {prevCard}.", FeedbackKind.Err);
                            return;
                        }
                        canonAppSeen[dedupKey] = cardIdx;
                    }
                }

                var parsed = TriggerHelpers.ParseKeyTrigger(trig.Substring(4));
                keyParsed.Add((trig, parsed, cardIdx, row.IsGlobal, new List<string>(row.Apps)));
                entries.Add(new BindingEntry
                {
                    trigger     = trig,
                    outputs     = outputsList,
                    outputDelay = row.OutputDelay,
                    apps        = row.IsGlobal ? null : new List<string>(row.Apps),
                });
            }
        }

        if (entries.Count == 0) { ShowFeedback("Add at least one binding before saving.", FeedbackKind.Err); return; }

        // Prefix-pair warnings: warn when two triggers with overlapping scope form a prefix pair
        var prefixPairs = new List<string>();
        for (int i = 0; i < keyParsed.Count; i++)
            for (int j = 0; j < keyParsed.Count; j++)
            {
                if (i == j || !TriggerHelpers.IsKeyPrefixOf(keyParsed[i].Parsed, keyParsed[j].Parsed)) continue;
                // Skip if both scoped but share no app
                bool iGlobal = keyParsed[i].IsGlobal, jGlobal = keyParsed[j].IsGlobal;
                if (!iGlobal && !jGlobal && !keyParsed[i].Apps.Intersect(keyParsed[j].Apps, StringComparer.OrdinalIgnoreCase).Any()) continue;
                prefixPairs.Add($"{keyParsed[i].Trigger} → {keyParsed[j].Trigger}");
            }

        // Windows hotkey conflict probe
        var conflicts = new List<string>();
        foreach (var (trig, parsed, _, _, _) in keyParsed)
            if (HotkeyProbe.IsConflicted(parsed.Mods, parsed.Keys[0]))
                conflicts.Add(trig.Substring(4));

        try { ConfigService.SaveActiveProfileBindings(InstallService.ScriptRoot, entries); }
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
