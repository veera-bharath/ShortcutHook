using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ShortcutHookCore.Models;

namespace ShortcutHookUI.ViewModels;

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
