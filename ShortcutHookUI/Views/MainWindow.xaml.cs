using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using ShortcutHookCore.Enums;
using ShortcutHookCore.Models;
using ShortcutHookCore.Parsing;
using ShortcutHookUI.ViewModels;
using ShortcutHookUI.Services;
using ShortcutHookUI.Models;

namespace ShortcutHookUI.Views;

public partial class MainWindow : Window
{
    static readonly Dictionary<Key, string> KeyDisplay = BuildKeyDisplay();
    static Dictionary<Key, string> BuildKeyDisplay()
    {
        var d = new Dictionary<Key, string>();
        for (var k = Key.A; k <= Key.Z; k++) d[k] = k.ToString();
        d[Key.D0] = "0"; d[Key.D1] = "1"; d[Key.D2] = "2"; d[Key.D3] = "3"; d[Key.D4] = "4";
        d[Key.D5] = "5"; d[Key.D6] = "6"; d[Key.D7] = "7"; d[Key.D8] = "8"; d[Key.D9] = "9";
        for (var k = Key.F1; k <= Key.F12; k++) d[k] = k.ToString();
        d[Key.Return]   = "Enter";
        d[Key.Tab]      = "Tab";
        d[Key.Space]    = "Space";
        d[Key.Back]     = "Back";
        d[Key.Delete]   = "Delete";
        d[Key.Insert]   = "Insert";
        d[Key.Home]     = "Home";
        d[Key.End]      = "End";
        d[Key.PageUp]   = "PgUp";
        d[Key.PageDown] = "PgDn";
        d[Key.Left]     = "Left";
        d[Key.Right]    = "Right";
        d[Key.Up]       = "Up";
        d[Key.Down]     = "Down";
        d[Key.PrintScreen] = "PrtScr";
        d[Key.Escape]   = "Esc";
        return d;
    }

    static readonly Dictionary<Key, int> ModBits = new()
    {
        [Key.LeftCtrl]  = TriggerParser.MOD_CTRL,  [Key.RightCtrl]  = TriggerParser.MOD_CTRL,
        [Key.LeftShift] = TriggerParser.MOD_SHIFT, [Key.RightShift] = TriggerParser.MOD_SHIFT,
        [Key.LeftAlt]   = TriggerParser.MOD_ALT,   [Key.RightAlt]   = TriggerParser.MOD_ALT,
        [Key.LWin]      = TriggerParser.MOD_WIN,   [Key.RWin]       = TriggerParser.MOD_WIN,
    };

    static readonly Brush AmberBrush = Br("#FFC107");
    static readonly Brush Transparent = Brushes.Transparent;
    static Brush Br(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    bool _captureActive;
    int _captureMods;
    readonly List<Key> _captureNonMods = new();

    IntPtr _captureHookId = IntPtr.Zero;
    HookApi.LowLevelKeyboardProc? _captureHookProc;

    // Palette state
    enum Section { Mouse, Kbd, AppTriggers }
    sealed class PaletteItem
    {
        public string Label { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public int Score { get; set; }
        public Action? Execute { get; set; }
        public Border? Row { get; set; }
    }
    readonly List<PaletteItem> _paletteItems = new();
    int _paletteSelectedIndex = -1;

    public MainWindow()
    {
        InitializeComponent();

        var vm = new MainWindowViewModel();
        DataContext = vm;

        vm.PropertyChanged += ViewModel_PropertyChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += (_, __) => { vm.OnClosed(); UninstallCaptureHook(); };
        Deactivated += (_, __) => { if (_captureActive) EndCapture(); };

        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewKeyUp += OnWindowPreviewKeyUp;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CapturingTarget))
        {
            var vm = (MainWindowViewModel)DataContext;
            if (vm.CapturingTarget != null)
            {
                _captureActive = true;
                _captureMods = 0;
                _captureNonMods.Clear();
                InstallCaptureHook();
                UpdateCapturingStateInViewModel(vm.CapturingTarget, isRecording: true);
            }
            else
            {
                _captureActive = false;
                UninstallCaptureHook();
            }
        }
    }

    private void UpdateCapturingStateInViewModel(object target, bool isRecording)
    {
        if (target is KbdTriggerCardViewModel kbd)
        {
            kbd.IsRecording = isRecording;
            kbd.RecordingText = isRecording ? ComposeCaptureString() : "";
        }
        else if (target is ChainedActionViewModel step)
        {
            step.IsRecording = isRecording;
            step.RecordingText = isRecording ? ComposeCaptureString() : "";
        }
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
        var vm = (MainWindowViewModel)DataContext;
        vm.OnLoaded();
    }

    // =========================================================================
    // Key capture
    // =========================================================================
    void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = (MainWindowViewModel)DataContext;
        if (vm.ShowSettings)
        {
            var settingsKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (settingsKey == Key.Escape) { vm.ShowSettings = false; e.Handled = true; }
            return;
        }

        var rawKey = e.Key == Key.System ? e.SystemKey : e.Key;

        // Command palette
        if (CommandPaletteRoot.Visibility == Visibility.Visible)
        {
            if (rawKey == Key.Escape)  { CloseCommandPalette(); e.Handled = true; return; }
            if (rawKey == Key.Down)    { MovePaletteSelection(+1); e.Handled = true; return; }
            if (rawKey == Key.Up)      { MovePaletteSelection(-1); e.Handled = true; return; }
            if (rawKey == Key.Return)  { ExecuteSelectedPaletteItem(); e.Handled = true; return; }
            return;
        }

        if (rawKey == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && !_captureActive)
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        if (rawKey == Key.Escape && SearchBox.IsFocused && !string.IsNullOrEmpty(SearchBox.Text))
        {
            vm.SearchText = "";
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
        if ((_captureMods & TriggerParser.MOD_WIN)   != 0) parts.Add("Win");
        if ((_captureMods & TriggerParser.MOD_CTRL)  != 0) parts.Add("Ctrl");
        if ((_captureMods & TriggerParser.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((_captureMods & TriggerParser.MOD_ALT)   != 0) parts.Add("Alt");
        foreach (var k in _captureNonMods) parts.Add(KeyDisplay[k]);
        return string.Join("+", parts);
    }

    void UpdateCaptureDisplay()
    {
        var vm = (MainWindowViewModel)DataContext;
        if (vm.CapturingTarget != null)
        {
            UpdateCapturingStateInViewModel(vm.CapturingTarget, isRecording: true);
        }
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
        catch { }
        return HookApi.CallNextHookEx(_captureHookId, nCode, wParam, lParam);
    }

    void FinalizeCapture()
    {
        var s = ComposeCaptureString();
        var vm = (MainWindowViewModel)DataContext;
        var target = vm.CapturingTarget;

        vm.CapturingTarget = null; // Unsubscribes hook

        if (!string.IsNullOrEmpty(s) && target != null)
        {
            if (target is KbdTriggerCardViewModel kbd)
            {
                kbd.Trigger = "key:" + s;
                kbd.IsRecording = false;
            }
            else if (target is ChainedActionViewModel step)
            {
                step.OutputValue = s;
                step.IsRecording = false;
            }
            vm.MarkDirty();
        }
    }

    void EndCapture()
    {
        var vm = (MainWindowViewModel)DataContext;
        var target = vm.CapturingTarget;
        vm.CapturingTarget = null; // Unsubscribes hook

        if (target is KbdTriggerCardViewModel kbd)
        {
            kbd.IsRecording = false;
        }
        else if (target is ChainedActionViewModel step)
        {
            step.IsRecording = false;
        }
    }

    // =========================================================================
    // Setup View Click handlers
    // =========================================================================
    private void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        var vm = (MainWindowViewModel)DataContext;
        using var fbd = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Installation Folder",
            SelectedPath = vm.SetupAppFolderText
        };
        if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            try
            {
                string targetPath = fbd.SelectedPath;
                InstallService.Install(targetPath);
                vm.OnLoaded(); // Refresh
                vm.ShowFeedbackMsg("Installed successfully.", FeedbackKind.Ok);
            }
            catch (Exception ex)
            {
                vm.ShowFeedbackMsg("Installation failed: " + ex.Message, FeedbackKind.Err);
            }
        }
    }

    private void OpenInstallBtn_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(InstallService.ScriptRoot) { UseShellExecute = true }); }
        catch { }
    }

    private void CompleteSetupBtn_Click(object sender, RoutedEventArgs e)
    {
        var vm = (MainWindowViewModel)DataContext;
        try
        {
            if (!InstallService.IsInstalled())
                throw new InvalidOperationException("Install before finishing setup.");
            string appRoot = vm.SetupAppFolderText;
            if (string.IsNullOrEmpty(appRoot) || !InstallService.IsAppInstalled(appRoot))
                throw new InvalidOperationException("App not installed. Run install first.");

            if (vm.SetupStartMenu)
                InstallService.CreateStartMenuShortcut(appRoot);

            if (vm.SetupDesktop)
                InstallService.CreateDesktopShortcut(appRoot);

            InstallService.MarkSetupComplete();
            vm.ShowSetup = false;
            vm.OnLoaded();
            vm.ShowFeedbackMsg("Setup completed successfully.", FeedbackKind.Ok);

            if (!InstallService.IsRunningFromInstalledLocation(appRoot))
            {
                InstallService.LaunchInstalledApp(appRoot);
                Close();
            }
        }
        catch (Exception ex)
        {
            vm.ShowFeedbackMsg("Setup failed: " + ex.Message, FeedbackKind.Err);
        }
    }

    // =========================================================================
    // Command Palette
    // =========================================================================
    void OpenCommandPalette()
    {
        PaletteSearchBox.Text = "";
        PaletteSearchPlaceholder.Visibility = Visibility.Visible;
        UpdatePaletteResults("");
        CommandPaletteRoot.Visibility = Visibility.Visible;
        PaletteSearchBox.Focus();
    }

    void CloseCommandPalette()
    {
        CommandPaletteRoot.Visibility = Visibility.Collapsed;
        PaletteResultsStack.Children.Clear();
    }

    void CommandPaletteRoot_MouseDown(object sender, MouseButtonEventArgs e) => CloseCommandPalette();
    void CommandPaletteCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void PaletteSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PaletteSearchPlaceholder.Visibility = PaletteSearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdatePaletteResults(PaletteSearchBox.Text);
    }

    void PaletteSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)   { MovePaletteSelection(+1); e.Handled = true; }
        else if (e.Key == Key.Up) { MovePaletteSelection(-1); e.Handled = true; }
    }

    void UpdatePaletteResults(string raw)
    {
        var query = raw.Trim();
        PaletteResultsStack.Children.Clear();
        _paletteItems.Clear();
        _paletteSelectedIndex = -1;

        var bindingItems = new List<PaletteItem>();
        var vm = (MainWindowViewModel)DataContext;

        // Mouse
        foreach (var group in vm.MouseTriggers)
        {
            var outputs = group.Variants.SelectMany(v => GetRowOutputs(v)).Distinct().ToList();
            if (!string.IsNullOrEmpty(query) && !FuzzyMatch(group.Label, query) && !outputs.Any(o => FuzzyMatch(o, query))) continue;
            var sub = outputs.Count > 0 ? string.Join(", ", outputs.Take(2)) : "";
            int score = string.IsNullOrEmpty(query) ? 0 : FuzzyScore(group.Label, query);
            var g = group;
            bindingItems.Add(new PaletteItem { Label = group.Label, Subtitle = sub, Score = score,
                Execute = () => NavigateToPaletteBinding(Section.Mouse, g) });
        }

        // Keyboard
        foreach (var card in vm.KbdTriggers)
        {
            var trig = card.Trigger.StartsWith("key:", StringComparison.Ordinal) ? card.Trigger.Substring(4) : card.Trigger;
            var outputs = card.Variants.SelectMany(v => GetRowOutputs(v)).Distinct().ToList();
            if (!string.IsNullOrEmpty(query) && !FuzzyMatch(trig, query) && !outputs.Any(o => FuzzyMatch(o, query))) continue;
            var sub = outputs.Count > 0 ? string.Join(", ", outputs.Take(2)) : "";
            int score = string.IsNullOrEmpty(query) ? 0 : FuzzyScore(trig, query);
            var c = card;
            bindingItems.Add(new PaletteItem { Label = trig, Subtitle = sub, Score = score,
                Execute = () => NavigateToPaletteBinding(Section.Kbd, c) });
        }

        // App triggers
        foreach (var card in vm.AppTriggers)
        {
            var kindLabel = card.Kind switch { "exit" => "App exits", "focus" => "App focus", "blur" => "App blur", _ => "App launches" };
            var apps = card.SelectedApps;
            var label = apps.Count > 0 ? $"{kindLabel}: {apps[0]}" : kindLabel;
            var outputs = GetRowOutputs(card.Row);
            if (!string.IsNullOrEmpty(query) && !FuzzyMatch(label, query) && !outputs.Any(o => FuzzyMatch(o, query))) continue;
            var sub = outputs.Count > 0 ? string.Join(", ", outputs.Take(2)) : "";
            int score = string.IsNullOrEmpty(query) ? 0 : FuzzyScore(label, query);
            var c = card;
            bindingItems.Add(new PaletteItem { Label = label, Subtitle = sub, Score = score,
                Execute = () => NavigateToPaletteBinding(Section.AppTriggers, c) });
        }

        if (!string.IsNullOrEmpty(query))
            bindingItems = bindingItems.OrderBy(x => x.Score).ToList();

        // Action results
        var config = ConfigService.ReadConfig(InstallService.ScriptRoot);
        bool daemonRunning = DaemonService.IsRunning();

        var actionDefs = new List<(string label, string sub, Action execute)>
        {
            ("Add shortcut", "Record a new keyboard binding", () =>
            {
                CloseCommandPalette();
                vm.ActiveTab = TabKind.All;
                vm.KbdExpanded = true;
                vm.AddKbdTriggerCard("", null, insertAtTop: true);
            }),
            ("Open settings", "Open the settings panel", () =>
            {
                CloseCommandPalette();
                vm.SidebarSettingsCommand.Execute(null);
            }),
            (daemonRunning ? "Stop daemon" : "Start daemon",
             daemonRunning ? "Stop the hook daemon" : "Start the hook daemon", () =>
             {
                 CloseCommandPalette();
                 vm.HookBtnCommand.Execute(null);
             }),
            ("Import binding", "Paste a binding JSON from the clipboard", () =>
            {
                CloseCommandPalette();
                vm.ImportBindingCommand.Execute(null);
            }),
        };

        foreach (var p in config.profiles ?? new List<ProfileEntry>())
        {
            var pName = p.name;
            var active = string.Equals(pName, config.activeProfile, StringComparison.Ordinal);
            actionDefs.Add(($"Switch to profile: {pName}", active ? "Currently active" : "Switch active profile",
                () => { CloseCommandPalette(); if (!active) vm.Profiles.FirstOrDefault(pr => string.Equals(pr.Name, pName, StringComparison.OrdinalIgnoreCase))?.SelectCommand.Execute(null); }));
        }

        var actionItems = actionDefs
            .Where(a => string.IsNullOrEmpty(query) || FuzzyMatch(a.label, query) || FuzzyMatch(a.sub, query))
            .Select(a => new PaletteItem { Label = a.label, Subtitle = a.sub, Score = string.IsNullOrEmpty(query) ? 0 : FuzzyScore(a.label, query), Execute = a.execute })
            .ToList();

        if (!string.IsNullOrEmpty(query))
            actionItems = actionItems.OrderBy(x => x.Score).ToList();

        void AddCategoryHeader(string text)
        {
            PaletteResultsStack.Children.Add(new TextBlock
            {
                Text = text, Foreground = Br("#505050"), FontSize = 10,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 8, 12, 3),
            });
        }

        var limit = bindingItems.Count > 0 && actionItems.Count > 0 ? 6 : 10;
        if (bindingItems.Count > 0) { AddCategoryHeader("BINDINGS"); foreach (var item in bindingItems.Take(limit)) AddPaletteRow(item); }
        if (actionItems.Count  > 0) { AddCategoryHeader("ACTIONS");  foreach (var item in actionItems)              AddPaletteRow(item); }

        if (_paletteItems.Count == 0)
        {
            PaletteResultsStack.Children.Add(new TextBlock
            {
                Text = "No results",
                Foreground = Br("#444444"), FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 18, 0, 18),
            });
        }
        else SetPaletteSelection(0);
    }

    void AddPaletteRow(PaletteItem item)
    {
        var labelTb = new TextBlock
        {
            Text = item.Label, Foreground = Br("#E0E0E0"), FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var stack = new StackPanel();
        stack.Children.Add(labelTb);
        if (!string.IsNullOrEmpty(item.Subtitle))
            stack.Children.Add(new TextBlock
            {
                Text = item.Subtitle, Foreground = Br("#505050"), FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
            });

        var row = new Border
        {
            Padding     = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            Cursor      = Cursors.Hand,
            Child       = stack,
            Margin      = new Thickness(0, 1, 0, 1),
        };
        item.Row = row;
        _paletteItems.Add(item);

        var idx = _paletteItems.Count - 1;
        row.MouseEnter        += (_, __) => SetPaletteSelection(idx);
        row.MouseLeftButtonDown += (_, e)  => { ExecutePaletteItem(_paletteItems[idx]); e.Handled = true; };

        PaletteResultsStack.Children.Add(row);
    }

    void SetPaletteSelection(int index)
    {
        if (index < 0 || index >= _paletteItems.Count) return;
        if (_paletteSelectedIndex >= 0 && _paletteSelectedIndex < _paletteItems.Count)
            _paletteItems[_paletteSelectedIndex].Row!.Background = Transparent;
        _paletteSelectedIndex = index;
        var row = _paletteItems[index].Row!;
        row.Background = Br("#1A3050");
        row.BringIntoView();
    }

    void MovePaletteSelection(int delta)
    {
        if (_paletteItems.Count == 0) return;
        SetPaletteSelection(Math.Clamp(_paletteSelectedIndex + delta, 0, _paletteItems.Count - 1));
    }

    void ExecuteSelectedPaletteItem()
    {
        if (_paletteSelectedIndex >= 0 && _paletteSelectedIndex < _paletteItems.Count)
            ExecutePaletteItem(_paletteItems[_paletteSelectedIndex]);
    }

    void ExecutePaletteItem(PaletteItem item) => item.Execute?.Invoke();

    void NavigateToPaletteBinding(Section section, object viewModel)
    {
        CloseCommandPalette();
        var vm = (MainWindowViewModel)DataContext;
        vm.ActiveTab = TabKind.All;
        switch (section)
        {
            case Section.Mouse:       vm.MouseExpanded       = true; break;
            case Section.Kbd:         vm.KbdExpanded         = true; break;
            case Section.AppTriggers: vm.AppTriggersExpanded = true; break;
        }

        Dispatcher.InvokeAsync(() =>
        {
            DependencyObject? container = null;
            if (section == Section.Kbd)
            {
                var ic = (ItemsControl)FindName("KbdItemsControl");
                if (ic != null) container = ic.ItemContainerGenerator.ContainerFromItem(viewModel);
            }
            else if (section == Section.AppTriggers)
            {
                var ic = (ItemsControl)FindName("AppItemsControl");
                if (ic != null) container = ic.ItemContainerGenerator.ContainerFromItem(viewModel);
            }
            else if (section == Section.Mouse)
            {
                var ic = (ItemsControl)FindName("MouseItemsControl");
                if (ic != null) container = ic.ItemContainerGenerator.ContainerFromItem(viewModel);
            }

            if (container is FrameworkElement target)
            {
                target.BringIntoView();
                FlashPaletteTarget(target);
            }
        }, DispatcherPriority.Loaded);
    }

    void FlashPaletteTarget(FrameworkElement el)
    {
        var border = el as Border ?? (el is Panel p ? p.Children.OfType<Border>().FirstOrDefault() : null);
        if (border == null) return;
        var origBrush = border.BorderBrush;
        border.BorderBrush = Br("#5B9CF6");
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        t.Tick += (_, __) => { border.BorderBrush = origBrush; t.Stop(); };
        t.Start();
    }

    private static bool FuzzyMatch(string text, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        int qi = 0;
        foreach (char c in text)
        {
            if (char.ToUpperInvariant(c) == char.ToUpperInvariant(query[qi]))
                if (++qi == query.Length) return true;
        }
        return false;
    }

    private static int FuzzyScore(string text, string query)
    {
        if (string.IsNullOrEmpty(query)) return 0;
        char q0 = char.ToUpperInvariant(query[0]);
        for (int i = 0; i < text.Length; i++)
            if (char.ToUpperInvariant(text[i]) == q0) return i;
        return int.MaxValue;
    }

    List<string> GetRowOutputs(RowViewModel row)
    {
        return row.Chain
            .Select(item => item.Action switch
            {
                ActionKind.Shortcut => item.OutputValue,
                ActionKind.OpenApp => string.IsNullOrEmpty(item.OutputValue) ? "" : item.OutputValue,
                ActionKind.OpenFile => string.IsNullOrEmpty(item.OutputValue) ? "" : item.OutputValue,
                ActionKind.OpenFolder => string.IsNullOrEmpty(item.OutputValue) ? "" : item.OutputValue,
                ActionKind.Command => string.IsNullOrEmpty(item.OutputValue) ? "" : (item.CmdShow ? "cmdw:" : "cmd:") + item.OutputValue.Trim(),
                ActionKind.TypeText => string.IsNullOrEmpty(item.OutputValue) ? "" : "type:" + item.OutputValue,
                ActionKind.ShiftHome => "Shift+Home",
                ActionKind.ShiftEnd => "Shift+End",
                ActionKind.CtrlShiftLeft => "Ctrl+Shift+Left",
                ActionKind.CtrlShiftRight => "Ctrl+Shift+Right",
                ActionKind.HScrollLeft => "hscroll:left",
                ActionKind.HScrollRight => "hscroll:right",
                ActionKind.TogglePause => "toggle:pause",
                ActionKind.SwitchProfile => string.IsNullOrEmpty(item.OutputValue) ? "" : "profile:" + item.OutputValue,
                _ => ""
            })
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private void SettingsGitHubBtn_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/veera-bharath/ShortcutHook") { UseShellExecute = true }); }
        catch { }
    }

    private void UpdateDownloadCommand(object sender, MouseButtonEventArgs e)
    {
        var vm = (MainWindowViewModel)DataContext;
        vm.UpdateDownloadCommand.Execute(null);
    }

    private void SettingsRoot_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var vm = (MainWindowViewModel)DataContext;
        vm.ShowSettings = false;
    }

    private void SettingsCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }
}
