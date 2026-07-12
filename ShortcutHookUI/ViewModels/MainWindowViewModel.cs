using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShortcutHookCore.Models;
using ShortcutHookUI.ViewModels;
using ShortcutHookCore.Parsing;
using ShortcutHookCore.Validation;
using ShortcutHookUI.Services;
using ShortcutHookUI.Models;
using ShortcutHookUI.Views;

namespace ShortcutHookUI.ViewModels;

public enum TabKind { All, Mouse, Kbd, AppSpecific }

public class MainWindowViewModel : ViewModelBase
{
    // Brushes matching XAML SolidColorBrushes
    private static readonly SolidColorBrush PrimaryBrush = Br("#5B9CF6");
    private static readonly SolidColorBrush SuccessBrush = Br("#4CAF50");
    private static readonly SolidColorBrush DangerBrush = Br("#E53935");
    private static readonly SolidColorBrush SecondaryBrush = Br("#888888");
    private static readonly SolidColorBrush DimBrush = Br("#666666");

    private static SolidColorBrush Br(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _feedbackTimer;
    private string _appRoot;
    private bool _setupComplete;
    private UpdateCheckService.UpdateInfo? _updateInfo;
    private string? _lastKnownActiveProfile;

    // Active apps and cache
    public List<AppEntry> AllApps { get; }

    // --- View State Properties ---
    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }

    private bool _isHookRunning;
    public bool IsHookRunning
    {
        get => _isHookRunning;
        set
        {
            if (SetProperty(ref _isHookRunning, value))
            {
                OnPropertyChanged(nameof(HookStatusText));
                OnPropertyChanged(nameof(StatusDotBrush));
            }
        }
    }

    public string HookStatusText => IsHookRunning ? "Running" : "Stopped";
    public SolidColorBrush StatusDotBrush => IsHookRunning ? SuccessBrush : SecondaryBrush;

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnSearchTextChanged();
            }
        }
    }

    private TabKind _activeTab = TabKind.All;
    public TabKind ActiveTab
    {
        get => _activeTab;
        set
        {
            if (SetProperty(ref _activeTab, value))
            {
                OnSearchTextChanged();
                OnPropertyChanged(nameof(TabAllActive));
                OnPropertyChanged(nameof(TabMouseActive));
                OnPropertyChanged(nameof(TabKbdActive));
                OnPropertyChanged(nameof(TabAppSpecificActive));
            }
        }
    }

    public bool TabAllActive => ActiveTab == TabKind.All;
    public bool TabMouseActive => ActiveTab == TabKind.Mouse;
    public bool TabKbdActive => ActiveTab == TabKind.Kbd;
    public bool TabAppSpecificActive => ActiveTab == TabKind.AppSpecific;

    // Accordion Expansion states
    private bool _mouseExpanded = true;
    public bool MouseExpanded
    {
        get => _mouseExpanded;
        set
        {
            if (SetProperty(ref _mouseExpanded, value))
            {
                OnPropertyChanged(nameof(MouseChevronText));
            }
        }
    }
    public string MouseChevronText => MouseExpanded ? "▾" : "›";

    private bool _kbdExpanded;
    public bool KbdExpanded
    {
        get => _kbdExpanded;
        set
        {
            if (SetProperty(ref _kbdExpanded, value))
            {
                OnPropertyChanged(nameof(KbdChevronText));
            }
        }
    }
    public string KbdChevronText => KbdExpanded ? "▾" : "›";

    private bool _appTriggersExpanded;
    public bool AppTriggersExpanded
    {
        get => _appTriggersExpanded;
        set
        {
            if (SetProperty(ref _appTriggersExpanded, value))
            {
                OnPropertyChanged(nameof(AppTriggersChevronText));
            }
        }
    }
    public string AppTriggersChevronText => AppTriggersExpanded ? "▾" : "›";

    // Collections
    public ObservableCollection<ProfileViewModel> Profiles { get; } = new();
    public ObservableCollection<MouseTriggerGroupViewModel> MouseTriggers { get; } = new();
    public ObservableCollection<KbdTriggerCardViewModel> KbdTriggers { get; } = new();
    public ObservableCollection<AppTriggerCardViewModel> AppTriggers { get; } = new();

    // Badges / Tabs selection
    private string _tabAllBadge = "";
    public string TabAllBadge
    {
        get => _tabAllBadge;
        set => SetProperty(ref _tabAllBadge, value);
    }

    private string _tabMouseBadge = "";
    public string TabMouseBadge
    {
        get => _tabMouseBadge;
        set => SetProperty(ref _tabMouseBadge, value);
    }

    private string _tabKbdBadge = "";
    public string TabKbdBadge
    {
        get => _tabKbdBadge;
        set => SetProperty(ref _tabKbdBadge, value);
    }

    private string _tabAppSpecificBadge = "";
    public string TabAppSpecificBadge
    {
        get => _tabAppSpecificBadge;
        set => SetProperty(ref _tabAppSpecificBadge, value);
    }

    // Dirty changes / Save bar
    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(ShowSaveBar));
            }
        }
    }

    public bool ShowSaveBar => IsDirty || ShowFeedback;

    private string _feedbackMessage = "";
    public string FeedbackMessage
    {
        get => _feedbackMessage;
        set => SetProperty(ref _feedbackMessage, value);
    }

    private bool _showFeedback;
    public bool ShowFeedback
    {
        get => _showFeedback;
        set
        {
            if (SetProperty(ref _showFeedback, value))
            {
                OnPropertyChanged(nameof(ShowSaveBar));
            }
        }
    }

    private SolidColorBrush _feedbackBrush = SuccessBrush;
    public SolidColorBrush FeedbackBrush
    {
        get => _feedbackBrush;
        set => SetProperty(ref _feedbackBrush, value);
    }

    // Setup overlay
    private bool _showSetup;
    public bool ShowSetup
    {
        get => _showSetup;
        set => SetProperty(ref _showSetup, value);
    }

    private string _setupAppFolderText = "";
    public string SetupAppFolderText
    {
        get => _setupAppFolderText;
        set => SetProperty(ref _setupAppFolderText, value);
    }

    private string _setupScriptFolderText = "%LOCALAPPDATA%\\ShortcutHook";
    public string SetupScriptFolderText
    {
        get => _setupScriptFolderText;
        set => SetProperty(ref _setupScriptFolderText, value);
    }

    private string _setupInstallStatusText = "Not installed yet.";
    public string SetupInstallStatusText
    {
        get => _setupInstallStatusText;
        set => SetProperty(ref _setupInstallStatusText, value);
    }

    private bool _setupStartMenu = true;
    public bool SetupStartMenu
    {
        get => _setupStartMenu;
        set => SetProperty(ref _setupStartMenu, value);
    }

    private bool _setupDesktop = true;
    public bool SetupDesktop
    {
        get => _setupDesktop;
        set => SetProperty(ref _setupDesktop, value);
    }

    private bool _completeSetupEnabled;
    public bool CompleteSetupEnabled
    {
        get => _completeSetupEnabled;
        set => SetProperty(ref _completeSetupEnabled, value);
    }

    private string _setupHintText = "Install the runtime first to continue.";
    public string SetupHintText
    {
        get => _setupHintText;
        set => SetProperty(ref _setupHintText, value);
    }

    // Update banner
    private bool _showUpdateBanner;
    public bool ShowUpdateBanner
    {
        get => _showUpdateBanner;
        set => SetProperty(ref _showUpdateBanner, value);
    }

    private string _updateBannerText = "";
    public string UpdateBannerText
    {
        get => _updateBannerText;
        set => SetProperty(ref _updateBannerText, value);
    }

    // Update overlay
    private bool _showUpdateOverlay;
    public bool ShowUpdateOverlay
    {
        get => _showUpdateOverlay;
        set => SetProperty(ref _showUpdateOverlay, value);
    }

    private string _updateOverlayMessageText = "";
    public string UpdateOverlayMessageText
    {
        get => _updateOverlayMessageText;
        set => SetProperty(ref _updateOverlayMessageText, value);
    }

    private string _updateOverlayPathText = "";
    public string UpdateOverlayPathText
    {
        get => _updateOverlayPathText;
        set => SetProperty(ref _updateOverlayPathText, value);
    }

    // Settings Card View
    private bool _showSettings;
    public bool ShowSettings
    {
        get => _showSettings;
        set => SetProperty(ref _showSettings, value);
    }

    private string _settingsActiveView = "Daemon";
    public string SettingsActiveView
    {
        get => _settingsActiveView;
        set
        {
            if (SetProperty(ref _settingsActiveView, value))
            {
                OnPropertyChanged(nameof(DaemonViewActive));
                OnPropertyChanged(nameof(ProfilesViewActive));
                OnPropertyChanged(nameof(IgnoredAppsViewActive));
                OnPropertyChanged(nameof(AboutViewActive));
            }
        }
    }

    public bool DaemonViewActive => SettingsActiveView == "Daemon";
    public bool ProfilesViewActive => SettingsActiveView == "Profiles";
    public bool IgnoredAppsViewActive => SettingsActiveView == "IgnoredApps";
    public bool AboutViewActive => SettingsActiveView == "About";

    private bool _startupLaunchOnLogin;
    public bool StartupLaunchOnLogin
    {
        get => _startupLaunchOnLogin;
        set => SetProperty(ref _startupLaunchOnLogin, value);
    }

    private string _daemonStatusDetail = "";
    public string DaemonStatusDetail
    {
        get => _daemonStatusDetail;
        set => SetProperty(ref _daemonStatusDetail, value);
    }

    private SolidColorBrush _daemonStatusDotBrush = SecondaryBrush;
    public SolidColorBrush DaemonStatusDotBrush
    {
        get => _daemonStatusDotBrush;
        set => SetProperty(ref _daemonStatusDotBrush, value);
    }

    public ObservableCollection<string> IgnoredApps { get; } = new();

    private string _ignoredAppsCustomInput = "";
    public string IgnoredAppsCustomInput
    {
        get => _ignoredAppsCustomInput;
        set => SetProperty(ref _ignoredAppsCustomInput, value);
    }

    // Profile form within Settings
    private bool _showProfileForm;
    public bool ShowProfileForm
    {
        get => _showProfileForm;
        set => SetProperty(ref _showProfileForm, value);
    }

    private string _profileFormTitle = "Add Profile";
    public string ProfileFormTitle
    {
        get => _profileFormTitle;
        set => SetProperty(ref _profileFormTitle, value);
    }

    private string _profileNameBox = "";
    public string ProfileNameBox
    {
        get => _profileNameBox;
        set => SetProperty(ref _profileNameBox, value);
    }

    private string _profileFormError = "";
    public string ProfileFormError
    {
        get => _profileFormError;
        set => SetProperty(ref _profileFormError, value);
    }

    // Profile delete check
    private bool _showProfileDelete;
    public bool ShowProfileDelete
    {
        get => _showProfileDelete;
        set => SetProperty(ref _showProfileDelete, value);
    }

    private string _profileDeleteMsg = "";
    public string ProfileDeleteMsg
    {
        get => _profileDeleteMsg;
        set => SetProperty(ref _profileDeleteMsg, value);
    }

    private string _profileDeleteError = "";
    public string ProfileDeleteError
    {
        get => _profileDeleteError;
        set => SetProperty(ref _profileDeleteError, value);
    }

    private string _settingsVersionText = "";
    public string SettingsVersionText
    {
        get => _settingsVersionText;
        set => SetProperty(ref _settingsVersionText, value);
    }

    // Key recording target (decoupled hook management)
    private object? _capturingTarget;
    public object? CapturingTarget
    {
        get => _capturingTarget;
        set => SetProperty(ref _capturingTarget, value);
    }

    // --- Filter Properties ---
    public IEnumerable<MouseTriggerGroupViewModel> FilteredMouseTriggers
    {
        get
        {
            if (ActiveTab != TabKind.All && ActiveTab != TabKind.Mouse)
                return Enumerable.Empty<MouseTriggerGroupViewModel>();

            var filter = SearchText.Trim();
            if (string.IsNullOrEmpty(filter))
                return MouseTriggers;

            return MouseTriggers
                .Where(m => RowsMatchFilter(m.Label, m.Variants, filter))
                .OrderBy(m => FuzzyScore(m.Label, filter));
        }
    }

    public IEnumerable<KbdTriggerCardViewModel> FilteredKbdTriggers
    {
        get
        {
            if (ActiveTab != TabKind.All && ActiveTab != TabKind.Kbd)
                return Enumerable.Empty<KbdTriggerCardViewModel>();

            var filter = SearchText.Trim();
            if (string.IsNullOrEmpty(filter))
                return KbdTriggers;

            return KbdTriggers
                .Where(k => RowsMatchFilter(k.Trigger, k.Variants, filter))
                .OrderBy(k => {
                    var trig = k.Trigger.StartsWith("key:", StringComparison.Ordinal)
                        ? k.Trigger.Substring(4) : k.Trigger;
                    return FuzzyScore(trig, filter);
                });
        }
    }

    public IEnumerable<AppTriggerCardViewModel> FilteredAppTriggers
    {
        get
        {
            if (ActiveTab != TabKind.All && ActiveTab != TabKind.AppSpecific)
                return Enumerable.Empty<AppTriggerCardViewModel>();

            var filter = SearchText.Trim();
            if (string.IsNullOrEmpty(filter))
                return AppTriggers;

            return AppTriggers
                .Where(a => {
                    var trigDisplay = string.Join(",", a.SelectedApps);
                    return RowsMatchFilter(trigDisplay, new List<RowViewModel> { a.Row }, filter);
                })
                .OrderBy(a => {
                    var trigDisplay = string.Join(",", a.SelectedApps);
                    return FuzzyScore(trigDisplay, filter);
                });
        }
    }

    // --- Commands ---
    public ICommand HookBtnCommand { get; }
    public ICommand SaveBtnCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ImportBindingCommand { get; }
    public ICommand AddKbdTriggerCommand { get; }
    public ICommand AddAppTriggerCommand { get; }
    
    // Sidebar actions
    public ICommand SidebarAddProfileCommand { get; }
    public ICommand SidebarSettingsCommand { get; }

    // Settings nav actions
    public ICommand NavDaemonCommand { get; }
    public ICommand NavProfilesCommand { get; }
    public ICommand NavIgnoredAppsCommand { get; }
    public ICommand NavLogsCommand { get; }
    public ICommand NavAboutCommand { get; }

    // Settings startup toggle
    public ICommand StartupToggleCommand { get; }

    // Profile administration in settings
    public ICommand AddProfileBtnCommand { get; }
    public ICommand ExportProfileBtnCommand { get; }
    public ICommand ImportProfileBtnCommand { get; }
    public ICommand ProfileFormConfirmCommand { get; }
    public ICommand ProfileFormCancelCommand { get; }
    public ICommand ProfileDeleteConfirmCommand { get; }
    public ICommand ProfileDeleteCancelCommand { get; }

    // Ignored apps actions
    public ICommand IgnoredAppsAddCustomCommand { get; }
    public ICommand IgnoredAppsSaveCommand { get; }
    public ICommand IgnoredAppsCancelCommand { get; }

    // Update overlay actions
    public ICommand UpdateOverlayCancelCommand { get; }
    public ICommand UpdateOverlayInstallCommand { get; }
    public ICommand UpdateDismissCommand { get; }
    public ICommand UpdateDownloadCommand { get; }

    // Tab clicks
    public ICommand TabAllCommand { get; }
    public ICommand TabMouseCommand { get; }
    public ICommand TabKbdCommand { get; }
    public ICommand TabAppSpecificCommand { get; }

    // Accordions
    public ICommand ToggleMouseAccordionCommand { get; }
    public ICommand ToggleKbdAccordionCommand { get; }
    public ICommand ToggleAppTriggersAccordionCommand { get; }

    public MainWindowViewModel()
    {
        _appRoot = InstallService.TryGetConfiguredAppRoot(out var configuredRoot)
            ? configuredRoot
            : InstallService.DefaultAppRoot;

        AllApps = AppScanner.Scan();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pollTimer.Tick += (_, __) => UpdateHookStatus();

        _feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _feedbackTimer.Tick += (_, __) =>
        {
            ShowFeedback = false;
            _feedbackTimer.Stop();
        };

        // Wire Commands
        HookBtnCommand = new RelayCommand(HookBtn_Click);
        SaveBtnCommand = new RelayCommand(SaveBtn_Click);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");

        ImportBindingCommand = new RelayCommand(() =>
        {
            try
            {
                var txt = Clipboard.GetText()?.Trim();
                if (string.IsNullOrEmpty(txt))
                {
                    ShowFeedbackMsg("Clipboard is empty.", FeedbackKind.Err);
                    return;
                }
                var entry = System.Text.Json.JsonSerializer.Deserialize<BindingEntry>(txt);
                if (entry == null || string.IsNullOrEmpty(entry.trigger))
                {
                    ShowFeedbackMsg("Invalid binding entry JSON.", FeedbackKind.Err);
                    return;
                }
                AddBindingToView(entry, insertAtTop: true);
                MarkDirty();
                ShowFeedbackMsg("Imported binding from clipboard.", FeedbackKind.Ok);
            }
            catch (Exception ex)
            {
                ShowFeedbackMsg("Import failed: " + ex.Message, FeedbackKind.Err);
            }
        });

        AddKbdTriggerCommand = new RelayCommand(() => AddKbdTriggerCard("", null, insertAtTop: true));
        AddAppTriggerCommand = new RelayCommand(() => AddAppTriggerCard("launch", new List<string>(), new List<string> { "" }, null, 0, true, false, "", insertAtTop: true));

        SidebarAddProfileCommand = new RelayCommand(() => ShowProfileFormMethod(null));
        SidebarSettingsCommand = new RelayCommand(OpenSettings);

        NavDaemonCommand = new RelayCommand(() => SettingsActiveView = "Daemon");
        NavProfilesCommand = new RelayCommand(() => SettingsActiveView = "Profiles");
        NavIgnoredAppsCommand = new RelayCommand(() => SettingsActiveView = "IgnoredApps");
        NavLogsCommand = new RelayCommand(() =>
        {
            var win = new LogViewerWindow { Owner = Application.Current.MainWindow };
            win.ShowDialog();
        });
        NavAboutCommand = new RelayCommand(() => SettingsActiveView = "About");

        StartupToggleCommand = new RelayCommand(() =>
        {
            if (!_setupComplete) return;
            try
            {
                StartupService.Set(StartupLaunchOnLogin);
            }
            catch (Exception ex)
            {
                ShowFeedbackMsg("Failed to update startup registry: " + ex.Message, FeedbackKind.Err);
            }
        });

        AddProfileBtnCommand = new RelayCommand(() => ShowProfileFormMethod(null));

        ExportProfileBtnCommand = new RelayCommand(() =>
        {
            var sel = SelectedProfileForExport;
            if (sel == null) return;
            var sfd = new Microsoft.Win32.SaveFileDialog { Title = "Export Profile", Filter = "JSON files (*.json)|*.json", FileName = $"{sel.Name}.json" };
            if (sfd.ShowDialog(Application.Current.MainWindow) == true)
            {
                try
                {
                    var root = ConfigService.ReadConfig(InstallService.ScriptRoot);
                    var prof = root.profiles?.FirstOrDefault(p => string.Equals(p.name, sel.Name, StringComparison.OrdinalIgnoreCase));
                    if (prof == null) return;
                    var txt = System.Text.Json.JsonSerializer.Serialize(prof, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(sfd.FileName, txt);
                    ShowFeedbackMsg("Profile exported successfully.", FeedbackKind.Ok);
                }
                catch (Exception ex)
                {
                    ShowFeedbackMsg("Export failed: " + ex.Message, FeedbackKind.Err);
                }
            }
        });

        ImportProfileBtnCommand = new RelayCommand(() =>
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = "Import Profile", Filter = "JSON files (*.json)|*.json" };
            if (ofd.ShowDialog(Application.Current.MainWindow) == true)
            {
                try
                {
                    var txt = File.ReadAllText(ofd.FileName);
                    var imported = System.Text.Json.JsonSerializer.Deserialize<ProfileEntry>(txt);
                    if (imported == null || string.IsNullOrEmpty(imported.name))
                    {
                        ShowFeedbackMsg("Invalid profile file format.", FeedbackKind.Err);
                        return;
                    }

                    var root = ConfigService.ReadConfig(InstallService.ScriptRoot);
                    if (root.profiles != null && root.profiles.Any(p => string.Equals(p.name, imported.name, StringComparison.OrdinalIgnoreCase)))
                    {
                        ShowFeedbackMsg($"Profile '{imported.name}' already exists.", FeedbackKind.Err);
                        return;
                    }

                    if (root.profiles == null) root.profiles = new List<ProfileEntry>();
                    root.profiles.Add(imported);
                    ConfigService.Save(InstallService.ScriptRoot, root);

                    ReloadBindingsFromConfig();
                    ShowFeedbackMsg($"Profile '{imported.name}' imported.", FeedbackKind.Ok);
                }
                catch (Exception ex)
                {
                    ShowFeedbackMsg("Import failed: " + ex.Message, FeedbackKind.Err);
                }
            }
        });

        ProfileFormConfirmCommand = new RelayCommand(ProfileFormConfirm);
        ProfileFormCancelCommand = new RelayCommand(() => ShowProfileForm = false);
        ProfileDeleteConfirmCommand = new RelayCommand(ProfileDeleteConfirm);
        ProfileDeleteCancelCommand = new RelayCommand(() => ShowProfileDelete = false);

        IgnoredAppsAddCustomCommand = new RelayCommand(() =>
        {
            var app = IgnoredAppsCustomInput.Trim();
            if (string.IsNullOrEmpty(app)) return;
            if (!app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app += ".exe";
            if (!IgnoredApps.Contains(app, StringComparer.OrdinalIgnoreCase))
            {
                IgnoredApps.Add(app);
            }
            IgnoredAppsCustomInput = "";
        });

        IgnoredAppsSaveCommand = new RelayCommand(() =>
        {
            try
            {
                var root = ConfigService.ReadConfig(InstallService.ScriptRoot);
                root.ignoredApps = IgnoredApps.ToList();
                ConfigService.Save(InstallService.ScriptRoot, root);
                ShowFeedbackMsg("Ignored apps saved.", FeedbackKind.Ok);
                OpenSettings(); // Refresh
            }
            catch (Exception ex)
            {
                ShowFeedbackMsg("Failed to save: " + ex.Message, FeedbackKind.Err);
            }
        });

        IgnoredAppsCancelCommand = new RelayCommand(OpenSettings);

        UpdateOverlayCancelCommand = new RelayCommand(() => ShowUpdateOverlay = false);
        UpdateOverlayInstallCommand = new RelayCommand(() =>
        {
            ShowUpdateOverlay = false;
            try
            {
                DaemonService.Stop();
                InstallService.Install(_appRoot);
                DaemonService.Start();
                ShowFeedbackMsg("Successfully updated!", FeedbackKind.Ok);

                if (!InstallService.IsRunningFromInstalledLocation(_appRoot))
                {
                    InstallService.LaunchInstalledApp(_appRoot);
                    Application.Current.Shutdown();
                }
            }
            catch (Exception ex)
            {
                ShowFeedbackMsg("Failed to run installer: " + ex.Message, FeedbackKind.Err);
            }
        });

        UpdateDismissCommand = new RelayCommand(() =>
        {
            ShowUpdateBanner = false;
            if (_updateInfo != null)
                InstallService.SetDismissedUpdateVersion(_updateInfo.Value.Tag);
        });

        UpdateDownloadCommand = new RelayCommand(() =>
        {
            if (_updateInfo == null) return;
            try { Process.Start(new ProcessStartInfo(_updateInfo.Value.HtmlUrl) { UseShellExecute = true }); }
            catch { }
        });

        TabAllCommand = new RelayCommand(() => ActiveTab = TabKind.All);
        TabMouseCommand = new RelayCommand(() => ActiveTab = TabKind.Mouse);
        TabKbdCommand = new RelayCommand(() => ActiveTab = TabKind.Kbd);
        TabAppSpecificCommand = new RelayCommand(() => ActiveTab = TabKind.AppSpecific);

        ToggleMouseAccordionCommand = new RelayCommand(() => MouseExpanded = !MouseExpanded);
        ToggleKbdAccordionCommand = new RelayCommand(() => KbdExpanded = !KbdExpanded);
        ToggleAppTriggersAccordionCommand = new RelayCommand(() => AppTriggersExpanded = !AppTriggersExpanded);

        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        SettingsVersionText = $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    public void OnLoaded()
    {
        _pollTimer.Start();
        RefreshInstallState();
        ReloadBindingsFromConfig();
        UpdateHookStatus();

        _setupComplete = InstallService.IsSetupComplete()
                         && InstallService.IsInstalled()
                         && InstallService.TryGetConfiguredAppRoot(out _appRoot)
                         && InstallService.IsAppInstalled(_appRoot);

        StartupLaunchOnLogin = _setupComplete && StartupService.IsEnabled();
        ShowSetup = !_setupComplete;

        if (_setupComplete)
        {
            var runningVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            var installedVersion = InstallService.GetInstalledVersion() ?? new Version(0, 0, 0);

            if (!InstallService.IsRunningFromInstalledLocation(_appRoot) || runningVersion > installedVersion)
            {
                UpdateOverlayMessageText = $"You're running a newer version of ShortcutHook. This will update your installation to v{runningVersion.Major}.{runningVersion.Minor}.{runningVersion.Build}.";
                UpdateOverlayPathText = $"Target location: {_appRoot}";
                ShowUpdateOverlay = true;
            }
            else
            {
                _ = CheckForUpdateAsync();
            }
        }
    }

    public void OnClosed()
    {
        _pollTimer.Stop();
        _feedbackTimer.Stop();
        CapturingTarget = null;
    }

    private void RefreshInstallState()
    {
        SetupAppFolderText = _appRoot;
        if (InstallService.IsInstalled() && InstallService.IsAppInstalled(_appRoot))
        {
            var installedVersion = InstallService.GetInstalledVersion();
            var status = installedVersion != null ? $"Installed v{installedVersion.Major}.{installedVersion.Minor}.{installedVersion.Build}." : "Installed.";
            SetupInstallStatusText = status;
            CompleteSetupEnabled = true;
            SetupHintText = "Setup complete! Click finish to run the app.";
        }
        else
        {
            SetupInstallStatusText = "Not installed yet.";
            CompleteSetupEnabled = false;
            SetupHintText = "Install the runtime first to continue.";
        }
    }

    private void OnSearchTextChanged()
    {
        OnPropertyChanged(nameof(FilteredMouseTriggers));
        OnPropertyChanged(nameof(FilteredKbdTriggers));
        OnPropertyChanged(nameof(FilteredAppTriggers));

        bool active = !string.IsNullOrEmpty(SearchText);
        if (ActiveTab == TabKind.All)
        {
            if (active)
            {
                MouseExpanded = true;
                KbdExpanded = true;
                AppTriggersExpanded = true;
            }
        }
    }

    private void OpenSettings()
    {
        ShowSettings = true;
        SettingsActiveView = "Daemon";

        // Refresh settings state
        StartupLaunchOnLogin = _setupComplete && StartupService.IsEnabled();
        bool daemonRunning = DaemonService.IsRunning();
        bool daemonPaused = daemonRunning && IsDaemonPaused();
        DaemonStatusDetail = daemonRunning ? (daemonPaused ? "Paused" : "Running") : "Stopped";
        DaemonStatusDotBrush = daemonRunning ? (daemonPaused ? Br("#E8C25C") : SuccessBrush) : SecondaryBrush;

        var root = ConfigService.ReadConfig(InstallService.ScriptRoot);
        IgnoredApps.Clear();
        if (root.ignoredApps != null)
        {
            foreach (var app in root.ignoredApps) IgnoredApps.Add(app);
        }
    }

    public void MarkDirty()
    {
        IsDirty = true;
    }

    public void ShowFeedbackMsg(string msg, FeedbackKind kind)
    {
        FeedbackMessage = msg;
        FeedbackBrush = kind == FeedbackKind.Err ? DangerBrush : SuccessBrush;
        ShowFeedback = true;

        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private bool IsDaemonPaused()
    {
        try { return File.ReadAllText(InstallService.PauseStatePath).Trim() == "paused"; }
        catch { return false; }
    }

    private void RestartDaemonIfRunning()
    {
        if (DaemonService.IsRunning())
        {
            DaemonService.Stop();
            DaemonService.Start();
        }
    }

    private void HookBtn_Click()
    {
        var isRunning = DaemonService.IsRunning();
        if (isRunning)
        {
            DaemonService.Stop();
        }
        else
        {
            DaemonService.Start();
        }
        UpdateHookStatus();
    }

    public void UpdateHookStatus()
    {
        IsHookRunning = DaemonService.IsRunning();
        IsPaused = IsHookRunning && IsDaemonPaused();

        // Auto reload bindings if profile changed
        var activeProfile = ConfigService.ReadConfig(InstallService.ScriptRoot).activeProfile ?? "Default";
        if (_lastKnownActiveProfile != null && _lastKnownActiveProfile != activeProfile)
        {
            ReloadBindingsFromConfig();
        }
        _lastKnownActiveProfile = activeProfile;
    }

    public void ReloadBindingsFromConfig()
    {
        IsDirty = false;

        // Load Profiles
        var root = ConfigService.ReadConfig(InstallService.ScriptRoot);
        Profiles.Clear();
        var activeName = root.activeProfile ?? "Default";
        Profiles.Add(new ProfileViewModel("Default", SelectProfile, SelectProfileForExportMethod, RenameProfile, DeleteProfile) { IsSelected = string.Equals(activeName, "Default", StringComparison.OrdinalIgnoreCase) });
        if (root.profiles != null)
        {
            foreach (var profile in root.profiles)
            {
                Profiles.Add(new ProfileViewModel(profile.name, SelectProfile, SelectProfileForExportMethod, RenameProfile, DeleteProfile) { IsSelected = string.Equals(activeName, profile.name, StringComparison.OrdinalIgnoreCase) });
            }
        }

        // Active bindings
        var activeBindings = ConfigService.GetActiveProfile(root).bindings;

        // Load Mouse Triggers
        MouseTriggers.Clear();
        var mouseGroups = new Dictionary<string, List<BindingEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in activeBindings)
        {
            if (!b.trigger.StartsWith("mouse:", StringComparison.Ordinal)) continue;
            var g = b.trigger.Substring(6);
            if (!mouseGroups.TryGetValue(g, out var list))
            {
                list = new List<BindingEntry>();
                mouseGroups[g] = list;
            }
            list.Add(b);
        }

        foreach (var def in MouseDefs)
        {
            mouseGroups.TryGetValue(def.Gesture, out var bindings);
            var group = new MouseTriggerGroupViewModel(def, MarkDirty, AllApps, Profiles, RecordAction);

            // Global row
            var globalEntry = bindings?.FirstOrDefault(b => b.apps == null || b.apps.Count == 0);
            var globalRow = new RowViewModel(MarkDirty, AllApps, Profiles, RecordAction, group.DeleteVariant,
                                             isMouseTrigger: true, def.Gesture, hasAppScope: true, BuildActionsForGesture(def));
            globalRow.InitializeChain(globalEntry?.outputs ?? new List<string> { "" }, globalEntry?.outputDelays, globalEntry?.outputDelay ?? 0);
            globalRow.InitializeAppScope(isGlobal: true, new List<string>(), globalEntry?.exceptApps);
            globalRow.Enabled = globalEntry?.enabled != false;
            globalRow.Debounce = globalEntry?.debounce ?? false;
            globalRow.ShowToast = globalEntry?.showToast ?? false;
            globalRow.NoteLabel = globalEntry?.label ?? "";
            group.Variants.Add(globalRow);

            // App-scoped variant rows
            if (bindings != null)
            {
                foreach (var b in bindings.Where(b => b.apps != null && b.apps.Count > 0))
                {
                    var row = new RowViewModel(MarkDirty, AllApps, Profiles, RecordAction, group.DeleteVariant,
                                               isMouseTrigger: true, def.Gesture, hasAppScope: true, BuildActionsForGesture(def));
                    row.InitializeChain(b.outputs ?? new List<string> { "" }, b.outputDelays, b.outputDelay);
                    row.InitializeAppScope(isGlobal: false, b.apps!, null);
                    row.Enabled = b.enabled != false;
                    row.Debounce = b.debounce;
                    row.ShowToast = b.showToast;
                    row.NoteLabel = b.label ?? "";
                    group.Variants.Add(row);
                }
            }
            MouseTriggers.Add(group);
        }

        // Load Keyboard Triggers
        KbdTriggers.Clear();
        var kbdGroups = new Dictionary<string, List<BindingEntry>>(StringComparer.Ordinal);
        var kbdOrder = new List<string>();
        foreach (var b in activeBindings)
        {
            if (b.trigger.StartsWith("mouse:", StringComparison.Ordinal)) continue;
            if (b.trigger.StartsWith("launch:", StringComparison.Ordinal) ||
                b.trigger.StartsWith("exit:", StringComparison.Ordinal) ||
                b.trigger.StartsWith("focus:", StringComparison.Ordinal) ||
                b.trigger.StartsWith("blur:", StringComparison.Ordinal)) continue;
            if (!b.trigger.StartsWith("key:", StringComparison.Ordinal)) continue;

            if (!kbdGroups.TryGetValue(b.trigger, out var list))
            {
                list = new List<BindingEntry>();
                kbdGroups[b.trigger] = list;
                kbdOrder.Add(b.trigger);
            }
            list.Add(b);
        }

        foreach (var trig in kbdOrder)
        {
            var bindings = kbdGroups[trig];
            var card = new KbdTriggerCardViewModel(trig, MarkDirty, AllApps, Profiles, RecordAction, DeleteKbdCard, RecordKbdCard);

            foreach (var b in bindings)
            {
                var row = new RowViewModel(MarkDirty, AllApps, Profiles, RecordAction, card.DeleteVariant,
                                           isMouseTrigger: false, null, hasAppScope: true, StandardActions);
                row.InitializeChain(b.outputs ?? new List<string> { "" }, b.outputDelays, b.outputDelay);
                row.InitializeAppScope(b.apps == null || b.apps.Count == 0, b.apps ?? new List<string>(), b.exceptApps);
                row.Enabled = b.enabled != false;
                row.ShowToast = b.showToast;
                row.NoteLabel = b.label ?? "";
                card.Variants.Add(row);
            }
            KbdTriggers.Add(card);
        }

        // Load App Triggers
        AppTriggers.Clear();
        foreach (var b in activeBindings)
        {
            string? kind = null;
            List<string>? apps = null;
            if (b.trigger.StartsWith("launch:", StringComparison.Ordinal))
            {
                kind = "launch";
                apps = b.trigger.Substring(7).Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            }
            else if (b.trigger.StartsWith("exit:", StringComparison.Ordinal))
            {
                kind = "exit";
                apps = b.trigger.Substring(5).Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            }
            else if (b.trigger.StartsWith("focus:", StringComparison.Ordinal))
            {
                kind = "focus";
                apps = b.trigger.Substring(6).Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            }
            else if (b.trigger.StartsWith("blur:", StringComparison.Ordinal))
            {
                kind = "blur";
                apps = b.trigger.Substring(5).Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            }

            if (kind != null && apps != null)
            {
                var card = new AppTriggerCardViewModel(MarkDirty, AllApps, Profiles, RecordAction, DeleteAppCard);
                card.Kind = kind;
                card.InitializeSelectedApps(apps);
                card.Row.InitializeChain(b.outputs ?? new List<string> { "" }, b.outputDelays, b.outputDelay);
                card.Row.Enabled = b.enabled != false;
                card.Row.ShowToast = b.showToast;
                card.Row.NoteLabel = b.label ?? "";
                AppTriggers.Add(card);
            }
        }

        UpdateTabBadges();
        OnSearchTextChanged();
    }

    private void DeleteKbdCard(KbdTriggerCardViewModel card)
    {
        KbdTriggers.Remove(card);
        MarkDirty();
        UpdateTabBadges();
        OnSearchTextChanged();
    }

    private void RecordKbdCard(KbdTriggerCardViewModel card)
    {
        if (CapturingTarget == card)
        {
            CapturingTarget = null;
        }
        else
        {
            CapturingTarget = card;
        }
    }

    private void DeleteAppCard(AppTriggerCardViewModel card)
    {
        AppTriggers.Remove(card);
        MarkDirty();
        UpdateTabBadges();
        OnSearchTextChanged();
    }

    private void RecordAction(ChainedActionViewModel step)
    {
        if (CapturingTarget == step)
        {
            CapturingTarget = null;
        }
        else
        {
            CapturingTarget = step;
        }
    }

    public void AddKbdTriggerCard(string trigger, List<(List<string> outputs, List<int>? outputDelays, int outputDelay, bool isGlobal, List<string> apps, List<string> exceptApps, bool enabled, bool showToast, string noteLabel)>? variants, bool insertAtTop = false)
    {
        var card = new KbdTriggerCardViewModel(trigger, MarkDirty, AllApps, Profiles, RecordAction, DeleteKbdCard, RecordKbdCard);
        if (variants == null || variants.Count == 0)
        {
            var row = new RowViewModel(MarkDirty, AllApps, Profiles, RecordAction, card.DeleteVariant,
                                       isMouseTrigger: false, null, hasAppScope: true, StandardActions);
            row.InitializeChain(new List<string> { "" }, null, 0);
            row.InitializeAppScope(isGlobal: true, new List<string>(), new List<string>());
            card.Variants.Add(row);
        }
        else
        {
            foreach (var v in variants)
            {
                var row = new RowViewModel(MarkDirty, AllApps, Profiles, RecordAction, card.DeleteVariant,
                                           isMouseTrigger: false, null, hasAppScope: true, StandardActions);
                row.InitializeChain(v.outputs, v.outputDelays, v.outputDelay);
                row.InitializeAppScope(v.isGlobal, v.apps, v.exceptApps);
                row.Enabled = v.enabled;
                row.ShowToast = v.showToast;
                row.NoteLabel = v.noteLabel;
                card.Variants.Add(row);
            }
        }

        if (insertAtTop)
        {
            KbdTriggers.Insert(0, card);
        }
        else
        {
            KbdTriggers.Add(card);
        }
        MarkDirty();
        UpdateTabBadges();
        OnSearchTextChanged();
    }

    public void AddAppTriggerCard(string kind, List<string> appNames, List<string> outputs, List<int>? outputDelays, int outputDelay, bool enabled, bool showToast, string noteLabel, bool insertAtTop = false)
    {
        var card = new AppTriggerCardViewModel(MarkDirty, AllApps, Profiles, RecordAction, DeleteAppCard);
        card.Kind = kind;
        card.InitializeSelectedApps(appNames);
        card.Row.InitializeChain(outputs, outputDelays, outputDelay);
        card.Row.Enabled = enabled;
        card.Row.ShowToast = showToast;
        card.Row.NoteLabel = noteLabel;

        if (insertAtTop)
        {
            AppTriggers.Insert(0, card);
        }
        else
        {
            AppTriggers.Add(card);
        }
        MarkDirty();
        UpdateTabBadges();
        OnSearchTextChanged();
    }

    private void SelectProfile(ProfileViewModel profile)
    {
        try
        {
            var root = ConfigService.ReadConfig(InstallService.ScriptRoot);
            root.activeProfile = profile.Name;
            ConfigService.Save(InstallService.ScriptRoot, root);

            // Notify daemon
            RestartDaemonIfRunning();

            ReloadBindingsFromConfig();
            ShowFeedbackMsg($"Profile switched to '{profile.Name}'.", FeedbackKind.Ok);
        }
        catch (Exception ex)
        {
            ShowFeedbackMsg("Failed to switch profile: " + ex.Message, FeedbackKind.Err);
        }
    }

    private void SelectProfileForExportMethod(ProfileViewModel profile)
    {
        foreach (var p in Profiles) p.IsSelectedForExport = false;
        profile.IsSelectedForExport = true;
        SelectedProfileForExport = profile;
    }

    private ProfileViewModel? _selectedProfileForExport;
    public ProfileViewModel? SelectedProfileForExport
    {
        get => _selectedProfileForExport;
        set => SetProperty(ref _selectedProfileForExport, value);
    }

    private ProfileViewModel? _profileEditing;

    private void ShowProfileFormMethod(ProfileViewModel? profile)
    {
        _profileEditing = profile;
        if (profile == null)
        {
            ProfileFormTitle = "Add Profile";
            ProfileNameBox = "";
        }
        else
        {
            ProfileFormTitle = "Rename Profile";
            ProfileNameBox = profile.Name;
        }
        ProfileFormError = "";
        ShowProfileForm = true;
    }

    private void ProfileFormConfirm()
    {
        var name = ProfileNameBox.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ProfileFormError = "Name cannot be empty.";
            return;
        }

        if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase))
        {
            ProfileFormError = "Cannot rename/create as 'Default'.";
            return;
        }

        try
        {
            var root = ConfigService.ReadConfig(InstallService.ScriptRoot);
            if (root.profiles == null) root.profiles = new List<ProfileEntry>();

            if (_profileEditing == null)
            {
                // Create
                if (root.profiles.Any(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    ProfileFormError = "A profile with that name already exists.";
                    return;
                }
                root.profiles.Add(new ProfileEntry { name = name, bindings = new List<BindingEntry>() });
            }
            else
            {
                // Rename
                if (string.Equals(_profileEditing.Name, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    ProfileFormError = "Cannot rename Default profile.";
                    return;
                }
                if (!string.Equals(_profileEditing.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    root.profiles.Any(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    ProfileFormError = "A profile with that name already exists.";
                    return;
                }

                var match = root.profiles.FirstOrDefault(p => string.Equals(p.name, _profileEditing.Name, StringComparison.OrdinalIgnoreCase));
                if (match != null) match.name = name;

                if (string.Equals(root.activeProfile, _profileEditing.Name, StringComparison.OrdinalIgnoreCase))
                    root.activeProfile = name;
            }

            ConfigService.Save(InstallService.ScriptRoot, root);
            ReloadBindingsFromConfig();
            ShowProfileForm = false;
        }
        catch (Exception ex)
        {
            ProfileFormError = ex.Message;
        }
    }

    private void RenameProfile(ProfileViewModel profile)
    {
        ShowProfileFormMethod(profile);
    }

    private ProfileViewModel? _profileDeleting;
    private void DeleteProfile(ProfileViewModel profile)
    {
        if (string.Equals(profile.Name, "Default", StringComparison.OrdinalIgnoreCase))
            return;

        _profileDeleting = profile;
        ProfileDeleteMsg = $"Are you sure you want to delete the profile '{profile.Name}'? This will permanently delete all its shortcut bindings.";
        ProfileDeleteError = "";
        ShowProfileDelete = true;
    }

    private void ProfileDeleteConfirm()
    {
        if (_profileDeleting == null) return;
        try
        {
            var root = ConfigService.ReadConfig(InstallService.ScriptRoot);
            if (root.profiles != null)
            {
                root.profiles.RemoveAll(p => string.Equals(p.name, _profileDeleting.Name, StringComparison.OrdinalIgnoreCase));
            }
            if (string.Equals(root.activeProfile, _profileDeleting.Name, StringComparison.OrdinalIgnoreCase))
            {
                root.activeProfile = "Default";
            }
            ConfigService.Save(InstallService.ScriptRoot, root);
            ReloadBindingsFromConfig();
            ShowProfileDelete = false;
        }
        catch (Exception ex)
        {
            ProfileDeleteError = ex.Message;
        }
    }

    private void SaveBtn_Click()
    {
        var entries = new List<BindingEntry>();

        // 1. Mouse bindings
        foreach (var group in MouseTriggers)
        {
            bool globalSeen = false;
            var appSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in group.Variants)
            {
                var outputsList = GetRowOutputs(row);
                if (outputsList.Count == 0) continue;

                if (!row.IsGlobal && row.Apps.Count == 0)
                {
                    ShowFeedbackMsg($"Mouse '{group.Label}': app-scoped variant has no apps selected.", FeedbackKind.Err);
                    return;
                }

                if (row.Enabled)
                {
                    foreach (var outp in outputsList)
                    {
                        if (outp.StartsWith("open:", StringComparison.Ordinal) ||
                            outp.StartsWith("cmd:", StringComparison.Ordinal) ||
                            outp.StartsWith("cmdw:", StringComparison.Ordinal) ||
                            outp.StartsWith("type:", StringComparison.Ordinal) ||
                            outp.StartsWith("profile:", StringComparison.Ordinal) ||
                            outp == "toggle:pause") continue;
                        try { ShortcutValidator.ValidateShortcutOutput(outp); }
                        catch (Exception ex) { ShowFeedbackMsg($"Mouse '{group.Label}': {ex.Message}", FeedbackKind.Err); return; }
                    }
                }

                if (row.IsGlobal)
                {
                    if (globalSeen) { ShowFeedbackMsg($"Mouse '{group.Label}': duplicate global binding.", FeedbackKind.Err); return; }
                    globalSeen = true;
                }
                else
                {
                    foreach (var a in row.Apps)
                    {
                        if (!appSeen.Add(a)) { ShowFeedbackMsg($"Mouse '{group.Label}': app '{a}' appears in multiple variants.", FeedbackKind.Err); return; }
                    }
                }

                var entryDelays = row.Chain.Select(item => item.Delay).Take(outputsList.Count).ToList();
                var entry = new BindingEntry
                {
                    trigger = "mouse:" + group.Gesture,
                    outputs = outputsList,
                    outputDelay = row.OutputDelay,
                    outputDelays = entryDelays.Any(d => d != 0) ? entryDelays : null,
                    apps = row.IsGlobal ? null : new List<string>(row.Apps),
                    exceptApps = (row.IsGlobal && row.ExceptApps.Count > 0) ? new List<string>(row.ExceptApps) : null,
                };
                if (!row.Enabled) entry.enabled = false;
                if (row.Debounce) entry.debounce = true;
                if (row.ShowToast) entry.showToast = true;
                if (!string.IsNullOrWhiteSpace(row.NoteLabel)) entry.label = row.NoteLabel.Trim();
                entries.Add(entry);
            }
        }

        // 2. Keyboard bindings
        var canonAppSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int cardIdx = 0;
        foreach (var card in KbdTriggers)
        {
            cardIdx++;
            if (card.Variants.Count == 0) continue;
            var trig = card.Trigger;
            bool anyContent = card.Variants.Any(r => GetRowOutputs(r).Count > 0);
            if (string.IsNullOrEmpty(trig) && !anyContent) continue;

            foreach (var row in card.Variants)
            {
                var outputsList = GetRowOutputs(row);

                if (!row.IsGlobal && row.Apps.Count == 0 && row.Enabled)
                {
                    ShowFeedbackMsg($"Keyboard trigger {cardIdx}: app-scoped variant has no apps selected.", FeedbackKind.Err);
                    return;
                }

                if (!row.Enabled)
                {
                    if (!string.IsNullOrEmpty(trig))
                    {
                        var disabledDelays = row.Chain.Select(item => item.Delay).Take(outputsList.Count).ToList();
                        entries.Add(new BindingEntry
                        {
                            trigger = trig,
                            outputs = outputsList.Count > 0 ? outputsList : new List<string> { "" },
                            outputDelay = row.OutputDelay,
                            outputDelays = disabledDelays.Any(d => d != 0) ? disabledDelays : null,
                            apps = row.IsGlobal ? null : new List<string>(row.Apps),
                            exceptApps = (row.IsGlobal && row.ExceptApps.Count > 0) ? new List<string>(row.ExceptApps) : null,
                            enabled = false,
                            showToast = row.ShowToast,
                            label = string.IsNullOrWhiteSpace(row.NoteLabel) ? null : row.NoteLabel.Trim(),
                        });
                    }
                    continue;
                }

                if (string.IsNullOrEmpty(trig)) { ShowFeedbackMsg($"Keyboard trigger {cardIdx}: no trigger recorded.", FeedbackKind.Err); return; }
                if (outputsList.Count == 0) { ShowFeedbackMsg($"Keyboard trigger {cardIdx}: no output configured.", FeedbackKind.Err); return; }

                string canon;
                try { canon = TriggerParser.CanonicalizeTrigger(trig); }
                catch (Exception ex) { ShowFeedbackMsg($"Keyboard trigger {cardIdx}: {ex.Message}", FeedbackKind.Err); return; }

                foreach (var outp in outputsList)
                {
                    if (outp.StartsWith("open:", StringComparison.Ordinal) ||
                        outp.StartsWith("cmd:", StringComparison.Ordinal) ||
                        outp.StartsWith("cmdw:", StringComparison.Ordinal) ||
                        outp.StartsWith("type:", StringComparison.Ordinal) ||
                        outp.StartsWith("profile:", StringComparison.Ordinal) ||
                        outp == "toggle:pause") continue;
                    try { ShortcutValidator.ValidateShortcutOutput(outp); }
                    catch (Exception ex) { ShowFeedbackMsg($"Keyboard trigger {cardIdx}: {ex.Message}", FeedbackKind.Err); return; }
                }

                if (row.IsGlobal)
                {
                    var dedupKey = canon + "|global";
                    if (canonAppSeen.TryGetValue(dedupKey, out var prevCard))
                    {
                        ShowFeedbackMsg($"Keyboard trigger {cardIdx}: duplicate global binding (also at trigger {prevCard}).", FeedbackKind.Err);
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
                            ShowFeedbackMsg($"Keyboard trigger {cardIdx}: app '{a}' already used in trigger {prevCard}.", FeedbackKind.Err);
                            return;
                        }
                        canonAppSeen[dedupKey] = cardIdx;
                    }
                }

                var activeDelays = row.Chain.Select(item => item.Delay).Take(outputsList.Count).ToList();
                entries.Add(new BindingEntry
                {
                    trigger = trig,
                    outputs = outputsList,
                    outputDelay = row.OutputDelay,
                    outputDelays = activeDelays.Any(d => d != 0) ? activeDelays : null,
                    apps = row.IsGlobal ? null : new List<string>(row.Apps),
                    exceptApps = (row.IsGlobal && row.ExceptApps.Count > 0) ? new List<string>(row.ExceptApps) : null,
                    showToast = row.ShowToast,
                    label = string.IsNullOrWhiteSpace(row.NoteLabel) ? null : row.NoteLabel.Trim(),
                });
            }
        }

        // 3. App triggers
        var appTriggerSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in AppTriggers)
        {
            var apps = card.SelectedApps;
            var appsLabel = string.Join(",", apps);
            var kind = card.Kind;
            var row = card.Row;
            var outputsList = GetRowOutputs(row);

            if (apps.Count == 0) { ShowFeedbackMsg("An app trigger is missing a process name.", FeedbackKind.Err); return; }
            if (outputsList.Count == 0) { ShowFeedbackMsg($"App trigger '{appsLabel}': no output configured.", FeedbackKind.Err); return; }

            var dedupKey = kind + ":" + string.Join(",", apps.Select(a => a.ToLowerInvariant()).OrderBy(a => a, StringComparer.Ordinal));
            if (!appTriggerSeen.Add(dedupKey))
            {
                ShowFeedbackMsg($"Duplicate app trigger: {kind}:{appsLabel}.", FeedbackKind.Err);
                return;
            }

            foreach (var outp in outputsList)
            {
                if (outp.StartsWith("open:", StringComparison.Ordinal) ||
                    outp.StartsWith("cmd:", StringComparison.Ordinal) ||
                    outp.StartsWith("cmdw:", StringComparison.Ordinal) ||
                    outp.StartsWith("type:", StringComparison.Ordinal) ||
                    outp.StartsWith("profile:", StringComparison.Ordinal) ||
                    outp == "toggle:pause") continue;
                try { ShortcutValidator.ValidateShortcutOutput(outp); }
                catch (Exception ex) { ShowFeedbackMsg($"App trigger '{appsLabel}': {ex.Message}", FeedbackKind.Err); return; }
            }

            var entryDelays = row.Chain.Select(item => item.Delay).Take(outputsList.Count).ToList();
            entries.Add(new BindingEntry
            {
                trigger = kind + ":" + appsLabel,
                outputs = outputsList,
                outputDelay = row.OutputDelay,
                outputDelays = entryDelays.Any(d => d != 0) ? entryDelays : null,
                enabled = row.Enabled ? null : false,
                showToast = row.ShowToast,
                label = string.IsNullOrWhiteSpace(row.NoteLabel) ? null : row.NoteLabel.Trim(),
            });
        }

        if (entries.Count == 0) { ShowFeedbackMsg("Add at least one binding before saving.", FeedbackKind.Err); return; }

        try
        {
            // Write config
            var root = ConfigService.ReadConfig(InstallService.ScriptRoot);
            var activeProfile = root.activeProfile ?? "Default";
            if (string.Equals(activeProfile, "Default", StringComparison.OrdinalIgnoreCase))
            {
                root.bindings = entries;
            }
            else
            {
                var match = root.profiles?.FirstOrDefault(p => string.Equals(p.name, activeProfile, StringComparison.OrdinalIgnoreCase));
                if (match != null) match.bindings = entries;
            }
            ConfigService.Save(InstallService.ScriptRoot, root);

            // Notify daemon
            RestartDaemonIfRunning();

            IsDirty = false;
            UpdateTabBadges();
            ShowFeedbackMsg("Changes saved successfully.", FeedbackKind.Ok);
        }
        catch (Exception ex)
        {
            ShowFeedbackMsg("Save failed: " + ex.Message, FeedbackKind.Err);
        }
    }

    private List<string> GetRowOutputs(RowViewModel row)
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

    private void AddBindingToView(BindingEntry b, bool insertAtTop = false)
    {
        // Add loaded/imported binding back into ViewModel collections
        if (b.trigger.StartsWith("mouse:", StringComparison.Ordinal))
        {
            var g = b.trigger.Substring(6);
            var group = MouseTriggers.FirstOrDefault(m => string.Equals(m.Gesture, g, StringComparison.OrdinalIgnoreCase));
            if (group != null)
            {
                var row = new RowViewModel(MarkDirty, AllApps, Profiles, RecordAction, group.DeleteVariant,
                                           isMouseTrigger: true, g, hasAppScope: true, BuildActionsForGesture(new MouseGestureDef(g, group.Label)));
                row.InitializeChain(b.outputs ?? new List<string> { "" }, b.outputDelays, b.outputDelay);
                row.InitializeAppScope(b.apps == null || b.apps.Count == 0, b.apps ?? new List<string>(), b.exceptApps);
                row.Enabled = b.enabled != false;
                row.Debounce = b.debounce;
                row.ShowToast = b.showToast;
                row.NoteLabel = b.label ?? "";
                if (insertAtTop) group.Variants.Insert(0, row);
                else group.Variants.Add(row);
            }
        }
        else if (b.trigger.StartsWith("launch:", StringComparison.Ordinal) ||
                 b.trigger.StartsWith("exit:", StringComparison.Ordinal) ||
                 b.trigger.StartsWith("focus:", StringComparison.Ordinal) ||
                 b.trigger.StartsWith("blur:", StringComparison.Ordinal))
        {
            string kind = b.trigger.Substring(0, b.trigger.IndexOf(':'));
            var appNames = b.trigger.Substring(kind.Length + 1).Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            AddAppTriggerCard(kind, appNames, b.outputs ?? new List<string> { "" }, b.outputDelays, b.outputDelay, b.enabled != false, b.showToast, b.label ?? "", insertAtTop);
        }
        else if (b.trigger.StartsWith("key:", StringComparison.Ordinal))
        {
            var card = KbdTriggers.FirstOrDefault(c => string.Equals(c.Trigger, b.trigger, StringComparison.OrdinalIgnoreCase));
            if (card == null)
            {
                card = new KbdTriggerCardViewModel(b.trigger, MarkDirty, AllApps, Profiles, RecordAction, DeleteKbdCard, RecordKbdCard);
                if (insertAtTop) KbdTriggers.Insert(0, card);
                else KbdTriggers.Add(card);
            }
            var row = new RowViewModel(MarkDirty, AllApps, Profiles, RecordAction, card.DeleteVariant,
                                       isMouseTrigger: false, null, hasAppScope: true, StandardActions);
            row.InitializeChain(b.outputs ?? new List<string> { "" }, b.outputDelays, b.outputDelay);
            row.InitializeAppScope(b.apps == null || b.apps.Count == 0, b.apps ?? new List<string>(), b.exceptApps);
            row.Enabled = b.enabled != false;
            row.ShowToast = b.showToast;
            row.NoteLabel = b.label ?? "";
            if (insertAtTop) card.Variants.Insert(0, row);
            else card.Variants.Add(row);
        }
    }

    private void UpdateTabBadges()
    {
        var bindings = ConfigService.Read(InstallService.ScriptRoot);
        int mouseCnt = bindings.Count(b => b.trigger.StartsWith("mouse:", StringComparison.Ordinal));
        int kbdCnt = KbdTriggers.Count;
        int appCnt = AppTriggers.Count;
        int allCnt = mouseCnt + kbdCnt + appCnt;

        TabAllBadge = allCnt > 0 ? $"({allCnt})" : "";
        TabMouseBadge = mouseCnt > 0 ? $"({mouseCnt})" : "";
        TabKbdBadge = kbdCnt > 0 ? $"({kbdCnt})" : "";
        TabAppSpecificBadge = appCnt > 0 ? $"({appCnt})" : "";
    }

    private async Task CheckForUpdateAsync()
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        var update = await UpdateCheckService.CheckForUpdateAsync(current);
        if (update == null) return;
        if (string.Equals(InstallService.GetDismissedUpdateVersion(), update.Value.Tag, StringComparison.OrdinalIgnoreCase))
            return;

        _updateInfo = update;
        UpdateBannerText = $"ShortcutHook {update.Value.Tag} is available (you have v{current.Major}.{current.Minor}.{current.Build}).";
        ShowUpdateBanner = true;
    }

    // --- Static Action Builders (copied faithfully from original MainWindow) ---
    public static readonly ActionDef[] StandardActions = {
        new("Trigger shortcut", ActionKind.Shortcut),
        new("Open app",         ActionKind.OpenApp),
        new("Open file",        ActionKind.OpenFile),
        new("Open folder",      ActionKind.OpenFolder),
        new("Run command",      ActionKind.Command),
        new("Type text",        ActionKind.TypeText),
        new("Toggle pause",     ActionKind.TogglePause),
        new("Switch profile",   ActionKind.SwitchProfile),
    };

    public static ActionDef[] BuildActionsForGesture(MouseGestureDef def)
    {
        if (def.GestureDefault == null) return StandardActions;
        var list = new List<ActionDef> { def.GestureDefault };
        list.AddRange(StandardActions);
        return list.ToArray();
    }

    private static readonly MouseGestureDef[] MouseDefs = {
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

    // --- Search Helper Functions ---
    private static bool RowsMatchFilter(string label, IEnumerable<RowViewModel> rows, string query)
    {
        if (FuzzyMatch(label, query)) return true;
        foreach (var row in rows)
        {
            foreach (var step in row.Chain)
            {
                if (FuzzyMatch(step.OutputValue, query)) return true;
            }
            foreach (var app in row.Apps)
            {
                if (FuzzyMatch(app, query)) return true;
            }
        }
        return false;
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
}
