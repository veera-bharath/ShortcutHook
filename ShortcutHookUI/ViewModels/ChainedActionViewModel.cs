using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using ShortcutHookCore.Models;
using ShortcutHookUI.Models;

namespace ShortcutHookUI.ViewModels;

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
