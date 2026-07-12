using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ShortcutHookCore.Enums;
using ShortcutHookCore.Models;
using ShortcutHookCore.Parsing;
using ShortcutHookCore.Validation;
using ShortcutHookUI;

namespace ShortcutHookUI.ViewModels;

public enum FeedbackKind { Ok, Warn, Err }

public class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute == null || _canExecute();
    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

public class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
    {
        if (_canExecute == null) return true;
        if (parameter == null && typeof(T).IsValueType) return false;
        return _canExecute((T)parameter!);
    }

    public void Execute(object? parameter) => _execute((T)parameter!);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

public class ProfileViewModel : ViewModelBase
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private bool _isSelectedForExport;
    public bool IsSelectedForExport
    {
        get => _isSelectedForExport;
        set => SetProperty(ref _isSelectedForExport, value);
    }

    public ICommand SelectCommand { get; }
    public ICommand SelectForExportCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }

    public ProfileViewModel(string name, Action<ProfileViewModel> select, Action<ProfileViewModel> selectExport, Action<ProfileViewModel> rename, Action<ProfileViewModel> delete)
    {
        Name = name;
        SelectCommand = new RelayCommand(() => select(this));
        SelectForExportCommand = new RelayCommand(() => selectExport(this));
        RenameCommand = new RelayCommand(() => rename(this));
        DeleteCommand = new RelayCommand(() => delete(this));
    }
}

public class AppScopeOptionViewModel : ViewModelBase
{
    public string DisplayName { get; }
    public string ProcessName { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                SelectionChanged?.Invoke();
            }
        }
    }

    public Action? SelectionChanged { get; set; }

    public AppScopeOptionViewModel(string displayName, string processName, bool isChecked)
    {
        DisplayName = displayName;
        ProcessName = processName;
        _isChecked = isChecked;
    }
}

public class ChainedActionViewModel : ViewModelBase
{
    private ActionKind _action = ActionKind.Shortcut;
    public ActionKind Action
    {
        get => _action;
        set
        {
            if (SetProperty(ref _action, value))
            {
                switch (value)
                {
                    case ActionKind.ShiftHome: OutputValue = "Shift+Home"; break;
                    case ActionKind.ShiftEnd: OutputValue = "Shift+End"; break;
                    case ActionKind.CtrlShiftLeft: OutputValue = "Ctrl+Shift+Left"; break;
                    case ActionKind.CtrlShiftRight: OutputValue = "Ctrl+Shift+Right"; break;
                    case ActionKind.HScrollLeft: OutputValue = "hscroll:left"; break;
                    case ActionKind.HScrollRight: OutputValue = "hscroll:right"; break;
                    case ActionKind.TogglePause: OutputValue = "toggle:pause"; break;
                    case ActionKind.OpenApp:
                        if (!OutputValue.StartsWith("open:", StringComparison.Ordinal))
                        {
                            var firstApp = Apps.FirstOrDefault();
                            OutputValue = firstApp != null ? "open:" + firstApp.Path : "";
                        }
                        break;
                    case ActionKind.OpenFile:
                    case ActionKind.OpenFolder:
                        if (!OutputValue.StartsWith("open:", StringComparison.Ordinal))
                            OutputValue = "";
                        break;
                    case ActionKind.SwitchProfile:
                        if (!OutputValue.StartsWith("profile:", StringComparison.Ordinal))
                        {
                            var firstProfile = Profiles.FirstOrDefault();
                            OutputValue = firstProfile != null ? "profile:" + firstProfile.Name : "";
                        }
                        break;
                    case ActionKind.Command:
                        if (OutputValue.StartsWith("cmd:", StringComparison.Ordinal) || OutputValue.StartsWith("cmdw:", StringComparison.Ordinal))
                        {
                            CmdShow = OutputValue.StartsWith("cmdw:", StringComparison.Ordinal);
                            OutputValue = OutputValue.Substring(CmdShow ? 5 : 4);
                        }
                        break;
                    case ActionKind.TypeText:
                        if (OutputValue.StartsWith("type:", StringComparison.Ordinal))
                            OutputValue = OutputValue.Substring(5);
                        break;
                    case ActionKind.Shortcut:
                        if (OutputValue.StartsWith("open:", StringComparison.Ordinal) || OutputValue.StartsWith("profile:", StringComparison.Ordinal) || OutputValue.StartsWith("type:", StringComparison.Ordinal) || OutputValue.StartsWith("cmd:", StringComparison.Ordinal) || OutputValue.StartsWith("cmdw:", StringComparison.Ordinal))
                            OutputValue = "";
                        break;
                }
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(SelectedApp));
                OnPropertyChanged(nameof(SelectedProfileName));
                _markDirty();
            }
        }
    }

    private string _outputValue = "";
    public string OutputValue
    {
        get => _outputValue;
        set
        {
            if (SetProperty(ref _outputValue, value))
            {
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(SelectedApp));
                OnPropertyChanged(nameof(SelectedProfileName));
                _markDirty();
            }
        }
    }

    private bool _shortcutRecordMode = true;
    public bool ShortcutRecordMode
    {
        get => _shortcutRecordMode;
        set => SetProperty(ref _shortcutRecordMode, value);
    }

    private bool _cmdShow;
    public bool CmdShow
    {
        get => _cmdShow;
        set
        {
            if (SetProperty(ref _cmdShow, value))
            {
                _markDirty();
            }
        }
    }

    private int _delay;
    public int Delay
    {
        get => _delay;
        set
        {
            if (SetProperty(ref _delay, value))
            {
                _markDirty();
            }
        }
    }

    private bool _isMultiStep;
    public bool IsMultiStep
    {
        get => _isMultiStep;
        set => SetProperty(ref _isMultiStep, value);
    }

    private bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(DisplayRecordText));
            }
        }
    }

    private string _recordingText = "";
    public string RecordingText
    {
        get => _recordingText;
        set
        {
            if (SetProperty(ref _recordingText, value))
            {
                OnPropertyChanged(nameof(DisplayRecordText));
            }
        }
    }

    public string DisplayRecordText
    {
        get
        {
            if (IsRecording)
            {
                return string.IsNullOrEmpty(RecordingText) ? "● Press keys..." : "● " + RecordingText;
            }
            return !string.IsNullOrEmpty(OutputValue) ? OutputValue : "Click to record...";
        }
    }

    public string DisplayPath
    {
        get
        {
            var p = OutputValue.StartsWith("open:", StringComparison.Ordinal) ? OutputValue.Substring(5) : OutputValue;
            return string.IsNullOrEmpty(p) ? "No path selected" : p;
        }
        set
        {
            OutputValue = string.IsNullOrEmpty(value) ? "" : "open:" + value;
        }
    }

    public AppEntry? SelectedApp
    {
        get
        {
            if (Action == ActionKind.OpenApp && OutputValue.StartsWith("open:", StringComparison.Ordinal))
            {
                var path = OutputValue.Substring(5);
                return Apps.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
            }
            return null;
        }
        set
        {
            OutputValue = value != null ? "open:" + value.Path : "";
            OnPropertyChanged();
            _markDirty();
        }
    }

    public string? SelectedProfileName
    {
        get
        {
            if (Action == ActionKind.SwitchProfile && OutputValue.StartsWith("profile:", StringComparison.Ordinal))
            {
                return OutputValue.Substring(8);
            }
            return null;
        }
        set
        {
            OutputValue = !string.IsNullOrEmpty(value) ? "profile:" + value : "";
            OnPropertyChanged();
            _markDirty();
        }
    }

    public List<AppEntry> Apps { get; }
    public ObservableCollection<ProfileViewModel> Profiles { get; }

    public ICommand RecordShortcutCommand { get; }
    public ICommand ToggleRecordModeCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand BrowseFileCommand { get; }
    public ICommand BrowseFolderCommand { get; }

    private readonly Action _markDirty;

    public ChainedActionViewModel(Action markDirty, List<AppEntry> apps, ObservableCollection<ProfileViewModel> profiles, Action<ChainedActionViewModel> delete, Action<ChainedActionViewModel> record)
    {
        _markDirty = markDirty;
        Apps = apps;
        Profiles = profiles;
        DeleteCommand = new RelayCommand(() => delete(this));
        RecordShortcutCommand = new RelayCommand(() => record(this));
        ToggleRecordModeCommand = new RelayCommand(() =>
        {
            ShortcutRecordMode = !ShortcutRecordMode;
        });

        BrowseFileCommand = new RelayCommand(() =>
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = "Select a file", Filter = "All files (*.*)|*.*" };
            if (ofd.ShowDialog(Application.Current.MainWindow) == true)
            {
                DisplayPath = ofd.FileName;
            }
        });

        BrowseFolderCommand = new RelayCommand(() =>
        {
            using var fbd = new System.Windows.Forms.FolderBrowserDialog { Description = "Select a folder" };
            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                DisplayPath = fbd.SelectedPath;
            }
        });
    }

    public void ParseOutputString(string output, ActionDef[]? availableActions)
    {
        bool Has(ActionKind k) => availableActions == null || Array.FindIndex(availableActions, d => d.Kind == k) >= 0;

        if (output.StartsWith("cmd:", StringComparison.Ordinal) ||
            output.StartsWith("cmdw:", StringComparison.Ordinal))
        {
            _action = ActionKind.Command;
            _cmdShow = output.StartsWith("cmdw:", StringComparison.Ordinal);
            _outputValue = output.Substring(_cmdShow ? 5 : 4);
        }
        else if (output.StartsWith("type:", StringComparison.Ordinal) && Has(ActionKind.TypeText))
        {
            _action = ActionKind.TypeText;
            _outputValue = output.Substring(5);
        }
        else if (output == "hscroll:left" && Has(ActionKind.HScrollLeft))
        {
            _action = ActionKind.HScrollLeft;
            _outputValue = "hscroll:left";
        }
        else if (output == "hscroll:right" && Has(ActionKind.HScrollRight))
        {
            _action = ActionKind.HScrollRight;
            _outputValue = "hscroll:right";
        }
        else if (output == "toggle:pause" && Has(ActionKind.TogglePause))
        {
            _action = ActionKind.TogglePause;
            _outputValue = "toggle:pause";
        }
        else if (output.StartsWith("profile:", StringComparison.Ordinal) && Has(ActionKind.SwitchProfile))
        {
            _action = ActionKind.SwitchProfile;
            _outputValue = output.Substring(8);
        }
        else if (output == "Shift+Home" && Has(ActionKind.ShiftHome))
        {
            _action = ActionKind.ShiftHome;
            _outputValue = "Shift+Home";
        }
        else if (output == "Shift+End" && Has(ActionKind.ShiftEnd))
        {
            _action = ActionKind.ShiftEnd;
            _outputValue = "Shift+End";
        }
        else if (output == "Ctrl+Shift+Left" && Has(ActionKind.CtrlShiftLeft))
        {
            _action = ActionKind.CtrlShiftLeft;
            _outputValue = "Ctrl+Shift+Left";
        }
        else if (output == "Ctrl+Shift+Right" && Has(ActionKind.CtrlShiftRight))
        {
            _action = ActionKind.CtrlShiftRight;
            _outputValue = "Ctrl+Shift+Right";
        }
        else if (output.StartsWith("open:", StringComparison.Ordinal))
        {
            var p = output.Substring(5);
            bool isFolder = false;
            try { isFolder = Directory.Exists(p); } catch { }
            if (isFolder)
            {
                _action = ActionKind.OpenFolder;
            }
            else if (Apps.Any(a => string.Equals(a.Path, p, StringComparison.OrdinalIgnoreCase)))
            {
                _action = ActionKind.OpenApp;
            }
            else
            {
                _action = ActionKind.OpenFile;
            }
            _outputValue = output;
        }
        else
        {
            _action = ActionKind.Shortcut;
            _outputValue = output;
        }

        OnPropertyChanged(nameof(Action));
        OnPropertyChanged(nameof(OutputValue));
        OnPropertyChanged(nameof(DisplayPath));
        OnPropertyChanged(nameof(SelectedApp));
        OnPropertyChanged(nameof(SelectedProfileName));
        OnPropertyChanged(nameof(CmdShow));
        OnPropertyChanged(nameof(DisplayRecordText));
    }
}

public class RowViewModel : ViewModelBase
{
    private bool _isGlobal = true;
    public bool IsGlobal
    {
        get => _isGlobal;
        set
        {
            if (SetProperty(ref _isGlobal, value))
            {
                UpdateAppScopeOptions();
                UpdateAppScopeLabel();
                _markDirty();
            }
        }
    }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                _markDirty();
            }
        }
    }

    private bool _debounce;
    public bool Debounce
    {
        get => _debounce;
        set
        {
            if (SetProperty(ref _debounce, value))
            {
                _markDirty();
            }
        }
    }

    private bool _showToast;
    public bool ShowToast
    {
        get => _showToast;
        set
        {
            if (SetProperty(ref _showToast, value))
            {
                _markDirty();
            }
        }
    }

    private string _noteLabel = "";
    public string NoteLabel
    {
        get => _noteLabel;
        set
        {
            if (SetProperty(ref _noteLabel, value))
            {
                _markDirty();
            }
        }
    }

    private int _outputDelay;
    public int OutputDelay
    {
        get => _outputDelay;
        set
        {
            if (SetProperty(ref _outputDelay, value))
            {
                // Sync to all subsequent steps in the chain (index >= 1)
                for (int i = 1; i < Chain.Count; i++)
                {
                    Chain[i].Delay = value;
                }
                _markDirty();
            }
        }
    }

    public ObservableCollection<ChainedActionViewModel> Chain { get; } = new();

    public List<string> Apps { get; } = new();
    public List<string> ExceptApps { get; } = new();

    private string _appScopeLabel = "Global";
    public string AppScopeLabel
    {
        get => _appScopeLabel;
        set => SetProperty(ref _appScopeLabel, value);
    }

    public ObservableCollection<AppScopeOptionViewModel> AppScopeOptions { get; } = new();

    public bool IsMouseTrigger { get; }
    public bool DebounceVisible { get; }
    public bool HasAppScope { get; }
    public ActionDef[] AvailableActions { get; }

    public ICommand AddActionCommand { get; }
    public ICommand DeleteRowCommand { get; }
    public ICommand ToggleGlobalCommand { get; }

    private readonly Action _markDirty;
    private readonly List<AppEntry> _allApps;
    private readonly ObservableCollection<ProfileViewModel> _profiles;
    private readonly Action<ChainedActionViewModel> _recordAction;

    public RowViewModel(Action markDirty, List<AppEntry> allApps, ObservableCollection<ProfileViewModel> profiles,
                        Action<ChainedActionViewModel> recordAction, Action<RowViewModel> deleteRow,
                        bool isMouseTrigger, string? gesture, bool hasAppScope, ActionDef[] availableActions)
    {
        _markDirty = markDirty;
        _allApps = allApps;
        _profiles = profiles;
        _recordAction = recordAction;
        IsMouseTrigger = isMouseTrigger;
        HasAppScope = hasAppScope;
        AvailableActions = availableActions;

        if (isMouseTrigger && gesture != null)
        {
            DebounceVisible = gesture.Contains("scroll");
        }

        AddActionCommand = new RelayCommand(() =>
        {
            var item = new ChainedActionViewModel(_markDirty, _allApps, _profiles, DeleteActionStep, _recordAction);
            Chain.Add(item);
            UpdateMultiStepFlags();
            _markDirty();
        });

        DeleteRowCommand = new RelayCommand(() => deleteRow(this));

        ToggleGlobalCommand = new RelayCommand(() =>
        {
            IsGlobal = !IsGlobal;
        });
    }

    public void DeleteActionStep(ChainedActionViewModel step)
    {
        if (Chain.Count > 1)
        {
            Chain.Remove(step);
            UpdateMultiStepFlags();
            _markDirty();
        }
    }

    public void UpdateMultiStepFlags()
    {
        bool multi = Chain.Count > 1;
        foreach (var item in Chain)
        {
            item.IsMultiStep = multi;
        }
    }

    public void InitializeChain(List<string> outputs, List<int>? outputDelays, int outputDelay)
    {
        Chain.Clear();
        var effectiveDelays = GetEffectiveDelays(outputs, outputDelays, outputDelay);
        for (int i = 0; i < outputs.Count; i++)
        {
            var item = new ChainedActionViewModel(_markDirty, _allApps, _profiles, DeleteActionStep, _recordAction);
            item.ParseOutputString(outputs[i], AvailableActions);
            if (i < effectiveDelays.Count)
                item.Delay = effectiveDelays[i];
            Chain.Add(item);
        }
        if (Chain.Count == 0)
        {
            Chain.Add(new ChainedActionViewModel(_markDirty, _allApps, _profiles, DeleteActionStep, _recordAction));
        }
        UpdateMultiStepFlags();
    }

    private List<int> GetEffectiveDelays(List<string> outputs, List<int>? outputDelays, int outputDelay)
    {
        var result = new List<int>();
        int count = outputs.Count > 0 ? outputs.Count : 1;
        for (int i = 0; i < count; i++)
        {
            if (outputDelays != null && i < outputDelays.Count)
                result.Add(outputDelays[i]);
            else
                result.Add(i == 0 ? 0 : outputDelay);
        }
        return result;
    }

    public void InitializeAppScope(bool isGlobal, List<string> apps, List<string>? exceptApps)
    {
        _isGlobal = isGlobal;
        Apps.Clear();
        Apps.AddRange(apps);
        ExceptApps.Clear();
        ExceptApps.AddRange(exceptApps ?? new List<string>());

        UpdateAppScopeOptions();
        UpdateAppScopeLabel();
    }

    public void UpdateAppScopeOptions()
    {
        AppScopeOptions.Clear();
        var activeList = IsGlobal ? ExceptApps : Apps;
        var selectedSet = new HashSet<string>(activeList, StringComparer.OrdinalIgnoreCase);

        var entriesByProc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _allApps)
        {
            var procName = AppScanner.ResolveProcessName(app.Path);
            if (string.IsNullOrEmpty(procName)) continue;
            if (!entriesByProc.ContainsKey(procName)) entriesByProc[procName] = app.Name;
        }

        foreach (var stored in activeList)
            if (!entriesByProc.ContainsKey(stored)) entriesByProc[stored] = stored;

        foreach (var pair in entriesByProc.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
        {
            var procName = pair.Key;
            var display = pair.Value;
            var option = new AppScopeOptionViewModel(display, procName, selectedSet.Contains(procName));
            option.SelectionChanged = () =>
            {
                var list = IsGlobal ? ExceptApps : Apps;
                if (option.IsChecked)
                {
                    if (!list.Contains(procName, StringComparer.OrdinalIgnoreCase)) list.Add(procName);
                }
                else
                {
                    list.RemoveAll(a => string.Equals(a, procName, StringComparison.OrdinalIgnoreCase));
                }
                UpdateAppScopeLabel();
                _markDirty();
            };
            AppScopeOptions.Add(option);
        }
    }

    public void UpdateAppScopeLabel()
    {
        var list = IsGlobal ? ExceptApps : Apps;
        if (list.Count == 0)
        {
            AppScopeLabel = IsGlobal ? "Global" : "No Apps";
        }
        else
        {
            var prefix = IsGlobal ? "Except: " : "Only: ";
            AppScopeLabel = prefix + string.Join(", ", list);
        }
    }
}

public class MouseTriggerGroupViewModel : ViewModelBase
{
    public string Gesture { get; }
    public string Label { get; }
    public ObservableCollection<RowViewModel> Variants { get; } = new();

    public ICommand AddVariantCommand { get; }

    private readonly Action _markDirty;
    private readonly List<AppEntry> _allApps;
    private readonly ObservableCollection<ProfileViewModel> _profiles;
    private readonly Action<ChainedActionViewModel> _recordAction;

    public MouseTriggerGroupViewModel(MouseGestureDef def, Action markDirty, List<AppEntry> allApps,
                                       ObservableCollection<ProfileViewModel> profiles, Action<ChainedActionViewModel> recordAction)
    {
        Gesture = def.Gesture;
        Label = def.Label;
        _markDirty = markDirty;
        _allApps = allApps;
        _profiles = profiles;
        _recordAction = recordAction;

        AddVariantCommand = new RelayCommand(() =>
        {
            var row = new RowViewModel(_markDirty, _allApps, _profiles, _recordAction, DeleteVariant,
                                       isMouseTrigger: true, Gesture, hasAppScope: true, MainWindowViewModel.BuildActionsForGesture(def));
            row.InitializeChain(new List<string> { "" }, null, 0);
            row.InitializeAppScope(isGlobal: false, new List<string>(), new List<string>());
            Variants.Add(row);
            _markDirty();
        });
    }

    public void DeleteVariant(RowViewModel row)
    {
        Variants.Remove(row);
        _markDirty();
    }
}

public class KbdTriggerCardViewModel : ViewModelBase
{
    private string _trigger = "";
    public string Trigger
    {
        get => _trigger;
        set
        {
            if (SetProperty(ref _trigger, value))
            {
                OnPropertyChanged(nameof(DisplayTrigger));
                _markDirty();
            }
        }
    }

    public string DisplayTrigger
    {
        get
        {
            if (IsRecording)
            {
                return string.IsNullOrEmpty(RecordingText) ? "● Press keys..." : "● " + RecordingText;
            }
            return Trigger.StartsWith("key:", StringComparison.Ordinal) ? Trigger.Substring(4) : (string.IsNullOrEmpty(Trigger) ? "Record shortcut..." : Trigger);
        }
    }

    private bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(DisplayTrigger));
            }
        }
    }

    private string _recordingText = "";
    public string RecordingText
    {
        get => _recordingText;
        set
        {
            if (SetProperty(ref _recordingText, value))
            {
                OnPropertyChanged(nameof(DisplayTrigger));
            }
        }
    }

    public ObservableCollection<RowViewModel> Variants { get; } = new();

    public ICommand AddVariantCommand { get; }
    public ICommand DeleteCardCommand { get; }
    public ICommand RecordCommand { get; }

    private readonly Action _markDirty;
    private readonly List<AppEntry> _allApps;
    private readonly ObservableCollection<ProfileViewModel> _profiles;
    private readonly Action<ChainedActionViewModel> _recordAction;

    public KbdTriggerCardViewModel(string trigger, Action markDirty, List<AppEntry> allApps,
                                   ObservableCollection<ProfileViewModel> profiles, Action<ChainedActionViewModel> recordAction,
                                   Action<KbdTriggerCardViewModel> deleteCard, Action<KbdTriggerCardViewModel> recordCard)
    {
        _trigger = trigger;
        _markDirty = markDirty;
        _allApps = allApps;
        _profiles = profiles;
        _recordAction = recordAction;

        AddVariantCommand = new RelayCommand(() =>
        {
            var row = new RowViewModel(_markDirty, _allApps, _profiles, _recordAction, DeleteVariant,
                                       isMouseTrigger: false, null, hasAppScope: true, MainWindowViewModel.StandardActions);
            row.InitializeChain(new List<string> { "" }, null, 0);
            row.InitializeAppScope(isGlobal: false, new List<string>(), new List<string>());
            Variants.Add(row);
            _markDirty();
        });

        DeleteCardCommand = new RelayCommand(() => deleteCard(this));
        RecordCommand = new RelayCommand(() => recordCard(this));
    }

    public void DeleteVariant(RowViewModel row)
    {
        Variants.Remove(row);
        _markDirty();
    }
}

public class AppTriggerCardViewModel : ViewModelBase
{
    private string _kind = "launch";
    public string Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                _markDirty();
            }
        }
    }

    public int SelectedKindIndex
    {
        get => Kind == "exit" ? 1 : Kind == "focus" ? 2 : Kind == "blur" ? 3 : 0;
        set
        {
            Kind = value == 1 ? "exit" : value == 2 ? "focus" : value == 3 ? "blur" : "launch";
        }
    }

    public ObservableCollection<string> SelectedApps { get; } = new();

    private string _appsLabel = "No apps selected";
    public string AppsLabel
    {
        get => _appsLabel;
        set => SetProperty(ref _appsLabel, value);
    }

    public ObservableCollection<AppScopeOptionViewModel> AppOptions { get; } = new();

    public RowViewModel Row { get; }

    private string _customAppInput = "";
    public string CustomAppInput
    {
        get => _customAppInput;
        set => SetProperty(ref _customAppInput, value);
    }

    public ICommand DeleteCardCommand { get; }
    public ICommand AddCustomAppCommand { get; }

    private readonly Action _markDirty;
    private readonly List<AppEntry> _allApps;

    public AppTriggerCardViewModel(Action markDirty, List<AppEntry> allApps, ObservableCollection<ProfileViewModel> profiles,
                                   Action<ChainedActionViewModel> recordAction, Action<AppTriggerCardViewModel> deleteCard)
    {
        _markDirty = markDirty;
        _allApps = allApps;

        Row = new RowViewModel(_markDirty, _allApps, profiles, recordAction, _ => {},
                             isMouseTrigger: false, null, hasAppScope: false, MainWindowViewModel.StandardActions);

        DeleteCardCommand = new RelayCommand(() => deleteCard(this));

        AddCustomAppCommand = new RelayCommand(() =>
        {
            var name = CustomAppInput.Trim();
            if (string.IsNullOrEmpty(name)) return;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";

            if (!SelectedApps.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                SelectedApps.Add(name);
                UpdateAppOptionsSelection();
                UpdateAppsLabel();
                _markDirty();
            }
            CustomAppInput = "";
        });
    }

    public void InitializeSelectedApps(List<string> apps)
    {
        SelectedApps.Clear();
        foreach (var app in apps) SelectedApps.Add(app);

        UpdateAppOptionsSelection();
        UpdateAppsLabel();
    }

    public void UpdateAppOptionsSelection()
    {
        AppOptions.Clear();
        var selectedSet = new HashSet<string>(SelectedApps, StringComparer.OrdinalIgnoreCase);

        var entriesByProc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _allApps)
        {
            var procName = AppScanner.ResolveProcessName(app.Path);
            if (string.IsNullOrEmpty(procName)) continue;
            if (!entriesByProc.ContainsKey(procName)) entriesByProc[procName] = app.Name;
        }

        foreach (var stored in SelectedApps)
            if (!entriesByProc.ContainsKey(stored)) entriesByProc[stored] = stored;

        foreach (var pair in entriesByProc.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
        {
            var procName = pair.Key;
            var display = pair.Value;
            var option = new AppScopeOptionViewModel(display, procName, selectedSet.Contains(procName));
            option.SelectionChanged = () =>
            {
                if (option.IsChecked)
                {
                    if (!SelectedApps.Contains(procName, StringComparer.OrdinalIgnoreCase))
                        SelectedApps.Add(procName);
                }
                else
                {
                    for (int i = SelectedApps.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(SelectedApps[i], procName, StringComparison.OrdinalIgnoreCase))
                            SelectedApps.RemoveAt(i);
                    }
                }
                UpdateAppsLabel();
                _markDirty();
            };
            AppOptions.Add(option);
        }
    }

    public void UpdateAppsLabel()
    {
        if (SelectedApps.Count == 0)
        {
            AppsLabel = "No apps selected";
        }
        else
        {
            AppsLabel = string.Join(", ", SelectedApps);
        }
    }
}

public class ActionToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is ActionKind action && parameter is string param)
        {
            if (param == "OpenFileOrFolder")
                return (action == ActionKind.OpenFile || action == ActionKind.OpenFolder) ? Visibility.Visible : Visibility.Collapsed;

            if (Enum.TryParse<ActionKind>(param, out var targetAction))
                return action == targetAction ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBooleanToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return (value is bool b && !b) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

public class BooleanToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

public class RecordModeTextConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return (value is bool b && b) ? "Aa" : "⌨";
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

