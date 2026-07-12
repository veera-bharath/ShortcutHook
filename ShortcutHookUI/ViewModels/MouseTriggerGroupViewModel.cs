using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ShortcutHookCore.Models;
using ShortcutHookUI.Models;

namespace ShortcutHookUI.ViewModels;

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
