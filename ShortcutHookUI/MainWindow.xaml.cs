using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ShortcutHookUI;

public enum ActionKind { Shortcut, OpenApp, OpenFile, OpenFolder, Command, TypeText, ShiftHome, ShiftEnd, CtrlShiftLeft, CtrlShiftRight, HScrollLeft, HScrollRight, TogglePause, SwitchProfile }

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
    public FrameworkElement ControlsContainer = null!;  // top-level controls block added to ChainStack (toggle row + footer)

    // mouse-only
    public string?    MouseGesture;
    public string     Label = "";

    // keyboard-only
    public Button?    CaptureBtn;
    public string     Trigger = "";

    // app scope
    public bool          IsGlobal   = true;
    public List<string>  Apps       = new();
    public List<string>  ExceptApps = new();  // global binding skipped in these apps
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

    // show a brief on-screen toast when this binding fires
    public bool      ShowToast        = false;
    public CheckBox? ToastToggle;

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
        new("Type text",        ActionKind.TypeText),
        new("Toggle pause",     ActionKind.TogglePause),
        new("Switch profile",   ActionKind.SwitchProfile),
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
    static readonly Brush AccentBrush = Br("#5B9CF6");
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
            if (row.ExceptApps.Count == 0)
            {
                row.AppScopeBtn.Content     = "Global";
                row.AppScopeBtn.Foreground  = LabelBrush;
                row.AppScopeBtn.BorderBrush = DarkBorder;
            }
            else if (row.ExceptApps.Count == 1)
            {
                row.AppScopeBtn.Content     = $"≠ {row.ExceptApps[0]}";
                row.AppScopeBtn.Foreground  = AccentBrush;
                row.AppScopeBtn.BorderBrush = AccentBrush;
            }
            else
            {
                row.AppScopeBtn.Content     = $"≠ {row.ExceptApps.Count} apps";
                row.AppScopeBtn.Foreground  = AccentBrush;
                row.AppScopeBtn.BorderBrush = AccentBrush;
            }
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
    UpdateCheckService.UpdateInfo? _updateInfo;

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

    string _filterText           = "";
    bool   _filterWasActive      = false;
    bool   _preFilterMouseExpanded = true;
    bool   _preFilterKbdExpanded   = false;

    readonly DispatcherTimer _pollTimer;
    readonly DispatcherTimer _feedbackTimer;
    string? _lastKnownActiveProfile;

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
        RefreshProfileDropdown();
        ApplySectionState();
        UpdateHookStatus();
        _setupComplete = InstallService.IsSetupComplete()
                         && InstallService.IsInstalled()
                         && InstallService.TryGetConfiguredAppRoot(out _appRoot)
                         && InstallService.IsAppInstalled(_appRoot);
        StartupToggle.IsChecked = _setupComplete && StartupService.IsEnabled();
        UpdateSetupState();
        _pollTimer.Start();
        if (_setupComplete) _ = CheckForUpdateAsync();
    }

    // =========================================================================
    // Update check
    // =========================================================================
    async Task CheckForUpdateAsync()
    {
        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        var update = await UpdateCheckService.CheckForUpdateAsync(current);
        if (update == null) return;
        if (string.Equals(InstallService.GetDismissedUpdateVersion(), update.Value.Tag, StringComparison.OrdinalIgnoreCase))
            return;

        _updateInfo = update;
        UpdateBannerText.Text = $"ShortcutHook {update.Value.Tag} is available (you have v{current.Major}.{current.Minor}.{current.Build}).";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    void UpdateDownload_Click(object sender, MouseButtonEventArgs e)
    {
        if (_updateInfo == null) return;
        try { Process.Start(new ProcessStartInfo(_updateInfo.Value.HtmlUrl) { UseShellExecute = true }); }
        catch { /* best-effort — ignore if no browser association */ }
    }

    void UpdateDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (_updateInfo != null) InstallService.SetDismissedUpdateVersion(_updateInfo.Value.Tag);
        UpdateBanner.Visibility = Visibility.Collapsed;
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
    // Search / filter
    // =========================================================================
    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilter(SearchBox.Text);
    }

    void SearchClearBtn_Click(object sender, RoutedEventArgs e) => ClearSearch();

    void ClearSearch()
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    void ApplyFilter(string raw)
    {
        _filterText = raw.Trim();
        bool active = !string.IsNullOrEmpty(_filterText);

        SearchClearBtn.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        if (active)
        {
            if (!_filterWasActive)
            {
                _preFilterMouseExpanded = _mouseExpanded;
                _preFilterKbdExpanded   = _kbdExpanded;
                _filterWasActive = true;
            }
            _mouseExpanded = true;
            _kbdExpanded   = true;
            ApplySectionState();
        }
        else if (_filterWasActive)
        {
            _mouseExpanded = _preFilterMouseExpanded;
            _kbdExpanded   = _preFilterKbdExpanded;
            _filterWasActive = false;
            ApplySectionState();
        }

        foreach (var def in MouseDefs)
        {
            if (!_mouseGestureStacks.TryGetValue(def.Gesture, out var sp)) continue;
            sp.Visibility = !active || RowsMatchFilter(def.Label, _mouseRows[def.Gesture])
                ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var card in _kbdCards)
        {
            var trigDisplay = card.Trigger.StartsWith("key:", StringComparison.Ordinal)
                ? card.Trigger.Substring(4) : card.Trigger;
            card.CardBorder.Visibility = !active || RowsMatchFilter(trigDisplay, card.Variants)
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    bool RowsMatchFilter(string label, List<Row> rows)
    {
        if (label.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        foreach (var row in rows)
        {
            foreach (var output in GetRowOutputs(row))
                if (output.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var app in row.Apps)
                if (app.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    // =========================================================================
    // Binding export / import
    // =========================================================================

    // Adds a small ⬆ export button into the spacer column (col 4) of a row's chain footer.
    void AddExportToRow(Row row, Func<BindingEntry?> getEntry)
    {
        var btn = new Button
        {
            Style   = (Style)FindResource("BtnGhost"),
            Height  = 22,
            Width   = 22,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Copy this binding to clipboard as JSON",
            Content = new TextBlock
            {
                Text                = "",
                FontFamily          = new FontFamily("Segoe MDL2 Assets"),
                FontSize            = 13,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        Grid.SetColumn(btn, 4);
        btn.Click += (_, __) => ExportBindingToClipboard(getEntry());
        row.ChainFooter.Children.Add(btn);
    }

    void ExportBindingToClipboard(BindingEntry? entry)
    {
        if (entry == null) { ShowFeedback("Nothing to export — binding has no output.", FeedbackKind.Warn); return; }
        try
        {
            var json = ConfigService.SerializeBinding(entry);
            Clipboard.SetText(json);
            ShowFeedback("Binding copied to clipboard.", FeedbackKind.Ok);
        }
        catch (Exception ex)
        {
            ShowFeedback($"Export failed: {ex.Message}", FeedbackKind.Err);
        }
    }

    BindingEntry? BuildMouseBindingEntry(string gesture, Row row)
    {
        var outputs = GetRowOutputs(row);
        if (outputs.Count == 0) return null;
        return new BindingEntry
        {
            trigger     = "mouse:" + gesture,
            outputs     = outputs,
            outputDelay = row.OutputDelay,
            apps        = (!row.IsGlobal && row.Apps.Count > 0) ? new List<string>(row.Apps) : null,
            exceptApps  = (row.IsGlobal && row.ExceptApps.Count > 0) ? new List<string>(row.ExceptApps) : null,
            enabled     = row.Enabled ? null : false,
            debounce    = row.Debounce,
            showToast   = row.ShowToast,
        };
    }

    BindingEntry? BuildKbdBindingEntry(string trigger, Row row)
    {
        if (string.IsNullOrEmpty(trigger)) return null;
        var outputs = GetRowOutputs(row);
        if (outputs.Count == 0) return null;
        return new BindingEntry
        {
            trigger     = trigger,
            outputs     = outputs,
            outputDelay = row.OutputDelay,
            apps        = (!row.IsGlobal && row.Apps.Count > 0) ? new List<string>(row.Apps) : null,
            exceptApps  = (row.IsGlobal && row.ExceptApps.Count > 0) ? new List<string>(row.ExceptApps) : null,
            enabled     = row.Enabled ? null : false,
            showToast   = row.ShowToast,
        };
    }

    void ImportBindingBtn_Click(object sender, RoutedEventArgs e) => ImportBindingFromClipboard();

    void ImportBindingFromClipboard()
    {
        string json;
        try { json = Clipboard.GetText(); }
        catch { ShowFeedback("Could not read clipboard.", FeedbackKind.Err); return; }

        if (string.IsNullOrWhiteSpace(json))
        { ShowFeedback("Clipboard is empty — copy a binding JSON first.", FeedbackKind.Err); return; }

        BindingEntry entry;
        try { entry = ConfigService.ParseBinding(json); }
        catch (Exception ex) { ShowFeedback($"Invalid binding JSON: {ex.Message}", FeedbackKind.Err); return; }

        string canon;
        try { canon = TriggerHelpers.CanonicalizeTrigger(entry.trigger); }
        catch (Exception ex) { ShowFeedback($"Invalid trigger: {ex.Message}", FeedbackKind.Err); return; }

        // Validate shortcut outputs.
        foreach (var outp in entry.outputs ?? new List<string>())
        {
            if (string.IsNullOrEmpty(outp)) continue;
            if (outp.StartsWith("open:", StringComparison.Ordinal)      ||
                outp.StartsWith("cmd:",  StringComparison.Ordinal)      ||
                outp.StartsWith("cmdw:", StringComparison.Ordinal)      ||
                outp.StartsWith("hscroll:", StringComparison.Ordinal)   ||
                outp.StartsWith("type:", StringComparison.Ordinal)      ||
                outp.StartsWith("profile:", StringComparison.Ordinal)   ||
                outp == "toggle:pause") continue;
            try { TriggerHelpers.ValidateShortcutOutput(outp); }
            catch (Exception ex) { ShowFeedback($"Invalid output '{outp}': {ex.Message}", FeedbackKind.Err); return; }
        }

        // Exact-duplicate check: same canonical trigger + same app scope.
        var existing = ConfigService.Read(InstallService.ScriptRoot);
        bool entryGlobal = entry.apps == null || entry.apps.Count == 0;
        foreach (var b in existing)
        {
            string bCanon;
            try { bCanon = TriggerHelpers.CanonicalizeTrigger(b.trigger); }
            catch { continue; }
            if (!string.Equals(bCanon, canon, StringComparison.Ordinal)) continue;
            bool bGlobal = b.apps == null || b.apps.Count == 0;
            bool sameScope = entryGlobal == bGlobal &&
                             (entryGlobal || new HashSet<string>(entry.apps!, StringComparer.OrdinalIgnoreCase)
                                                .SetEquals(b.apps ?? new List<string>()));
            if (sameScope)
            {
                ShowFeedback("This binding already exists — remove the existing one first.", FeedbackKind.Err);
                return;
            }
        }

        // Prefix-pair check (keyboard only) — warn but allow.
        bool prefixWarn = false;
        if (entry.trigger.StartsWith("key:", StringComparison.Ordinal))
        {
            try
            {
                var newParsed = TriggerHelpers.ParseKeyTrigger(entry.trigger.Substring(4));
                foreach (var b in existing)
                {
                    if (!b.trigger.StartsWith("key:", StringComparison.Ordinal)) continue;
                    try
                    {
                        var bParsed = TriggerHelpers.ParseKeyTrigger(b.trigger.Substring(4));
                        if (TriggerHelpers.IsKeyPrefixOf(newParsed, bParsed) ||
                            TriggerHelpers.IsKeyPrefixOf(bParsed, newParsed))
                        { prefixWarn = true; break; }
                    }
                    catch { }
                }
            }
            catch { }
        }

        ConfigService.AddBindingToActiveProfile(InstallService.ScriptRoot, entry);
        ReloadBindingsFromConfig();
        RestartDaemonIfRunning();

        ShowFeedback(
            prefixWarn
                ? "Binding imported — note: this trigger is a prefix of another (~80 ms delay)."
                : "Binding imported.",
            prefixWarn ? FeedbackKind.Warn : FeedbackKind.Ok);
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
            PausedBadge.Visibility = Visibility.Collapsed;
            return;
        }

        HookBtn.IsEnabled = true;
        if (DaemonService.IsRunning())
        {
            StatusDot.Fill  = GreenBrush;
            StatusText.Text = "Running";
            HookBtn.Content = "Stop";
            HookBtn.Background = RedBrush;
            PausedBadge.Visibility = IsDaemonPaused() ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            StatusDot.Fill  = RedBrush;
            StatusText.Text = "Stopped";
            HookBtn.Content = "Start";
            HookBtn.Background = GreenBrush;
            PausedBadge.Visibility = Visibility.Collapsed;
        }

        try
        {
            var ap = ConfigService.ReadConfig(InstallService.ScriptRoot).activeProfile;
            if (!string.Equals(ap, _lastKnownActiveProfile, StringComparison.Ordinal))
            {
                RefreshProfileDropdown();
                ReloadBindingsFromConfig();
            }
        }
        catch { }
    }

    static bool IsDaemonPaused()
    {
        try { return File.ReadAllText(InstallService.PauseStatePath).Trim() == "paused"; }
        catch { return false; }
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
        if (!string.IsNullOrEmpty(SearchBox?.Text)) { SearchBox.Text = ""; }
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
                    bool isGlobal  = b.apps == null || b.apps.Count == 0;
                    var  apps      = b.apps      ?? new List<string>();
                    var  exceptApps = b.exceptApps ?? new List<string>();
                    return (b.outputs ?? new List<string> { "" }, b.outputDelay, isGlobal, apps, exceptApps, b.enabled != false, b.showToast);
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
        SettingsMenuView.Visibility          = Visibility.Visible;
        SettingsProfilesView.Visibility      = Visibility.Collapsed;
        SettingsIgnoredAppsView.Visibility   = Visibility.Collapsed;
        SettingsAboutView.Visibility         = Visibility.Collapsed;
        SettingsProfileFormView.Visibility   = Visibility.Collapsed;
        SettingsProfileDeleteView.Visibility = Visibility.Collapsed;
    }

    void CloseSettings() => SettingsRoot.Visibility = Visibility.Collapsed;

    void SettingsRoot_MouseDown(object sender, MouseButtonEventArgs e) => CloseSettings();

    void SettingsCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void ManageProfilesOption_Click(object sender, MouseButtonEventArgs e) => OpenProfilesView();

    void OpenProfilesView()
    {
        ShowSettingsMenu();
        SettingsMenuView.Visibility      = Visibility.Collapsed;
        SettingsProfilesView.Visibility  = Visibility.Visible;
        BuildProfileList();
        SettingsRoot.Visibility = Visibility.Visible;
    }

    void LogViewOption_Click(object sender, MouseButtonEventArgs e)
    {
        CloseSettings();
        new LogViewerWindow { Owner = this }.Show();
    }

    void AboutOption_Click(object sender, MouseButtonEventArgs e)
    {
        SettingsMenuView.Visibility  = Visibility.Collapsed;
        SettingsAboutView.Visibility = Visibility.Visible;
    }

    // =========================================================================
    // Ignored Apps view
    // =========================================================================
    List<(CheckBox Cb, string Name)> _ignoredAppCheckBoxes = new();

    void IgnoredAppsOption_Click(object sender, MouseButtonEventArgs e)
    {
        ShowSettingsMenu();
        SettingsMenuView.Visibility        = Visibility.Collapsed;
        SettingsIgnoredAppsView.Visibility = Visibility.Visible;
        BuildIgnoredAppsList();
        SettingsRoot.Visibility = Visibility.Visible;
    }

    void BuildIgnoredAppsList()
    {
        IgnoredAppsListStack.Children.Clear();
        _ignoredAppCheckBoxes.Clear();
        IgnoredAppsCustomInput.Text = "";

        var config  = ConfigService.ReadConfig(InstallService.ScriptRoot);
        var stored  = new HashSet<string>(config.ignoredApps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var names   = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { if (!string.IsNullOrEmpty(p.ProcessName)) names.Add(p.ProcessName + ".exe"); }
                catch { }
            }
        }
        foreach (var s in stored) names.Add(s);

        foreach (var name in names)
        {
            var n  = name;
            var cb = new CheckBox
            {
                Content    = n,
                IsChecked  = stored.Contains(n),
                Foreground = Br("#CCCCCC"),
                Margin     = new Thickness(4, 3, 4, 3),
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 11,
            };
            IgnoredAppsListStack.Children.Add(cb);
            _ignoredAppCheckBoxes.Add((cb, n));
        }
    }

    void IgnoredAppsAddCustom_Click(object sender, RoutedEventArgs e)
    {
        var raw  = IgnoredAppsCustomInput.Text.Trim();
        if (string.IsNullOrEmpty(raw)) return;
        var name = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw : raw + ".exe";

        if (_ignoredAppCheckBoxes.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            IgnoredAppsCustomInput.Text = "";
            return;
        }

        var cb = new CheckBox
        {
            Content    = name,
            IsChecked  = true,
            Foreground = Br("#CCCCCC"),
            Margin     = new Thickness(4, 3, 4, 3),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 11,
        };
        IgnoredAppsListStack.Children.Insert(0, cb);
        _ignoredAppCheckBoxes.Add((cb, name));
        IgnoredAppsCustomInput.Text = "";
    }

    void IgnoredAppsSave_Click(object sender, RoutedEventArgs e)
    {
        var selected = _ignoredAppCheckBoxes
            .Where(t => t.Cb.IsChecked == true)
            .Select(t => t.Name)
            .ToList();

        ConfigService.SetIgnoredApps(InstallService.ScriptRoot, selected);
        RestartDaemonIfRunning();
        ShowFeedback("Ignored apps saved.", FeedbackKind.Ok);
        ShowSettingsMenu();
        CloseSettings();
    }

    void SettingsBack_Click(object sender, RoutedEventArgs e) => ShowSettingsMenu();

    void SettingsGitHubBtn_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/veera-bharath/ShortcutHook")
        {
            UseShellExecute = true
        });

    // =========================================================================
    // Profile management
    // =========================================================================
    string? _profileFormEditTarget;   // null = adding a new profile, else renaming this profile
    string? _profileDeleteTarget;
    string? _selectedProfileForExport; // profile highlighted for Export; defaults to the active profile
    ProfileEntry? _pendingImport;      // import awaiting a rename to resolve a name conflict
    int _pendingImportSkipped;

    void ShowProfilesView()
    {
        SettingsProfileFormView.Visibility   = Visibility.Collapsed;
        SettingsProfileDeleteView.Visibility = Visibility.Collapsed;
        SettingsProfilesView.Visibility      = Visibility.Visible;
        BuildProfileList();
    }

    void BuildProfileList()
    {
        ProfileListStack.Children.Clear();

        var config = ConfigService.ReadConfig(InstallService.ScriptRoot);

        var selectedName = config.profiles.Any(p => string.Equals(p.name, _selectedProfileForExport, StringComparison.Ordinal))
            ? _selectedProfileForExport!
            : config.activeProfile;

        foreach (var profile in config.profiles)
        {
            var name       = profile.name;
            var isActive   = string.Equals(name, config.activeProfile, StringComparison.Ordinal);
            var isSelected = string.Equals(name, selectedName, StringComparison.Ordinal);

            var radio = new RadioButton
            {
                Style             = (Style)FindResource("DarkRadio"),
                GroupName         = "ActiveProfileGroup",
                IsChecked         = isActive,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 10, 0),
            };

            var nameText = new TextBlock
            {
                Text              = name,
                Foreground        = TextBrush,
                FontSize          = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var editBtn = new Button
            {
                Content = "Edit", Style = (Style)FindResource("BtnGhost"),
                Width = 52, Height = 26, Padding = new Thickness(0), FontSize = 11,
            };
            var deleteBtn = new Button
            {
                Content = "Delete", Style = (Style)FindResource("BtnGhost"),
                Width = 58, Height = 26, Padding = new Thickness(0), FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0),
            };
            var actionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Visibility  = Visibility.Collapsed,
            };
            actionsPanel.Children.Add(editBtn);
            actionsPanel.Children.Add(deleteBtn);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(radio, 0);
            Grid.SetColumn(nameText, 1);
            Grid.SetColumn(actionsPanel, 2);
            grid.Children.Add(radio);
            grid.Children.Add(nameText);
            grid.Children.Add(actionsPanel);

            var row = new Border
            {
                Padding      = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(6),
                Background   = Transparent,
                BorderBrush  = isSelected ? AccentBrush : Transparent,
                BorderThickness = new Thickness(1),
                Margin       = new Thickness(0, 0, 0, 4),
                Child        = grid,
            };
            row.MouseEnter += (_, __) => { actionsPanel.Visibility = Visibility.Visible; if (!isSelected) row.Background = Br("#1F1F1F"); };
            row.MouseLeave += (_, __) => { actionsPanel.Visibility = Visibility.Collapsed; if (!isSelected) row.Background = Transparent; };
            row.MouseLeftButtonDown += (_, __) => { _selectedProfileForExport = name; BuildProfileList(); };

            radio.Checked  += (_, __) => SwitchActiveProfile(name);
            editBtn.Click  += (_, __) => ShowProfileForm(name);
            deleteBtn.Click += (_, __) => ShowProfileDelete(name);

            ProfileListStack.Children.Add(row);
        }

        AddProfileBtn.IsEnabled = config.profiles.Count < ProfileHelpers.MaxProfiles;
    }

    void SwitchActiveProfile(string name)
    {
        var config = ConfigService.ReadConfig(InstallService.ScriptRoot);
        if (string.Equals(config.activeProfile, name, StringComparison.Ordinal)) return;

        ConfigService.SetActiveProfile(InstallService.ScriptRoot, name);
        ReloadBindingsFromConfig();
        RefreshProfileDropdown();
        RestartDaemonIfRunning();
        ShowFeedback($"Switched to '{name}'.", FeedbackKind.Ok);
    }

    void AddProfileBtn_Click(object sender, RoutedEventArgs e) => ShowProfileForm(null);

    void ShowProfileForm(string? editTarget)
    {
        _profileFormEditTarget = editTarget;

        var config = ConfigService.ReadConfig(InstallService.ScriptRoot);
        if (editTarget == null)
        {
            ProfileFormTitle.Text        = "Add Profile";
            ProfileFormConfirmBtn.Content = "Create";
            ProfileNameBox.Text           = ProfileHelpers.NextDefaultName(config.profiles);
        }
        else
        {
            ProfileFormTitle.Text        = "Rename Profile";
            ProfileFormConfirmBtn.Content = "Save";
            ProfileNameBox.Text           = editTarget;
        }

        ProfileFormError.Visibility      = Visibility.Collapsed;
        SettingsProfilesView.Visibility  = Visibility.Collapsed;
        SettingsProfileFormView.Visibility = Visibility.Visible;
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
    }

    void ProfileFormConfirm_Click(object sender, RoutedEventArgs e)
    {
        var config = ConfigService.ReadConfig(InstallService.ScriptRoot);
        var name   = ProfileNameBox.Text.Trim();

        var error = ProfileHelpers.ValidateName(name, config.profiles, _profileFormEditTarget);
        if (error != null)
        {
            ProfileFormError.Text       = error;
            ProfileFormError.Visibility = Visibility.Visible;
            return;
        }

        if (_pendingImport != null)
        {
            _pendingImport.name = name;
            FinishImport(_pendingImport, _pendingImportSkipped);
            _pendingImport = null;
            return;
        }

        if (_profileFormEditTarget == null)
        {
            if (config.profiles.Count >= ProfileHelpers.MaxProfiles)
            {
                ProfileFormError.Text       = $"Maximum of {ProfileHelpers.MaxProfiles} profiles reached.";
                ProfileFormError.Visibility = Visibility.Visible;
                return;
            }
            ConfigService.AddProfile(InstallService.ScriptRoot, name);
            ShowFeedback($"Profile '{name}' created.", FeedbackKind.Ok);
        }
        else
        {
            ConfigService.RenameProfile(InstallService.ScriptRoot, _profileFormEditTarget, name);
            ShowFeedback($"Profile renamed to '{name}'.", FeedbackKind.Ok);
        }

        RefreshProfileDropdown();
        ShowProfilesView();
    }

    void ProfileFormCancel_Click(object sender, RoutedEventArgs e)
    {
        _pendingImport = null;
        ShowProfilesView();
    }

    void ShowProfileDelete(string name)
    {
        _profileDeleteTarget = name;
        ProfileDeleteMsg.Text         = $"Delete profile '{name}'? This cannot be undone.";
        ProfileDeleteError.Visibility = Visibility.Collapsed;
        SettingsProfilesView.Visibility      = Visibility.Collapsed;
        SettingsProfileDeleteView.Visibility = Visibility.Visible;
    }

    void ProfileDeleteConfirm_Click(object sender, RoutedEventArgs e)
    {
        var config = ConfigService.ReadConfig(InstallService.ScriptRoot);
        var name   = _profileDeleteTarget!;

        if (string.Equals(config.activeProfile, name, StringComparison.Ordinal))
        {
            ProfileDeleteError.Text       = "Switch to another profile before deleting this one.";
            ProfileDeleteError.Visibility = Visibility.Visible;
            return;
        }
        if (config.profiles.Count <= 1)
        {
            ProfileDeleteError.Text       = "At least one profile must remain.";
            ProfileDeleteError.Visibility = Visibility.Visible;
            return;
        }

        ConfigService.DeleteProfile(InstallService.ScriptRoot, name);
        ShowFeedback($"Profile '{name}' deleted.", FeedbackKind.Ok);
        RefreshProfileDropdown();
        ShowProfilesView();
    }

    void ProfileDeleteCancel_Click(object sender, RoutedEventArgs e) => ShowProfilesView();

    // =========================================================================
    // Profile import / export
    // =========================================================================
    void ExportProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        var config = ConfigService.ReadConfig(InstallService.ScriptRoot);
        var name = config.profiles.Any(p => string.Equals(p.name, _selectedProfileForExport, StringComparison.Ordinal))
            ? _selectedProfileForExport!
            : config.activeProfile;
        var profile = config.profiles.FirstOrDefault(p => string.Equals(p.name, name, StringComparison.Ordinal));
        if (profile == null) return;

        var dialog = new SaveFileDialog
        {
            Title      = "Export Profile",
            Filter     = "JSON files (*.json)|*.json",
            DefaultExt = "json",
            FileName   = $"{profile.name}.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            ConfigService.ExportProfile(dialog.FileName, profile);
            ShowFeedback($"Profile '{profile.name}' exported.", FeedbackKind.Ok);
        }
        catch (Exception ex)
        {
            ShowFeedback($"Export failed: {ex.Message}", FeedbackKind.Err);
        }
    }

    void ImportProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title  = "Import Profile",
            Filter = "JSON files (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        ProfileEntry imported;
        try
        {
            imported = ConfigService.ImportProfile(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowFeedback($"Import failed: {ex.Message}", FeedbackKind.Err);
            return;
        }

        var config = ConfigService.ReadConfig(InstallService.ScriptRoot);
        if (config.profiles.Count >= ProfileHelpers.MaxProfiles)
        {
            ShowFeedback($"Cannot import: maximum of {ProfileHelpers.MaxProfiles} profiles reached.", FeedbackKind.Err);
            return;
        }

        imported.bindings = ConfigService.SanitizeImportedBindings(imported.bindings, out var skipped);

        if (config.profiles.Any(p => string.Equals(p.name, imported.name, StringComparison.OrdinalIgnoreCase)))
        {
            _pendingImport        = imported;
            _pendingImportSkipped = skipped;
            ShowImportRenameForm(imported.name);
            return;
        }

        FinishImport(imported, skipped);
    }

    void ShowImportRenameForm(string conflictName)
    {
        _profileFormEditTarget = null;
        ProfileFormTitle.Text         = "Import Profile";
        ProfileFormConfirmBtn.Content = "Import";
        ProfileNameBox.Text            = $"{conflictName} (imported)";

        ProfileFormError.Visibility       = Visibility.Collapsed;
        SettingsProfilesView.Visibility   = Visibility.Collapsed;
        SettingsProfileFormView.Visibility = Visibility.Visible;
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
    }

    void FinishImport(ProfileEntry profile, int skipped)
    {
        var config = ConfigService.ReadConfig(InstallService.ScriptRoot);
        config.profiles.Add(profile);
        ConfigService.Save(InstallService.ScriptRoot, config);

        _selectedProfileForExport = profile.name;
        RefreshProfileDropdown();
        ShowProfilesView();

        var msg = $"Profile '{profile.name}' imported.";
        if (skipped > 0)
            ShowFeedback($"{msg} {skipped} binding{(skipped == 1 ? "" : "s")} skipped (unknown trigger).", FeedbackKind.Warn);
        else
            ShowFeedback(msg, FeedbackKind.Ok);
    }

    // Stops + restarts the daemon if it's currently running (e.g. after switching profiles).
    bool RestartDaemonIfRunning()
    {
        var wasRunning = DaemonService.IsRunning();
        if (!wasRunning) return false;

        DaemonService.Stop();
        try { DaemonService.Start(); }
        catch (Exception ex)
        {
            ShowFeedback($"Restart failed: {ex.Message}", FeedbackKind.Err);
            UpdateHookStatus();
            return wasRunning;
        }
        StatusDot.Fill  = AmberBrush;
        StatusText.Text = "Restarting...";
        return wasRunning;
    }

    // =========================================================================
    // Header profile switcher
    // =========================================================================
    void ProfileSwitcherBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileSwitcherPopup.IsOpen) { ProfileSwitcherPopup.IsOpen = false; return; }
        RefreshProfileDropdown();
        ProfileSwitcherPopup.IsOpen = true;
    }

    void RefreshProfileDropdown()
    {
        var config = ConfigService.ReadConfig(InstallService.ScriptRoot);
        _lastKnownActiveProfile = config.activeProfile;
        ProfileSwitcherLabel.Text = config.activeProfile;

        ProfileSwitcherList.Children.Clear();

        foreach (var profile in config.profiles)
        {
            var name     = profile.name;
            var isActive = string.Equals(name, config.activeProfile, StringComparison.Ordinal);

            var text = new TextBlock
            {
                Text              = (isActive ? "✓  " : "    ") + name,
                Foreground        = isActive ? AccentBrush : TextBrush,
                FontSize          = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var row = new Border
            {
                Padding      = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(4),
                Background   = Transparent,
                Cursor       = Cursors.Hand,
                Child        = text,
            };
            row.MouseEnter += (_, __) => row.Background = BtnHoverBg;
            row.MouseLeave += (_, __) => row.Background = Transparent;
            row.MouseLeftButtonUp += (_, __) =>
            {
                ProfileSwitcherPopup.IsOpen = false;
                SwitchActiveProfile(name);
            };

            ProfileSwitcherList.Children.Add(row);
        }

        ProfileSwitcherList.Children.Add(new Border
        {
            Height     = 1,
            Background = Br("#3A3A3A"),
            Margin     = new Thickness(4, 4, 4, 4),
        });

        var manageText = new TextBlock
        {
            Text              = "Manage Profiles →",
            Foreground        = AccentBrush,
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var manageRow = new Border
        {
            Padding      = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(4),
            Background   = Transparent,
            Cursor       = Cursors.Hand,
            Child        = manageText,
        };
        manageRow.MouseEnter += (_, __) => manageRow.Background = BtnHoverBg;
        manageRow.MouseLeave += (_, __) => manageRow.Background = Transparent;
        manageRow.MouseLeftButtonUp += (_, __) =>
        {
            ProfileSwitcherPopup.IsOpen = false;
            OpenProfilesView();
        };
        ProfileSwitcherList.Children.Add(manageRow);
    }

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
                debounce: globalEntry?.debounce ?? false,
                showToast: globalEntry?.showToast ?? false,
                exceptApps: globalEntry?.exceptApps ?? new List<string>());

            // App-scoped variant rows
            if (bindings != null)
                foreach (var b in bindings.Where(b => b.apps != null && b.apps.Count > 0))
                    AddMouseVariantRow(def, gestureSP,
                        b.outputs ?? new List<string> { "" },
                        b.outputDelay, false, b.apps!, b.enabled != false, isGlobal: false,
                        debounce: b.debounce, showToast: b.showToast);

            MouseStack.Children.Add(gestureSP);
        }

    }

    void AddMouseVariantRow(MouseGestureDef def, StackPanel container, List<string> outputs, int outputDelay,
                            bool isGlobal_scope, List<string> apps, bool enabled, bool isGlobal, bool debounce = false,
                            bool showToast = false, List<string>? exceptApps = null)
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
            ShowToast        = showToast,
        };

        BuildChainStack(row, outputs, isGlobal_scope, apps, enabled, exceptApps);
        AddExportToRow(row, () => BuildMouseBindingEntry(def.Gesture, row));
        if (!enabled) grid.Opacity = 0.45;

        if (isGlobal)
            variantBtn.Click += (_, __) => AddMouseVariantRow(def, container, new List<string> { "" }, 0, false, new List<string>(), true, isGlobal: false, exceptApps: new List<string>());
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
    void BuildChainStack(Row row, List<string> outputs, bool isGlobal, List<string> apps, bool enabled, List<string>? exceptApps = null)
    {
        row.ChainStack.Children.Clear();
        row.Chain.Clear();

        var effectiveOutputs = outputs.Count > 0 ? outputs : new List<string> { "" };
        foreach (var o in effectiveOutputs)
            AddChainItem(row, o, rebuild: false);

        var controlRow = BuildChainControlRow(row, isGlobal, apps, enabled, exceptApps);
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

        if (rebuild && row.ControlsContainer != null)
        {
            int footerIdx = row.ChainStack.Children.IndexOf(row.ControlsContainer);
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

    // Builds the bottom control area for a variant as two stacked rows:
    //   Row 1: [+ chain][delay-lbl][delay-tb][ms][spacer *][AppScopeBtn], as usual.
    //   Row 2 (bottom, right-aligned): all toggles — enable, toast, and (scroll
    //   gestures only) debounce. Keeping every toggle on its own row means it
    //   never competes for space with the chain/delay/scope controls above.
    FrameworkElement BuildChainControlRow(Row row, bool isGlobal, List<string> apps, bool enabled, List<string>? exceptApps = null)
    {
        // ---- Toggle row (placed last) ----
        var toggleRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            // Left offset matches "+ chain"'s button border + padding so the
            // "enable" label lines up with the "+ chain" text above it.
            Margin              = new Thickness(7, 3, 0, 0),
        };

        var enableLbl = new TextBlock
        {
            Text = "enable",
            Foreground = DimBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0),
            ToolTip = "Enable this binding",
        };
        var enableToggle = new CheckBox
        {
            Style     = (Style)FindResource("Toggle"),
            IsChecked = enabled,
            ToolTip   = "Enable this binding",
        };
        row.EnabledToggle = enableToggle;
        row.Enabled       = enabled;
        enableToggle.Checked   += (_, __) => { row.Enabled = true;  row.Container.Opacity = 1.0; };
        enableToggle.Unchecked += (_, __) => { row.Enabled = false; row.Container.Opacity = 0.45; };
        toggleRow.Children.Add(enableLbl);
        toggleRow.Children.Add(enableToggle);

        var toastLbl = new TextBlock
        {
            Text = "toast",
            Foreground = DimBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 3, 0),
            ToolTip = "Show a brief on-screen toast when this binding fires",
        };
        var toastToggle = new CheckBox
        {
            Style     = (Style)FindResource("Toggle"),
            IsChecked = row.ShowToast,
            ToolTip   = "Show a brief on-screen toast when this binding fires",
        };
        row.ToastToggle = toastToggle;
        toastToggle.Checked   += (_, __) => row.ShowToast = true;
        toastToggle.Unchecked += (_, __) => row.ShowToast = false;
        toggleRow.Children.Add(toastLbl);
        toggleRow.Children.Add(toastToggle);

        if (row.MouseGesture?.Contains("scroll") == true)
        {
            var debounceLbl = new TextBlock
            {
                Text = "debounce",
                Foreground = DimBrush,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 3, 0),
                ToolTip = "Ignore repeated scroll firings within 200 ms",
            };
            var debounceToggle = new CheckBox
            {
                Style     = (Style)FindResource("Toggle"),
                IsChecked = row.Debounce,
                ToolTip   = "Ignore repeated scroll firings within 200 ms",
            };
            row.DebounceToggle = debounceToggle;
            debounceToggle.Checked   += (_, __) => row.Debounce = true;
            debounceToggle.Unchecked += (_, __) => row.Debounce = false;
            toggleRow.Children.Add(debounceLbl);
            toggleRow.Children.Add(debounceToggle);
        }

        // ---- Row 1: chain / delay / scope controls ----
        var footer = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

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

        var scopeBtn = BuildAppScopeButton(row, isGlobal, apps, exceptApps);
        Grid.SetColumn(scopeBtn, 5);

        row.DelayBox = delayTB;

        footer.Children.Add(addBtn);
        footer.Children.Add(delayLbl);
        footer.Children.Add(delayTB);
        footer.Children.Add(msLbl);
        footer.Children.Add(scopeBtn);

        row.ChainFooter = footer;

        var stack = new StackPanel();
        stack.Children.Add(footer);
        stack.Children.Add(toggleRow);
        row.ControlsContainer = stack;
        return stack;
    }

    // Builds the multi-select app scope button + popup and wires it to row state.
    Button BuildAppScopeButton(Row row, bool isGlobal, List<string> initialApps, List<string>? initialExceptApps = null)
    {
        row.IsGlobal      = isGlobal;
        row.Apps          = new List<string>(initialApps);
        row.ExceptApps    = new List<string>(initialExceptApps ?? new List<string>());
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

            // Mode label: when global → "Except in:" (exclusion list), else → "Only in:"
            var modeLabel = new TextBlock
            {
                Text       = row.IsGlobal ? "Except in:" : "Only in:",
                Foreground = row.IsGlobal ? AccentBrush : AmberBrush,
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 10,
                Margin     = new Thickness(4, 2, 4, 2),
            };
            popupStack.Children.Add(modeLabel);

            // Collect running process names + stored names for whichever list is active.
            var activeList   = row.IsGlobal ? row.ExceptApps : row.Apps;
            var selectedSet  = new HashSet<string>(activeList, StringComparer.OrdinalIgnoreCase);
            var runningNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try { if (!string.IsNullOrEmpty(p.ProcessName)) runningNames.Add(p.ProcessName + ".exe"); }
                    catch { }
                }
            }
            foreach (var stored in activeList) runningNames.Add(stored);

            foreach (var procName in runningNames)
            {
                var name = procName;
                var cb = new CheckBox
                {
                    Content    = name,
                    Foreground = Br("#CCCCCC"),
                    IsChecked  = selectedSet.Contains(name),
                    Margin     = new Thickness(4, 2, 4, 2),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 11,
                };
                cb.Checked += (_, __) =>
                {
                    var list = row.IsGlobal ? row.ExceptApps : row.Apps;
                    if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
                    UpdateAppScopeBtnLabel(row);
                };
                cb.Unchecked += (_, __) =>
                {
                    var list = row.IsGlobal ? row.ExceptApps : row.Apps;
                    list.RemoveAll(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
                    UpdateAppScopeBtnLabel(row);
                };
                popupStack.Children.Add(cb);
                row.AppCheckBoxes.Add((cb, name));
            }

            globalCb.Checked += (_, __) =>
            {
                row.IsGlobal = true;
                RebuildPopupItems();
                UpdateAppScopeBtnLabel(row);
            };
            globalCb.Unchecked += (_, __) =>
            {
                row.IsGlobal = false;
                RebuildPopupItems();
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

    void AddKbdTriggerCard(string trigger, List<(List<string> outputs, int outputDelay, bool isGlobal, List<string> apps, List<string> exceptApps, bool enabled, bool showToast)>? variants)
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

        addAppBtn.Click += (_, __) => AddKbdVariantRow(card, new List<string> { "" }, 0, false, new List<string>(), new List<string>(), true, false);

        delCardBtn.Click += (_, __) =>
        {
            if (_captureActive && _captureBtn == capBtn) EndCapture();
            KbdStack.Children.Remove(cardBorder);
            _kbdCards.Remove(card);
        };

        KbdStack.Children.Add(cardBorder);
        _kbdCards.Add(card);

        if (variants is { Count: > 0 })
            foreach (var (o, d, isG, apps, ex, e, st) in variants) AddKbdVariantRow(card, o, d, isG, apps, ex, e, st);
        else
            AddKbdVariantRow(card, new List<string> { "" }, 0, true, new List<string>(), new List<string>(), true, false);

        UpdateCardAccentBorder(card);
    }

    Row AddKbdVariantRow(KbdTriggerCard card, List<string> outputs, int outputDelay, bool scopeIsGlobal, List<string> scopeApps, List<string> scopeExceptApps, bool enabled, bool showToast)
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
            ShowToast   = showToast,
        };
        BuildChainStack(row, outputs, scopeIsGlobal, scopeApps, enabled, scopeExceptApps);
        AddExportToRow(row, () => BuildKbdBindingEntry(card.Trigger, row));
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
        bool hasAppScoped  = card.Variants.Any(r => !r.IsGlobal && r.Apps.Count > 0);
        bool hasExceptApps = card.Variants.Any(r => r.IsGlobal && r.ExceptApps.Count > 0);
        card.CardBorder.BorderBrush = hasAppScoped ? AmberBrush : hasExceptApps ? AccentBrush : Br("#2A2A2A");
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

        var rawKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (rawKey == Key.Escape && SearchBox.IsFocused && !string.IsNullOrEmpty(SearchBox.Text))
        {
            ClearSearch();
            e.Handled = true;
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
        if (output.StartsWith("type:", StringComparison.Ordinal) && Has(ActionKind.TypeText)) return ActionKind.TypeText;
        if (output == "hscroll:left"   && Has(ActionKind.HScrollLeft))    return ActionKind.HScrollLeft;
        if (output == "hscroll:right"  && Has(ActionKind.HScrollRight))   return ActionKind.HScrollRight;
        if (output == "toggle:pause"   && Has(ActionKind.TogglePause))    return ActionKind.TogglePause;
        if (output.StartsWith("profile:", StringComparison.Ordinal) && Has(ActionKind.SwitchProfile)) return ActionKind.SwitchProfile;
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
        if (item.Action == ActionKind.TypeText && item.OutputCtrl is TextBox prevType && prevType.Text.Length > 0)
            item.OutputValue = "type:" + prevType.Text;
        if (item.Action == ActionKind.SwitchProfile && item.OutputCtrl is ComboBox prevPcb && prevPcb.SelectedItem is string prevPn && !string.IsNullOrEmpty(prevPn))
            item.OutputValue = "profile:" + prevPn;

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
            case ActionKind.TogglePause:
            {
                var desc = action switch {
                    ActionKind.ShiftHome      => "Shift+Home — select to line start",
                    ActionKind.ShiftEnd       => "Shift+End — select to line end",
                    ActionKind.CtrlShiftLeft  => "Ctrl+Shift+Left — select word left",
                    ActionKind.CtrlShiftRight => "Ctrl+Shift+Right — select word right",
                    ActionKind.HScrollLeft    => "Horizontal scroll left",
                    ActionKind.HScrollRight   => "Horizontal scroll right",
                    ActionKind.TogglePause    => "Pause/resume all hook processing",
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
            case ActionKind.SwitchProfile:
            {
                var config   = ConfigService.ReadConfig(InstallService.ScriptRoot);
                var profiles = config.profiles.Select(p => p.name).ToList();
                var cb = new ComboBox
                {
                    Style       = (Style)FindResource("DarkCB"),
                    Height      = 28,
                    ItemsSource = profiles,
                };
                var curProfile = item.OutputValue.StartsWith("profile:", StringComparison.Ordinal)
                    ? item.OutputValue.Substring(8) : "";
                if (!string.IsNullOrEmpty(curProfile) && profiles.Contains(curProfile))
                    cb.SelectedItem = curProfile;
                else if (profiles.Count > 0)
                    cb.SelectedIndex = 0;
                item.OutputPanel.Children.Add(cb);
                item.OutputCtrl = cb;
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
            case ActionKind.TypeText:
            {
                var tb = new TextBox
                {
                    Style                       = (Style)FindResource("DarkTB"),
                    AcceptsReturn               = true,
                    AcceptsTab                  = true,
                    TextWrapping                = TextWrapping.Wrap,
                    Height                      = 56,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    FontFamily                  = new FontFamily("Consolas"),
                    FontSize                    = 11,
                    Text = item.OutputValue.StartsWith("type:", StringComparison.Ordinal)
                        ? item.OutputValue.Substring(5) : "",
                };
                item.OutputPanel.Children.Add(tb);
                item.OutputCtrl = tb;
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
        ActionKind.TypeText        => (item.OutputCtrl is TextBox typeTb && typeTb.Text.Length > 0)
                                         ? "type:" + typeTb.Text : "",
        ActionKind.ShiftHome       => "Shift+Home",
        ActionKind.ShiftEnd        => "Shift+End",
        ActionKind.CtrlShiftLeft   => "Ctrl+Shift+Left",
        ActionKind.CtrlShiftRight  => "Ctrl+Shift+Right",
        ActionKind.HScrollLeft     => "hscroll:left",
        ActionKind.HScrollRight    => "hscroll:right",
        ActionKind.TogglePause     => "toggle:pause",
        ActionKind.SwitchProfile   => (item.OutputCtrl is ComboBox pcb && pcb.SelectedItem is string pn && !string.IsNullOrEmpty(pn))
                                         ? "profile:" + pn : "",
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
                        if (outp.StartsWith("open:", StringComparison.Ordinal)    ||
                            outp.StartsWith("cmd:", StringComparison.Ordinal)     ||
                            outp.StartsWith("cmdw:", StringComparison.Ordinal)    ||
                            outp.StartsWith("hscroll:", StringComparison.Ordinal) ||
                            outp.StartsWith("type:", StringComparison.Ordinal)    ||
                            outp.StartsWith("profile:", StringComparison.Ordinal) ||
                            outp == "toggle:pause") continue;
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
                if (row.ShowToast) entry.showToast = true;
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
                            showToast   = row.ShowToast,
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
                    if (outp.StartsWith("open:", StringComparison.Ordinal)    ||
                        outp.StartsWith("cmd:", StringComparison.Ordinal)     ||
                        outp.StartsWith("cmdw:", StringComparison.Ordinal)    ||
                        outp.StartsWith("type:", StringComparison.Ordinal)    ||
                        outp.StartsWith("profile:", StringComparison.Ordinal) ||
                        outp == "toggle:pause") continue;
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
                    showToast   = row.ShowToast,
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
